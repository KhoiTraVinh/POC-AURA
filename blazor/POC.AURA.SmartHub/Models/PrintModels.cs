namespace POC.AURA.SmartHub.Models;

public record PrintJob(
    string Id,
    string TenantId,
    string DocumentName,
    string Content,
    int Copies,
    string RequestorConnectionId,
    DateTime CreatedAt
);

public record PrintJobResult(
    string JobId,
    string TenantId,
    string RequestorConnectionId,
    bool Success,
    string Message,
    string CompletedAt
);
