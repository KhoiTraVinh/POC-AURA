using Microsoft.AspNetCore.SignalR;
using POC.AURA.Api.Models;
using POC.AURA.Api.Services;

namespace POC.AURA.Api.Hubs;

/// <summary>
/// Hub for collaborative document editing with field-level pessimistic locking.
/// Only one user can edit a field at a time.
/// Locks expire after 30s (heartbeat extends them).
/// </summary>
public class DocumentHub : Hub
{
    private readonly IDocumentLockService _locks;

    public DocumentHub(IDocumentLockService locks) => _locks = locks;

    private string UserId =>
        Context.GetHttpContext()?.Request.Query["userId"].ToString()
        ?? Context.ConnectionId;

    private string UserName =>
        Context.GetHttpContext()?.Request.Query["userName"].ToString()
        ?? "Anonymous";

    public override async Task OnConnectedAsync()
    {
        // Send snapshot of all active locks to new connection
        await Clients.Caller.SendAsync("LockSnapshot", _locks.GetAllLocks());
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Auto-release all locks held by this connection
        var released = _locks.ReleaseAllByConnection(Context.ConnectionId);
        foreach (var fieldLock in released)
        {
            await Clients.Others.SendAsync("FieldUnlocked", new
            {
                fieldLock.DocId,
                fieldLock.FieldId
            });
        }
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Try to acquire exclusive lock on a document field.
    /// Returns: { acquired: bool, expiresAt: DateTime?, currentHolder: FieldLockInfo? }
    /// </summary>
    public async Task<LockAcquireResult> AcquireFieldLock(string docId, string fieldId)
    {
        var result = _locks.TryAcquire(docId, fieldId, UserId, UserName, Context.ConnectionId);

        if (result.Acquired)
        {
            // Broadcast lock acquired to all OTHER clients
            await Clients.Others.SendAsync("FieldLocked", new
            {
                DocId = docId,
                FieldId = fieldId,
                UserId,
                UserName,
                ExpiresAt = result.ExpiresAt
            });
        }

        return result;
    }

    /// <summary>
    /// Release lock. Only the lock holder can release.
    /// </summary>
    public async Task ReleaseFieldLock(string docId, string fieldId)
    {
        var released = _locks.Release(docId, fieldId, UserId);
        if (released)
        {
            await Clients.Others.SendAsync("FieldUnlocked", new { DocId = docId, FieldId = fieldId });
        }
    }

    /// <summary>
    /// Heartbeat to keep lock alive while user is actively typing.
    /// Must be called every ~10s to prevent lock expiry.
    /// </summary>
    public void HeartbeatFieldLock(string docId, string fieldId)
    {
        _locks.Heartbeat(docId, fieldId, UserId);
    }

    /// <summary>
    /// Broadcast field value change to all OTHER users in real-time.
    /// Caller must hold the lock.
    /// </summary>
    public async Task UpdateFieldValue(string docId, string fieldId, string value)
    {
        var currentLock = _locks.GetLock(docId, fieldId);
        if (currentLock == null || currentLock.UserId != UserId)
            throw new HubException("Cannot update field: you don't hold the lock");

        await Clients.Others.SendAsync("FieldValueChanged", new
        {
            DocId = docId,
            FieldId = fieldId,
            Value = value,
            UserId,
            UserName
        });
    }
}
