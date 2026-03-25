using System.Collections.Concurrent;

namespace POC.AURA.Api.Services;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="IConnectionTracker"/>.
/// Registered as a <b>singleton</b> so the map lives for the entire application lifetime.
/// </summary>
/// <remarks>
/// Two dictionaries are maintained for O(1) lookups in both directions:
/// <list type="bullet">
///   <item><c>_userToConnections</c>  — userId  → set of connectionIds</item>
///   <item><c>_connectionToUser</c>   — connectionId → userId</item>
/// </list>
/// </remarks>
public sealed class ConnectionTracker : IConnectionTracker
{
    // userId -> { connectionId, ... }
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _userToConnections = new();
    // connectionId -> userId  (reverse index for fast Unregister)
    private readonly ConcurrentDictionary<string, string> _connectionToUser = new();

    /// <inheritdoc/>
    public void Register(string userId, string connectionId)
    {
        // Add forward mapping: userId → connectionId
        _userToConnections
            .GetOrAdd(userId, _ => new ConcurrentDictionary<string, byte>())
            .TryAdd(connectionId, 0);

        // Add reverse mapping: connectionId → userId
        _connectionToUser[connectionId] = userId;
    }

    /// <inheritdoc/>
    public void Unregister(string connectionId)
    {
        if (!_connectionToUser.TryRemove(connectionId, out var userId))
            return;

        if (_userToConnections.TryGetValue(userId, out var connections))
        {
            connections.TryRemove(connectionId, out _);

            // Clean up the user entry when they have no more connections
            if (connections.IsEmpty)
                _userToConnections.TryRemove(userId, out _);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> GetConnectionIds(string userId) =>
        _userToConnections.TryGetValue(userId, out var connections)
            ? connections.Keys.ToList()
            : [];
}
