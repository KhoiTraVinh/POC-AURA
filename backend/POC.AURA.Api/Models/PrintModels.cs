namespace POC.AURA.Api.Models;

public record PrintJobRequest(string DocumentName, string Content, int Copies = 1);

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
    DateTime CompletedAt
);

/// <summary>Request body for SmartHub to report job completion via HTTP API.</summary>
public record CompleteJobRequest(string JobId, bool Success, string Message);
