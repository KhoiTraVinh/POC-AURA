using POC.AURA.Api.Models;

namespace POC.AURA.Api.Services;

public interface ITransactionQueueService
{
    /// <summary>
    /// Try to submit a bank transaction.
    /// The bank is a GLOBAL resource — only one transaction at a time across ALL tenants.
    /// Fails immediately (fail-fast) if the bank is busy.
    /// </summary>
    Task<TransactionSubmitResult> TrySubmitAsync(string tenantId, TransactionRequest request, string connectionId);

    /// <summary>
    /// Called from HTTP API when SmartHub reports transaction completion.
    /// Releases the global lock and broadcasts updated status to all connected UI clients.
    /// </summary>
    Task CompleteTransactionAsync(string tenantId, CompleteTransactionRequest req);

    /// <summary>Global bank status — same view for all tenants.</summary>
    TransactionHistoryStatus GetStatus();
}
