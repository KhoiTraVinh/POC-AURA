using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using POC.AURA.Api.Common.Constants;
using POC.AURA.Api.Common.Models;
using POC.AURA.Api.Server.Hubs;

namespace POC.AURA.Api.Service;

public class DocumentLockService : IDocumentLockService, IHostedService, IDisposable
{
    private const int LockTtlSeconds = 30;

    // Key: "docId:fieldId"
    private readonly ConcurrentDictionary<string, FieldLockEntry> _locks = new();
    private readonly IHubContext<AuraHub>         _hub;
    private readonly ILogger<DocumentLockService> _logger;
    private Timer? _cleanupTimer;

    public DocumentLockService(
        IHubContext<AuraHub>         hub,
        ILogger<DocumentLockService> logger)
    {
        _hub    = hub;
        _logger = logger;
    }

    public LockAcquireResult TryAcquire(string docId, string fieldId, string userId, string userName, string connectionId)
    {
        var key      = $"{docId}:{fieldId}";
        var newEntry = new FieldLockEntry(docId, fieldId, userId, userName, connectionId,
            DateTime.UtcNow.AddSeconds(LockTtlSeconds));

        FieldLockEntry? existingHolder = null;
        var acquired = false;

        _locks.AddOrUpdate(
            key,
            addValueFactory: _ => { acquired = true; return newEntry; },
            updateValueFactory: (_, existing) =>
            {
                if (existing.UserId == userId || existing.ExpiresAt < DateTime.UtcNow)
                {
                    acquired = true;
                    return newEntry;
                }
                existingHolder = existing;
                return existing;
            });

        if (!acquired && existingHolder != null)
            return new LockAcquireResult(false, null, existingHolder.ToInfo());

        return new LockAcquireResult(true, newEntry.ExpiresAt, null);
    }

    public bool Release(string docId, string fieldId, string userId)
    {
        var key = $"{docId}:{fieldId}";
        if (_locks.TryGetValue(key, out var existing) && existing.UserId == userId)
            return _locks.TryRemove(key, out _);
        return false;
    }

    public IReadOnlyList<FieldLockInfo> ReleaseAllByConnection(string connectionId)
    {
        var released = new List<FieldLockInfo>();
        foreach (var (key, entry) in _locks)
        {
            if (entry.ConnectionId == connectionId && _locks.TryRemove(key, out var removed))
                released.Add(removed.ToInfo());
        }
        return released;
    }

    public void Heartbeat(string docId, string fieldId, string userId)
    {
        var key = $"{docId}:{fieldId}";
        // Only extend an existing lock — never create a new one.
        // Creating a lock here would produce a "zombie" entry (empty ConnectionId)
        // that can never be released by ReleaseAllByConnection, e.g. when the
        // client heartbeats via a new SignalR connection after an auto-reconnect
        // while the old connection's OnDisconnectedAsync already released the lock.
        if (!_locks.TryGetValue(key, out var existing) || existing.UserId != userId) return;
        var updated = existing with { ExpiresAt = DateTime.UtcNow.AddSeconds(LockTtlSeconds) };
        _locks.TryUpdate(key, updated, existing);
    }

    public FieldLockInfo? GetLock(string docId, string fieldId)
    {
        var key = $"{docId}:{fieldId}";
        return _locks.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTime.UtcNow
            ? entry.ToInfo()
            : null;
    }

    public IReadOnlyList<FieldLockInfo> GetAllLocks() =>
        _locks.Values.Where(e => e.ExpiresAt > DateTime.UtcNow).Select(e => e.ToInfo()).ToList();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cleanupTimer = new Timer(CleanupExpired, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        _logger.LogInformation("DocumentLockService started - lock TTL: {Ttl}s", LockTtlSeconds);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    private void CleanupExpired(object? state)
    {
        var now     = DateTime.UtcNow;
        var expired = new List<FieldLockInfo>();

        foreach (var (key, entry) in _locks)
        {
            if (entry.ExpiresAt < now && _locks.TryRemove(key, out var removed))
            {
                expired.Add(removed.ToInfo());
                _logger.LogDebug("Lock expired: {DocId}:{FieldId} (was held by {User})",
                    removed.DocId, removed.FieldId, removed.UserId);
            }
        }

        if (expired.Count > 0)
        {
            _logger.LogInformation("Expired {Count} stale field locks", expired.Count);
            _ = _hub.Clients.Group(HubGroups.DocAll)
                    .SendAsync(HubEvents.FieldsExpiredUnlocked, expired);
        }
    }

    public void Dispose() => _cleanupTimer?.Dispose();
}
