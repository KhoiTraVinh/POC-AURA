using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using POC.AURA.Api.Models;
using POC.AURA.Api.Services;

namespace POC.AURA.Api.Hubs;

/// <summary>
/// Multi-tenant bank transaction hub.
/// Groups: ui-{tenantId} for Angular UI clients, bank-{tenantId} for Blazor bank processor.
/// Per-tenant SemaphoreSlim(1,1): only one transaction per tenant at a time.
/// Lock held until SmartHub reports via HTTP POST /api/transaction/complete.
/// </summary>
[Authorize]
public class TransactionHub : Hub
{
    private readonly ITransactionQueueService _bank;
    private readonly ILogger<TransactionHub> _logger;

    private string TenantId => Context.User!.FindFirst("tenant_id")!.Value;
    private string ClientType => Context.User!.FindFirst("client_type")!.Value;

    public TransactionHub(ITransactionQueueService bank, ILogger<TransactionHub> logger)
    {
        _bank = bank;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var group = $"{ClientType}-{TenantId}";
        await Groups.AddToGroupAsync(Context.ConnectionId, group);

        // Send current bank status to the new client
        await Clients.Caller.SendAsync("BankStatus", _bank.GetStatus(TenantId));

        _logger.LogInformation("[TransactionHub] {ClientType} connected for {TenantId}", ClientType, TenantId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"{ClientType}-{TenantId}");
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Called by Angular UI to submit a transaction. Returns accepted/rejected synchronously.
    /// If accepted, follow progress via TransactionStatusChanged and BankStatus events.
    /// </summary>
    public async Task<TransactionSubmitResult> SubmitTransaction(TransactionRequest request)
    {
        return await _bank.TrySubmitAsync(TenantId, request, Context.ConnectionId);
    }
}
