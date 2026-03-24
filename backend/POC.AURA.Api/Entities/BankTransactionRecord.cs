namespace POC.AURA.Api.Entities;

public class BankTransactionRecord
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "VND";
    public string RequestorConnectionId { get; set; } = "";
    public string Status { get; set; } = "pending"; // pending | completed | failed
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ResultMessage { get; set; }
}
