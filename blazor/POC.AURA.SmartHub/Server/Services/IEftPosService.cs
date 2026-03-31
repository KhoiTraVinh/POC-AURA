namespace POC.AURA.SmartHub.Server.Services;

public record EftPosRequest(
    string TransactionId,
    string Description,
    decimal Amount,
    string Currency,
    DateTime SubmittedAt,
    int ConnectionId
);

public record EftPosResult(
    string TransactionId,
    bool   Success,
    string Message
);

public interface IEftPosService
{
    /// <summary>
    /// Process an EFT/POS transaction.
    /// Real: communicate with physical EFT terminal.
    /// POC: simulate 3–7 s delay with 15% failure rate.
    /// </summary>
    Task<EftPosResult> DoEftPosTransactionAsync(EftPosRequest request, CancellationToken ct = default);
}
