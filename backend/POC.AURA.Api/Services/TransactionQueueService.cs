using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using POC.AURA.Api.Data;
using POC.AURA.Api.Entities;
using POC.AURA.Api.Hubs;
using POC.AURA.Api.Models;

namespace POC.AURA.Api.Services;

/// <summary>
/// Per-tenant bank transaction service with fail-fast pessimistic locking.
/// Each tenant has its own SemaphoreSlim(1,1): only one transaction per tenant at a time.
/// Lock is held until SmartHub reports completion via HTTP API.
/// </summary>
public class TransactionQueueService : ITransactionQueueService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, TransactionStatus?> _current = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<TransactionStatus>> _history = new();
    private const int MaxHistory = 20;

    private readonly IHubContext<TransactionHub> _hubContext;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TransactionQueueService> _logger;

    public TransactionQueueService(
        IHubContext<TransactionHub> hubContext,
        IServiceScopeFactory scopeFactory,
        ILogger<TransactionQueueService> logger)
    {
        _hubContext = hubContext;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<TransactionSubmitResult> TrySubmitAsync(string tenantId, TransactionRequest request, string connectionId)
    {
        var @lock = _locks.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
        var current = _current.GetValueOrDefault(tenantId);

        if (!@lock.Wait(0))
        {
            var msg = current != null
                ? $"Bank is busy processing [{current.Id}] \"{current.Description}\". Please try again."
                : "Bank is currently busy. Please try again in a moment.";
            _logger.LogInformation("[Bank:{Tenant}] REJECTED: bank busy", tenantId);
            return new TransactionSubmitResult(null, "rejected", msg, current);
        }

        var id = Guid.NewGuid().ToString("N")[..10].ToUpper();
        var status = new TransactionStatus(id, "processing", request.Description, null, DateTime.UtcNow, null);
        _current[tenantId] = status;

        _logger.LogInformation("[Bank:{Tenant}] ACCEPTED: TXN-{Id}", tenantId, id);

        // Save to DB then forward to SmartHub
        _ = SaveAndForwardAsync(tenantId, id, request, connectionId);

        return new TransactionSubmitResult(id, "accepted", $"Transaction [{id}] accepted. Routing to bank processor.", status);
    }

    public async Task CompleteTransactionAsync(string tenantId, CompleteTransactionRequest req)
    {
        var current = _current.GetValueOrDefault(tenantId);
        if (current == null || current.Id != req.TransactionId)
        {
            _logger.LogWarning("[Bank:{Tenant}] Complete called for unknown TXN-{Id}", tenantId, req.TransactionId);
            return;
        }

        var state = req.Success ? "completed" : "failed";
        var finished = new TransactionStatus(current.Id, state, current.Description, req.Message, current.SubmittedAt, DateTime.UtcNow);

        GetHistory(tenantId).Enqueue(finished);
        while (GetHistory(tenantId).Count > MaxHistory) GetHistory(tenantId).TryDequeue(out _);

        // Update DB
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = await db.BankTransactions.FindAsync(req.TransactionId);
        if (record != null)
        {
            record.Status = state;
            record.CompletedAt = DateTime.UtcNow;
            record.ResultMessage = req.Message;
            await db.SaveChangesAsync();
        }

        _logger.LogInformation("[Bank:{Tenant}] TXN-{Id} {State}", tenantId, current.Id, state);

        await _hubContext.Clients.Group($"ui-{tenantId}").SendAsync("TransactionStatusChanged", new
        {
            Id = current.Id,
            State = state,
            Message = req.Message
        });

        _current[tenantId] = null;
        _locks[tenantId].Release();

        await BroadcastStatusAsync(tenantId);
        _logger.LogInformation("[Bank:{Tenant}] Lock released", tenantId);
    }

    public TransactionHistoryStatus GetStatus(string tenantId) => new(
        _locks.TryGetValue(tenantId, out var sem) && sem.CurrentCount == 0,
        _current.GetValueOrDefault(tenantId),
        GetHistory(tenantId).ToArray()
    );

    private async Task SaveAndForwardAsync(string tenantId, string id, TransactionRequest request, string connectionId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.BankTransactions.Add(new BankTransactionRecord
            {
                Id = id,
                TenantId = tenantId,
                Description = request.Description,
                Amount = request.Amount,
                Currency = request.Currency,
                RequestorConnectionId = connectionId,
                Status = "pending",
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            await _hubContext.Clients.Group($"ui-{tenantId}").SendAsync("TransactionStatusChanged", new
            {
                Id = id, State = "processing",
                Message = $"Bank processing: {request.Description} ({request.Amount:N0} {request.Currency})"
            });
            await BroadcastStatusAsync(tenantId);

            await _hubContext.Clients.Group($"bank-{tenantId}").SendAsync("ExecuteTransaction", new
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
            _logger.LogError(ex, "[Bank:{Tenant}] Failed to forward TXN-{Id}", tenantId, id);
            _current[tenantId] = null;
            _locks[tenantId].Release();

            await _hubContext.Clients.Group($"ui-{tenantId}").SendAsync("TransactionStatusChanged", new
            {
                Id = id, State = "failed",
                Message = "Bank processor unavailable"
            });
            await BroadcastStatusAsync(tenantId);
        }
    }

    private Task BroadcastStatusAsync(string tenantId) =>
        _hubContext.Clients.Group($"ui-{tenantId}").SendAsync("BankStatus", GetStatus(tenantId));

    private ConcurrentQueue<TransactionStatus> GetHistory(string tenantId) =>
        _history.GetOrAdd(tenantId, _ => new ConcurrentQueue<TransactionStatus>());
}
