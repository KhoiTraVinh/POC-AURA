using Microsoft.AspNetCore.SignalR.Client;
using POC.AURA.SmartHub.Common.Models;
using POC.AURA.SmartHub.Data.Entities;

namespace POC.AURA.SmartHub.UI.Services;

/// <summary>
/// UI-side wrapper for the BlazorConnectionHub SignalR connection.
///
/// In production (UBS.Eclipse.SmartHub.UI): connects to the Server process at
///   http://localhost:6758/clientServiceHub
/// In this POC: connects to the same-process BlazorConnectionHub endpoint.
///
/// Exposes all hub methods as strongly-typed async wrappers.
/// </summary>
public class SignalRService : IAsyncDisposable
{
    private readonly HubConnection _hub;
    private readonly ILogger<SignalRService> _logger;

    public bool IsConnected => _hub.State == HubConnectionState.Connected;

    /// <summary>Raised when the Server pushes a connection list update.</summary>
    public event Action<List<ServerConnection>>? ConnectionsSnapshotReceived;

    /// <summary>Raised when a job status update arrives.</summary>
    public event Action<string>? JobStatusUpdateReceived;

    /// <summary>Raised when authentication completes (OAuth callback processed).</summary>
    public event Action<string>? AuthenticationCompleted;

    public SignalRService(IConfiguration config, ILogger<SignalRService> logger)
    {
        _logger = logger;

        // In production: "http://localhost:6758/clientServiceHub"
        // In POC: same host, route /clientServiceHub
        var serverUrl = config["SmartHubServer:Url"] ?? "http://localhost:5050";

        _hub = new HubConnectionBuilder()
            .WithUrl($"{serverUrl}/clientServiceHub")
            .WithAutomaticReconnect()
            .Build();

        _hub.On<List<ServerConnection>>("ConnectionsSnapshot",
            list => ConnectionsSnapshotReceived?.Invoke(list));

        _hub.On<string>("JobStatusUpdate",
            json => JobStatusUpdateReceived?.Invoke(json));

        _hub.On<string>("AuthenticationComplete",
            state => AuthenticationCompleted?.Invoke(state));
    }

    public async Task StartAsync()
    {
        try
        {
            await _hub.StartAsync();
            _logger.LogInformation("SignalRService connected to BlazorConnectionHub");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SignalRService failed to connect");
        }
    }

    // ── Hub method wrappers ───────────────────────────────────────────────

    public Task<AuthUrlPipe> SignInAsync(ServerConnectionApiModel model, bool isAuthenticateOnly = false)
        => _hub.InvokeAsync<AuthUrlPipe>("SignInAsync", model, isAuthenticateOnly);

    public Task<AuthenticationResult> IsServerAuthenticatedAsync(string authState)
        => _hub.InvokeAsync<AuthenticationResult>("IsServerAuthenticated", authState);

    public Task LogoutAsync(string authState)
        => _hub.InvokeAsync("Logout", authState);

    public Task HandleAuthenticationAsync(string encodedMessage)
        => _hub.InvokeAsync("HandleAuthenticationAsync", encodedMessage);

    public Task ReceiveServerConnectionAsync(string connectionJson)
        => _hub.InvokeAsync("ReceiveServerConnection", connectionJson);

    public Task DeleteServerConnectionAsync(string connectionJson)
        => _hub.InvokeAsync("DeleteServerConnection", connectionJson);

    public Task DeleteRefreshTokenJobAsync(int serverConnectionId)
        => _hub.InvokeAsync("DeleteRefreshTokenJob", serverConnectionId);

    public async ValueTask DisposeAsync() => await _hub.DisposeAsync();
}
