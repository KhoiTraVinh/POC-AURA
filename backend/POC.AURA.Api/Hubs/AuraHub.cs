using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using POC.AURA.Api.Constants;
using POC.AURA.Api.Extensions;
using POC.AURA.Api.Models;
using POC.AURA.Api.Repositories;
using POC.AURA.Api.Services;
using IConnectionTracker = POC.AURA.Api.Services.IConnectionTracker;

namespace POC.AURA.Api.Hubs;

/// <summary>
/// Unified multi-tenant hub for print jobs, bank transactions, and collaborative document editing.
/// <para>
/// <b>Groups managed per connection:</b><br/>
/// - <c>ui-{tenantId}</c>       — Angular browser clients (per-tenant)<br/>
/// - <c>smarthub-{tenantId}</c> — Blazor SmartHub print processors<br/>
/// - <c>bank-{tenantId}</c>     — Blazor SmartHub bank processors<br/>
/// - <c>ui-broadcast</c>        — All UI clients (global bank status broadcasts)<br/>
/// - <c>doc-all</c>             — All UI clients (collaborative document lock events)
/// </para>
/// <para>
/// All jobs are persisted to the <c>Messages</c> table via <see cref="IJobRepository"/>.
/// </para>
/// </summary>
[Authorize]
public class AuraHub : Hub
{
    private readonly IJobRepository           _jobs;
    private readonly ITransactionQueueService _bank;
    private readonly IDocumentLockService     _locks;
    private readonly IConnectionTracker       _tracker;
    private readonly ILogger<AuraHub>         _logger;

    // Shorthand claim accessors
    private string TenantId   => Context.User!.GetTenantId();
    private string ClientType => Context.User!.GetClientType();
    private string UserName   => Context.User!.GetUserName();

    public AuraHub(
        IJobRepository           jobs,
        ITransactionQueueService bank,
        IDocumentLockService     locks,
        IConnectionTracker       tracker,
        ILogger<AuraHub>         logger)
    {
        _jobs    = jobs;
        _bank    = bank;
        _locks   = locks;
        _tracker = tracker;
        _logger  = logger;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public override async Task OnConnectedAsync()
    {
        // Track userId → connectionId so completion notifications can be
        // routed to the user's current connection even after a reconnect.
        _tracker.Register(UserName, Context.ConnectionId);

        // Join the per-type-per-tenant group (e.g. "ui-TenantA")
        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.For(ClientType, TenantId));

        if (ClientType == ClientTypes.Ui)
        {
            // Join the global broadcast group for bank status updates
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.UiBroadcast);

            // Join the global doc-all group for collaborative document lock events
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.DocAll);

            // Send a snapshot of all active field locks so the client is up to date immediately
            await Clients.Caller.SendAsync(HubEvents.LockSnapshot, _locks.GetAllLocks());
        }

        // Announce connection to all UI clients in the same tenant
        await Clients.Group(HubGroups.Ui(TenantId)).SendAsync(HubEvents.ClientConnected, new
        {
            ConnectionId = Context.ConnectionId,
            TenantId,
            ClientType,
            UserName,
            Timestamp = DateTime.UtcNow
        });

        // Send current global bank state so the connecting client is up to date immediately
        await Clients.Caller.SendAsync(HubEvents.BankStatus, _bank.GetStatus());

        _logger.LogInformation("[AuraHub] {ClientType} connected for {TenantId}", ClientType, TenantId);
        await base.OnConnectedAsync();
    }

    /// <inheritdoc/>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _tracker.Unregister(Context.ConnectionId);

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.For(ClientType, TenantId));

        if (ClientType == ClientTypes.Ui)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.UiBroadcast);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.DocAll);

            // Auto-release all document field locks held by this connection
            var released = _locks.ReleaseAllByConnection(Context.ConnectionId);
            foreach (var fieldLock in released)
            {
                await Clients.Group(HubGroups.DocAll).SendAsync(HubEvents.FieldUnlocked, new
                {
                    fieldLock.DocId,
                    fieldLock.FieldId
                });
            }
        }

        await Clients.Group(HubGroups.Ui(TenantId)).SendAsync(HubEvents.ClientDisconnected, new
        {
            ConnectionId = Context.ConnectionId,
            ClientType,
            TenantId
        });

        await base.OnDisconnectedAsync(exception);
    }

    // ── Print ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Persists a new print job to the <c>Messages</c> table and routes it to the
    /// SmartHub processor registered for this tenant.
    /// Emits <see cref="HubEvents.PrintJobQueued"/> back to the caller as confirmation.
    /// </summary>
    /// <exception cref="HubException">Thrown when the database write fails so the client receives a meaningful error.</exception>
    public async Task SubmitPrintJob(PrintJobRequest request)
    {
        var id      = GenerateId();
        var docName = request.DocumentName ?? "Untitled";
        var content = request.Content ?? string.Empty;
        var copies  = Math.Max(1, request.Copies);

        var payload = JsonSerializer.Serialize(new { documentName = docName, content, copies });

        try
        {
            await _jobs.SaveAsync(TenantId, MessageTypes.PrintJob, id, payload, Context.ConnectionId, UserName);
        }
        catch (DbUpdateException ex)
        {
            var msg = ex.InnerException?.Message ?? ex.Message;
            _logger.LogError(ex, "[AuraHub] DB write failed for print job: {Msg}", msg);
            throw new HubException($"DB save failed: {msg}");
        }

        var job = new PrintJob(id, TenantId, docName, content, copies, Context.ConnectionId, DateTime.UtcNow);

        await Clients.Group(HubGroups.SmartHub(TenantId)).SendAsync(HubEvents.ExecutePrintJob, job);
        await Clients.Caller.SendAsync(HubEvents.PrintJobQueued, job);

        _logger.LogInformation("[AuraHub] PrintJob {Id} queued for {TenantId}", id, TenantId);
    }

    // ── Print — Resync ───────────────────────────────────────────────────────

    /// <summary>
    /// Called by Angular on reconnect to recover completion status for jobs that
    /// were processed while the client was disconnected or hadn't joined its group yet.
    /// <para>
    /// For each <paramref name="jobIds"/> entry that is no longer <c>pending</c> in the DB,
    /// a <see cref="HubEvents.PrintJobComplete"/> event is pushed back to the caller only.
    /// Jobs still pending are silently skipped.
    /// </para>
    /// </summary>
    public async Task SyncPrintJobs(string[] jobIds)
    {
        foreach (var jobId in jobIds)
        {
            var message = await _jobs.FindByRefAsync(jobId, TenantId, MessageTypes.PrintJob);
            if (message is null || message.Status == Constants.JobStatuses.Pending) continue;

            var result = new PrintJobResult(
                message.Ref,
                TenantId,
                message.RequestorConnectionId ?? "",
                message.Status == Constants.JobStatuses.Completed,
                message.ResultMessage ?? "",
                message.CompletedAt ?? DateTime.UtcNow);

            await Clients.Caller.SendAsync(HubEvents.PrintJobComplete, result);
        }
    }

    // ── Bank ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to acquire the global bank lock and begin processing a transaction.
    /// </summary>
    /// <returns>
    /// <c>accepted</c> if the lock was acquired and the job is being processed, or
    /// <c>rejected</c> if the bank is currently busy — the caller should retry.
    /// </returns>
    public Task<TransactionSubmitResult> SubmitTransaction(TransactionRequest request) =>
        _bank.TrySubmitAsync(TenantId, request, Context.ConnectionId);

    // ── Collaborative Document ────────────────────────────────────────────────

    /// <summary>
    /// Tries to acquire an exclusive edit lock on a document field.
    /// Returns the result immediately (acquired/rejected) so the caller can
    /// update its UI without waiting for a broadcast.
    /// On success, broadcasts <see cref="HubEvents.FieldLocked"/> to all other UI clients.
    /// </summary>
    public async Task<LockAcquireResult> AcquireFieldLock(string docId, string fieldId)
    {
        var result = _locks.TryAcquire(docId, fieldId, UserName, UserName, Context.ConnectionId);

        if (result.Acquired)
        {
            // Notify every other UI client that this field is now locked
            await Clients.GroupExcept(HubGroups.DocAll, Context.ConnectionId)
                .SendAsync(HubEvents.FieldLocked, new
                {
                    DocId    = docId,
                    FieldId  = fieldId,
                    UserId   = UserName,
                    UserName,
                    ExpiresAt = result.ExpiresAt
                });
        }

        return result;
    }

    /// <summary>
    /// Releases a field lock. Only the lock holder can release.
    /// Broadcasts <see cref="HubEvents.FieldUnlocked"/> to all UI clients on success.
    /// </summary>
    public async Task ReleaseFieldLock(string docId, string fieldId)
    {
        var released = _locks.Release(docId, fieldId, UserName);
        if (released)
        {
            await Clients.Group(HubGroups.DocAll)
                .SendAsync(HubEvents.FieldUnlocked, new { DocId = docId, FieldId = fieldId });
        }
    }

    /// <summary>
    /// Extends the TTL of an active field lock.
    /// Must be called every ~8 s while the user is actively editing; lock TTL is 30 s.
    /// </summary>
    public void HeartbeatFieldLock(string docId, string fieldId)
    {
        _locks.Heartbeat(docId, fieldId, UserName);
    }

    /// <summary>
    /// Broadcasts a real-time field value change to all other UI clients.
    /// The caller must hold the lock; throws <see cref="HubException"/> otherwise.
    /// </summary>
    public async Task UpdateFieldValue(string docId, string fieldId, string value)
    {
        var currentLock = _locks.GetLock(docId, fieldId);
        if (currentLock is null || currentLock.UserId != UserName)
            throw new HubException("Cannot update field: you don't hold the lock");

        await Clients.GroupExcept(HubGroups.DocAll, Context.ConnectionId)
            .SendAsync(HubEvents.FieldValueChanged, new
            {
                DocId    = docId,
                FieldId  = fieldId,
                Value    = value,
                UserId   = UserName,
                UserName
            });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Generates a short uppercase alphanumeric ID (10 chars).</summary>
    private static string GenerateId() =>
        Guid.NewGuid().ToString("N")[..10].ToUpper();
}
