using POC.AURA.SmartHub.Common;
using Microsoft.AspNetCore.SignalR;
using POC.AURA.SmartHub.Data;
using POC.AURA.SmartHub.Data.Entities;
using POC.AURA.SmartHub.Server.Hubs;
using POC.AURA.SmartHub.Server.Workers;
using System.Text.Json;

namespace POC.AURA.SmartHub.Server.Services;

public class ServerConnectionService(
    IServerConnectionRepository repo,
    HubConnectionWorker worker,
    IHubContext<BlazorConnectionHub> uiHub,
    ILogger<ServerConnectionService> logger) : IServerConnectionService
{
    public event Action? StateChanged;

    public async Task BroadcastUpdatedServerConnectionAsync(string connectionJson)
    {
        await uiHub.Clients.All.SendAsync("UpdateServerConnection", connectionJson);
    }

    public async Task BroadcastJobUpdateAsync(string jobJson)
    {
        await uiHub.Clients.All.SendAsync("JobStatusUpdate", jobJson);
    }

    /// <summary>Add a new server connection to the DB and start hub connections.</summary>
    public async Task<(bool Ok, string? Error)> AddServerAsync(
        string name, string serverUrl, string tenantId)
    {
        var conn = await repo.AddAsync(new ServerConnection
        {
            ServerName = name,
            ServerUrl  = serverUrl,
            TenantId   = tenantId,
            Status     = ConnectionStatus.Connecting,
            UpdatedAt  = DateTime.UtcNow
        });

        try
        {
            await worker.ConnectServerAsync(conn);
            await BroadcastUpdatedServerConnectionAsync(JsonSerializer.Serialize(conn));
            StateChanged?.Invoke();
            return (true, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to connect new server {Name}", name);
            return (false, ex.Message);
        }
    }

    public async Task DeleteServerAsync(int connectionId)
    {
        worker.DisconnectServer(connectionId);
        await repo.DeleteAsync(connectionId);
        await uiHub.Clients.All.SendAsync("RemoveServerConnection", connectionId);
        StateChanged?.Invoke();
    }

    public async Task<List<ServerConnection>> GetAllAsync() => await repo.GetAllAsync();

    /// <summary>Re-authenticate and reconnect an existing server connection.</summary>
    public async Task ReLoginAsync(int connectionId)
    {
        var conn = await repo.GetByIdAsync(connectionId);
        if (conn is null) return;

        await repo.UpdateStatusAsync(connectionId, ConnectionStatus.Connecting, "Re-authenticating…");
        worker.DisconnectServer(connectionId);
        await worker.ConnectServerAsync(conn);
        StateChanged?.Invoke();
    }
}
