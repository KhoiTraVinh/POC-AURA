namespace POC.AURA.Api.Models;

/// <summary>Inbound request from an Angular UI client to submit a bank transaction.</summary>
public record TransactionRequest(string Description, decimal Amount, string Currency = "VND");

/// <summary>
/// Snapshot of a bank transaction, used both while processing and in the history list.
/// </summary>
public record TransactionStatus(
    string    Id,
    string    State,        // processing | completed | failed | rejected
    string    Description,
    string?   Result,
    DateTime  SubmittedAt,
    DateTime? FinishedAt
);

/// <summary>
/// Immediate response to <see cref="ITransactionQueueService.TrySubmitAsync"/>.
/// <list type="bullet">
///   <item><c>accepted</c> — lock acquired; follow progress via <c>TransactionStatusChanged</c> SignalR events.</item>
///   <item><c>rejected</c> — bank is busy; caller should retry.</item>
/// </list>
/// </summary>
public record TransactionSubmitResult(
    string?           TransactionId,
    string            Status,                 // "accepted" | "rejected"
    string            Message,
    TransactionStatus? CurrentlyProcessing    // populated when rejected so UI can show who is blocking
);

/// <summary>
/// Full bank state snapshot pushed to all UI clients via <c>BankStatus</c>.
/// </summary>
public record TransactionHistoryStatus(
    bool                          IsBankBusy,
    TransactionStatus?            CurrentTransaction,
    IReadOnlyList<TransactionStatus> History
);

/// <summary>
/// Request body POSTed by the SmartHub to <c>/api/transaction/complete</c>.
/// </summary>
public record CompleteTransactionRequest(string TransactionId, bool Success, string Message);
