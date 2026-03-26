namespace POC.AURA.SmartHub.Server.Services;

public interface IServerConnectionService
{
    /// <summary>Fires whenever server connection list state changes (add/delete/re-login).</summary>
    event Action? StateChanged;

    /// <summary>Broadcast updated server connection state to all connected Blazor UI clients.</summary>
    Task BroadcastUpdatedServerConnectionAsync(string connectionJson);

    /// <summary>Broadcast a print/EFT job status update to all UI clients.</summary>
    Task BroadcastJobUpdateAsync(string jobJson);

    Task<(bool Ok, string? Error)> AddServerAsync(string name, string serverUrl, string tenantId);
    Task DeleteServerAsync(int connectionId);
    Task ReLoginAsync(int connectionId);
    Task<List<POC.AURA.SmartHub.Data.Entities.ServerConnection>> GetAllAsync();
}
