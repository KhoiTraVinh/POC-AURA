namespace POC.AURA.SmartHub.Server.Services;

public class PrintService(ILogger<PrintService> logger) : IPrintService
{
    private static readonly string[] MockPrinters =
        ["HP LaserJet Pro (POC)", "Canon ImageRunner (POC)", "Epson WorkForce (POC)"];

    public IReadOnlyList<string> GetPrinterDetailList()
    {
        // Real: WMI Win32_Printer query with 5-second timeout
        logger.LogDebug("GetPrinterDetailList called (mock)");
        return MockPrinters;
    }

    public async Task<(bool Success, string Message)> PrintDocumentAsync(
        PrintJobRequest job, string accessToken, CancellationToken ct = default)
    {
        // Real: download PDF via "Document" HTTP client → validate printer → Aspose.PDF print
        logger.LogInformation("Printing #{Id} \"{Name}\" ×{Copies}", job.Id, job.DocumentName, job.Copies);
        await Task.Delay(Random.Shared.Next(1_000, 3_000), ct);
        return (true, $"Printed {job.Copies}× \"{job.DocumentName}\" on {MockPrinters[0]}");
    }
}
