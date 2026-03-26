namespace POC.AURA.SmartHub.Server.Services;

public class EftPosService(ILogger<EftPosService> logger) : IEftPosService
{
    public async Task<EftPosResult> DoEftPosTransactionAsync(
        EftPosRequest request, CancellationToken ct = default)
    {
        // Real: open serial/USB connection to EFT terminal, send EFTPOS protocol messages
        logger.LogInformation("Processing EFT TXN-{Id} {Amount} {Currency}",
            request.TransactionId, request.Amount, request.Currency);

        await Task.Delay(Random.Shared.Next(3_000, 7_000), ct);

        var success = Random.Shared.NextDouble() > 0.15;
        var reference = Guid.NewGuid().ToString("N")[..8].ToUpper();
        var message = success
            ? $"Approved. Bank Reference: TXN-{reference}"
            : "Declined: insufficient balance or fraud check failed";

        return new EftPosResult(request.TransactionId, success, message);
    }
}
