namespace POC.AURA.Api.Common.Models;

public record FieldLockEntry(
    string DocId,
    string FieldId,
    string UserId,
    string UserName,
    string ConnectionId,
    DateTime ExpiresAt
)
{
    public FieldLockInfo ToInfo() => new(DocId, FieldId, UserId, UserName, ExpiresAt);
}

public record FieldLockInfo(string DocId, string FieldId, string UserId, string UserName, DateTime ExpiresAt);

public record LockAcquireResult(bool Acquired, DateTime? ExpiresAt, FieldLockInfo? CurrentHolder);
