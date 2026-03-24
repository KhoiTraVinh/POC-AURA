namespace POC.AURA.Api.Models;

public record TransactionRequest(string Description, decimal Amount, string Currency = "VND");

public record TransactionWorkItem(string Id, TransactionRequest Request, string ConnectionId, DateTime SubmittedAt);

public record TransactionStatus(
    string Id,
    string State,       // processing | completed | failed | rejected
    string Description,
    string? Result,
    DateTime SubmittedAt,
    DateTime? FinishedAt
);

/// <summary>
/// Immediate response to TrySubmit call.
/// Status = "accepted": transaction is being processed, follow via SignalR.
/// Status = "rejected": bank is busy, client should retry.
/// </summary>
public record TransactionSubmitResult(
    string? TransactionId,
    string Status,          // "accepted" | "rejected"
    string Message,
    TransactionStatus? CurrentlyProcessing  // who's blocking when rejected
);

public record TransactionHistoryStatus(
    bool IsBankBusy,
    TransactionStatus? CurrentTransaction,
    IReadOnlyList<TransactionStatus> History  // last 20 completed/failed
);

/// <summary>
/// Report sent by the Blazor bank processor back to the hub upon completion.
/// </summary>
public record TransactionResult(
    string TransactionId,
    bool Success,
    string Message
);

/// <summary>Request body for SmartHub to report transaction completion via HTTP API.</summary>
public record CompleteTransactionRequest(string TransactionId, bool Success, string Message);
