namespace POC.AURA.Api.Models;

/// <summary>Inbound request from an Angular UI client to queue a print job.</summary>
public record PrintJobRequest(string DocumentName, string Content, int Copies = 1);

/// <summary>
/// Immutable snapshot of a print job routed to the SmartHub processor.
/// Serialised as a SignalR payload for <c>ExecutePrintJob</c> and <c>PrintJobQueued</c>.
/// </summary>
public record PrintJob(
    string Id,
    string TenantId,
    string DocumentName,
    string Content,
    int    Copies,
    string RequestorConnectionId,
    DateTime CreatedAt
);

/// <summary>
/// Result emitted to Angular via <c>PrintJobComplete</c> / <c>PrintJobStatusUpdate</c>
/// after the SmartHub reports completion.
/// </summary>
public record PrintJobResult(
    string   JobId,
    string   TenantId,
    string   RequestorConnectionId,
    bool     Success,
    string   Message,
    DateTime CompletedAt
);

/// <summary>
/// Request body POSTed by the SmartHub to <c>/api/print/complete</c>.
/// </summary>
public record CompleteJobRequest(string JobId, bool Success, string Message);
