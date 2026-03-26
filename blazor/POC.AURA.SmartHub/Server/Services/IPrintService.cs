namespace POC.AURA.SmartHub.Server.Services;

public record PrintJobResult(
    string JobId,
    string TenantId,
    string RequestorConnectionId,
    bool   Success,
    string Message,
    DateTime CompletedAt
);

public record PrintJobRequest(
    string Id,
    string TenantId,
    string DocumentName,
    string Content,
    int    Copies,
    string RequestorConnectionId,
    DateTime CreatedAt,
    int    ConnectionId
);

public interface IPrintService
{
    /// <summary>
    /// Return list of available printers.
    /// Real: WMI Win32_Printer query.
    /// POC: returns a static list.
    /// </summary>
    IReadOnlyList<string> GetPrinterDetailList();

    /// <summary>
    /// Print a document.
    /// Real: download PDF via HTTP → Aspose.PDF → send to printer.
    /// POC: simulate 1–3 s delay.
    /// </summary>
    Task<(bool Success, string Message)> PrintDocumentAsync(
        PrintJobRequest job, string accessToken, CancellationToken ct = default);
}
