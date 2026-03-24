namespace POC.AURA.Api.Entities;

public class PrintJobRecord
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string DocumentName { get; set; } = "";
    public string Content { get; set; } = "";
    public int Copies { get; set; }
    public string RequestorConnectionId { get; set; } = "";
    public string Status { get; set; } = "pending"; // pending | completed | failed
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ResultMessage { get; set; }
}
