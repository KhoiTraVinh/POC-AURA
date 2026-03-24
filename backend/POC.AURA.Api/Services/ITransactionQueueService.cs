using POC.AURA.Api.Models;

namespace POC.AURA.Api.Services;

public interface ITransactionQueueService
{
    /// <summary>
    /// Try to submit a bank transaction for a specific tenant.
    /// Fails immediately if that tenant's bank is busy.
    /// </summary>
    Task<TransactionSubmitResult> TrySubmitAsync(string tenantId, TransactionRequest request, string connectionId);

    /// <summary>
    /// Called from HTTP API when SmartHub reports transaction completion.
    /// Releases the per-tenant lock and broadcasts result to Angular.
    /// </summary>
    Task CompleteTransactionAsync(string tenantId, CompleteTransactionRequest req);

    TransactionHistoryStatus GetStatus(string tenantId);
}
