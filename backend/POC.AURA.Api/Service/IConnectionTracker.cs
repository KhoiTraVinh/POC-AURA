namespace POC.AURA.Api.Service;

/// <summary>
/// Tracks the mapping between authenticated user identities and their active
/// SignalR connection IDs.
/// <para>
/// A user may have multiple active connections simultaneously (e.g. two browser tabs).
/// All connections are tracked so that job-completion notifications are delivered to
/// every tab the user has open.
/// </para>
/// </summary>
public interface IConnectionTracker
{
    /// <summary>
    /// Associates <paramref name="connectionId"/> with <paramref name="userId"/>.
    /// Called in <c>OnConnectedAsync</c>.
    /// </summary>
    void Register(string userId, string connectionId);

    /// <summary>
    /// Removes the connection from all user mappings.
    /// Called in <c>OnDisconnectedAsync</c>.
    /// </summary>
    void Unregister(string connectionId);

    /// <summary>
    /// Returns all active connection IDs for <paramref name="userId"/>.
    /// Returns an empty list when the user has no active connections (offline).
    /// </summary>
    IReadOnlyList<string> GetConnectionIds(string userId);

    /// <summary>
    /// Returns a snapshot of all active connections keyed by userId.
    /// Used by the debug /api/connections endpoint.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>> GetAll();
}
