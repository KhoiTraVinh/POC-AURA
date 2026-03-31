namespace POC.AURA.Api.Data.Entities;

/// <summary>
/// Unified job record stored in the <c>Messages</c> table.
/// Covers both print jobs (<c>print_job</c>) and bank transactions (<c>bank_txn</c>).
/// </summary>
public class Message
{
    public int      Id                      { get; set; }
    public string   Type                    { get; set; } = null!;  // "print_job" | "bank_txn"
    public string   Ref                     { get; set; } = null!;  // unique job / transaction ID
    public string?  TenantId                { get; set; }
    public string?  Payload                 { get; set; }
    public string?  Status                  { get; set; }  // pending | completed | failed
    public string?  RequestorUserId         { get; set; }
    public string?  RequestorConnectionId   { get; set; }
    public DateTime? CompletedAt            { get; set; }
    public string?  ResultMessage           { get; set; }
    public DateTime CreatedAt               { get; set; }
}
