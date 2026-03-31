using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using POC.AURA.Api.Common.Constants;
using POC.AURA.Api.Common.Models;
using POC.AURA.Api.Data.Repositories;
using POC.AURA.Api.Server.Hubs;

namespace POC.AURA.Api.Service;

/// <summary>
/// Global bank transaction processor with fail-fast pessimistic locking.
/// <para>
/// The bank is a <b>single shared resource across ALL tenants</b> — only ONE
/// transaction can be in-flight at any time, regardless of which tenant submitted it.
/// </para>
/// <para>
/// Design decisions:<br/>
/// - <b>Fail-fast</b>: <see cref="TrySubmitAsync"/> never blocks the caller. If the
///   global lock cannot be acquired in 0 ms the request is immediately rejected.<br/>
/// - <b>Fire-and-forget</b>: DB persistence and SmartHub routing run in the background
///   after the lock is acquired, so the HTTP/SignalR response returns quickly.<br/>
/// - <b>Lock released before broadcast</b>: allows the next submission to succeed
///   while status events are still in flight to UI clients.
/// </para>
/// </summary>
public sealed class TransactionQueueService : ITransactionQueueService
{
    // ── State ──────────────────────────────────────────────────────────────
    private readonly SemaphoreSlim _globalLock = new(1, 1);
    private readonly object _currentSync = new();
    private TransactionStatus? _current;

    private readonly ConcurrentQueue<TransactionStatus> _history = new();
    private const int MaxHistory = 50;

    // ── Dependencies ───────────────────────────────────────────────────────
    private readonly IHubContext<AuraHub> _hub;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TransactionQueueService> _logger;

    public TransactionQueueService(
        IHubContext<AuraHub> hub,
        IServiceScopeFactory scopeFactory,
        ILogger<TransactionQueueService> logger)
    {
        _hub = hub;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // ── ITransactionQueueService ───────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<TransactionSubmitResult> TrySubmitAsync(
        string tenantId, TransactionRequest request, string connectionId)
    {
        if (!_globalLock.Wait(0))
        {
            TransactionStatus? current;
            lock (_currentSync) current = _current;

            var reason = current is not null
                ? $"Bank đang xử lý [{current.Id}] \"{current.Description}\". Vui lòng thử lại."
                : "Bank đang bận. Vui lòng thử lại sau.";

            _logger.LogInformation("[Bank] REJECTED {Tenant}: bank busy (current: {Id})",
                tenantId, current?.Id ?? "unknown");

            return new TransactionSubmitResult(null, "rejected", reason, current);
        }

        var id = GenerateId();
        var status = new TransactionStatus(id, "processing", request.Description,
            null, DateTime.UtcNow, null);

        lock (_currentSync) _current = status;

        _logger.LogInformation("[Bank] ACCEPTED {Tenant}: TXN-{Id}", tenantId, id);

        _ = SaveAndForwardAsync(tenantId, id, request, connectionId);

        return new TransactionSubmitResult(
            id, "accepted",
            $"Transaction [{id}] accepted — routing to bank processor.",
            status);
    }

    /// <inheritdoc/>
    public async Task CompleteTransactionAsync(string tenantId, CompleteTransactionRequest req)
    {
        TransactionStatus? current;
        lock (_currentSync) current = _current;

        if (current is null || current.Id != req.TransactionId)
        {
            _logger.LogWarning("[Bank] Complete called for unknown TXN-{Id}", req.TransactionId);
            return;
        }

        var state = req.Success ? JobStatuses.Completed : JobStatuses.Failed;
        var finished = new TransactionStatus(
            current.Id, state, current.Description,
            req.Message, current.SubmittedAt, DateTime.UtcNow);

        _history.Enqueue(finished);
        while (_history.Count > MaxHistory) _history.TryDequeue(out _);

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        await repo.CompleteAsync(req.TransactionId, tenantId, MessageTypes.BankTransaction,
            req.Success, req.Message ?? "");

        _logger.LogInformation("[Bank] TXN-{Id} {State} (tenant: {Tenant})", current.Id, state, tenantId);

        lock (_currentSync) _current = null;
        _globalLock.Release();
        _logger.LogInformation("[Bank] Global lock released");

        await BroadcastEventAsync(current.Id, state, req.Message);
        await BroadcastStatusAsync();
    }

    /// <inheritdoc/>
    public TransactionHistoryStatus GetStatus() =>
        new(_globalLock.CurrentCount == 0, _current, _history.ToArray());

    // ── Private ────────────────────────────────────────────────────────────

    private async Task SaveAndForwardAsync(
        string tenantId, string id, TransactionRequest request, string connectionId)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                description = request.Description,
                amount = request.Amount,
                currency = request.Currency
            });

            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            await repo.SaveAsync(tenantId, MessageTypes.BankTransaction, id, payload, connectionId);

            await BroadcastEventAsync(id, "processing",
                $"[{tenantId}] Processing: {request.Description} ({request.Amount:N0} {request.Currency})");
            await BroadcastStatusAsync();

            await _hub.Clients.Group(HubGroups.Bank(tenantId)).SendAsync(HubEvents.ExecuteTransaction, new
            {
                TransactionId = id,
                request.Description,
                request.Amount,
                request.Currency,
                SubmittedAt = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Bank] Failed to forward TXN-{Id}", id);

            lock (_currentSync) _current = null;
            _globalLock.Release();

            await BroadcastEventAsync(id, JobStatuses.Failed, "Bank processor unavailable");
            await BroadcastStatusAsync();
        }
    }

    private Task BroadcastEventAsync(string id, string state, string? message) =>
        _hub.Clients.Group(HubGroups.UiBroadcast).SendAsync(HubEvents.TransactionStatusChanged,
            new { Id = id, State = state, Message = message });

    private Task BroadcastStatusAsync() =>
        _hub.Clients.Group(HubGroups.UiBroadcast).SendAsync(HubEvents.BankStatus, GetStatus());

    private static string GenerateId() =>
        Guid.NewGuid().ToString("N")[..10].ToUpper();
}
