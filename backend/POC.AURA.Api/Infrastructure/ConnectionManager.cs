using System.Collections.Concurrent;

namespace POC.AURA.Api.Infrastructure;

// Singleton pattern to manage active SignalR connections
public class ConnectionManager : IConnectionManager
{
    // Tracks ConnectionId -> groupId for auto-leave when disconnected (token expiration, network loss, tab closed)
    private readonly ConcurrentDictionary<string, string> _connectionGroups = new();

    public void AddConnection(string connectionId, string groupId)
    {
        _connectionGroups[connectionId] = groupId;
    }

    public void RemoveConnection(string connectionId)
    {
        _connectionGroups.TryRemove(connectionId, out _);
    }

    public string? GetGroupId(string connectionId)
    {
        return _connectionGroups.TryGetValue(connectionId, out var groupId) ? groupId : null;
    }
}
