using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using POC.AURA.Api.Common.Constants;
using POC.AURA.Api.Common.Extensions;
using POC.AURA.Api.Common.Models;
using POC.AURA.Api.Data.Repositories;
using POC.AURA.Api.Service;

namespace POC.AURA.Api.Server.Hubs;

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

    public override async Task OnConnectedAsync()
    {
        _tracker.Register(UserName, Context.ConnectionId);

        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.For(ClientType, TenantId));

        if (ClientType == ClientTypes.Ui)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.UiBroadcast);
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.DocAll);
            await Clients.Caller.SendAsync(HubEvents.LockSnapshot, _locks.GetAllLocks());
        }

        await Clients.Group(HubGroups.Ui(TenantId)).SendAsync(HubEvents.ClientConnected, new
        {
            ConnectionId = Context.ConnectionId,
            TenantId,
            ClientType,
            UserName,
            Timestamp = DateTime.UtcNow
        });

        await Clients.Caller.SendAsync(HubEvents.BankStatus, _bank.GetStatus());

        _logger.LogInformation("[AuraHub] {ClientType} connected for {TenantId}", ClientType, TenantId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _tracker.Unregister(Context.ConnectionId);

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.For(ClientType, TenantId));

        if (ClientType == ClientTypes.Ui)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.UiBroadcast);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.DocAll);

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
    /// Persists a new print job and routes it to the SmartHub processor for this tenant.
    /// </summary>
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
    /// Called by Angular on reconnect to recover completion status for jobs processed
    /// while disconnected.
    /// </summary>
    public async Task SyncPrintJobs(string[] jobIds)
    {
        foreach (var jobId in jobIds)
        {
            var message = await _jobs.FindByRefAsync(jobId, TenantId, MessageTypes.PrintJob);
            if (message is null || message.Status == JobStatuses.Pending) continue;

            var result = new PrintJobResult(
                message.Ref,
                TenantId,
                message.RequestorConnectionId ?? "",
                message.Status == JobStatuses.Completed,
                message.ResultMessage ?? "",
                message.CompletedAt ?? DateTime.UtcNow);

            await Clients.Caller.SendAsync(HubEvents.PrintJobComplete, result);
        }
    }

    // ── Bank ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to acquire the global bank lock and begin processing a transaction.
    /// </summary>
    public Task<TransactionSubmitResult> SubmitTransaction(TransactionRequest request) =>
        _bank.TrySubmitAsync(TenantId, request, Context.ConnectionId);

    // ── Collaborative Document ────────────────────────────────────────────────

    /// <summary>
    /// Tries to acquire an exclusive edit lock on a document field.
    /// </summary>
    public async Task<LockAcquireResult> AcquireFieldLock(string docId, string fieldId)
    {
        var result = _locks.TryAcquire(docId, fieldId, UserName, UserName, Context.ConnectionId);

        if (result.Acquired)
        {
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
    /// Must be called every ~8s while the user is actively editing; lock TTL is 30s.
    /// </summary>
    public void HeartbeatFieldLock(string docId, string fieldId)
    {
        _locks.Heartbeat(docId, fieldId, UserName);
    }

    /// <summary>
    /// Broadcasts a real-time field value change to all other UI clients.
    /// The caller must hold the lock.
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

    private static string GenerateId() =>
        Guid.NewGuid().ToString("N")[..10].ToUpper();
}
