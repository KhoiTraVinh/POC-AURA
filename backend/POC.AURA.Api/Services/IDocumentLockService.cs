using POC.AURA.Api.Models;

namespace POC.AURA.Api.Services;

public interface IDocumentLockService
{
    LockAcquireResult TryAcquire(string docId, string fieldId, string userId, string userName, string connectionId);
    bool Release(string docId, string fieldId, string userId);
    IReadOnlyList<FieldLockInfo> ReleaseAllByConnection(string connectionId);
    void Heartbeat(string docId, string fieldId, string userId);
    FieldLockInfo? GetLock(string docId, string fieldId);
    IReadOnlyList<FieldLockInfo> GetAllLocks();
}
