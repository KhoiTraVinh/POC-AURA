using POC.AURA.SmartHub.Common;
using Microsoft.AspNetCore.SignalR;
using POC.AURA.SmartHub.Common.Models;
using POC.AURA.SmartHub.Data;
using POC.AURA.SmartHub.Service.Auth;
using POC.AURA.SmartHub.Service.Scheduling;
using System.Text.Json;

namespace POC.AURA.SmartHub.Server.Hubs;

/// <summary>
/// SignalR hub exposed at /clientServiceHub (port 6758 in production).
/// Blazor UI connects here to receive real-time updates and invoke server operations.
///
/// In production: runs in UBS.Eclipse.SmartHub.Server (separate Windows Service).
/// In this POC: runs in the same process as the Blazor UI.
/// </summary>
public class BlazorConnectionHub(
    IServerConnectionRepository repo,
    IClientAuthenticationService auth,
    ITokenSchedulerService scheduler,
    ILogger<BlazorConnectionHub> logger) : Hub
{
    private static readonly JsonSerializerOptions _jsonOptions =
        new() { PropertyNameCaseInsensitive = true };
    // ── Hub Methods (called by Blazor UI via SignalRService) ──────────────

    /// <summary>
    /// Add or update a server connection. Calls the Aura API to fetch company info,
    /// saves to DB, then broadcasts the update to all UI clients.
    /// </summary>
    public async Task ReceiveServerConnection(string connectionJson)
    {
        var model = JsonSerializer.Deserialize<ServerConnectionApiModel>(connectionJson, _jsonOptions);
        if (model is null) return;

        var entity = await repo.GetByIdAsync(model.Id);
        if (entity is null)
        {
            entity = new Data.Entities.ServerConnection
            {
                ServerName = model.ServerName,
                ServerUrl  = model.ServerUrl,
                TenantId   = "",           // set by caller or derived from URL
                Status     = ConnectionStatus.Connecting,
                UpdatedAt  = DateTime.UtcNow
            };
            entity = await repo.AddAsync(entity);
        }
        else
        {
            await repo.UpdateStatusAsync(entity.Id, ConnectionStatus.Connecting);
        }

        await Clients.All.SendAsync("UpdateServerConnection", JsonSerializer.Serialize(model));
        logger.LogInformation("ReceiveServerConnection: {Name}", model.ServerName);
    }

    /// <summary>Delete a server connection and notify all UI clients.</summary>
    public async Task DeleteServerConnection(string connectionJson)
    {
        var model = JsonSerializer.Deserialize<ServerConnectionApiModel>(connectionJson, _jsonOptions);
        if (model is null) return;

        await scheduler.DeleteTokenRefreshAsync(model.Id);
        await repo.DeleteAsync(model.Id);
        await Clients.All.SendAsync("RemoveServerConnection", model.Id);
        logger.LogInformation("DeleteServerConnection: {Id}", model.Id);
    }

    /// <summary>
    /// Step 1 of OAuth PKCE flow. Generates code_verifier, code_challenge, state.
    /// Returns the OAuth authorization URL and state to the UI.
    /// UI saves state to ProtectedSessionStorage then redirects browser to AuthUrl.
    /// </summary>
    public async Task<AuthUrlPipe> SignInAsync(ServerConnectionApiModel model, bool isAuthenticateOnly)
    {
        var pipe = await auth.SignInAsync(model, isAuthenticateOnly);
        logger.LogInformation("SignIn initiated for {Url}, state={State}", model.ServerUrl, pipe.State);
        return pipe;
    }

    /// <summary>
    /// Check whether the given PKCE state maps to a completed authentication.
    /// Used by CustomAuthenticationStateProvider on page load.
    /// </summary>
    public Task<AuthenticationResult> IsServerAuthenticated(string authState)
        => Task.FromResult(auth.IsServerAuthenticated(authState));

    /// <summary>Remove the authentication cache entry for the given state.</summary>
    public Task Logout(string authState)
    {
        auth.Logout(authState);
        logger.LogInformation("Logout: state={State}", authState);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Step 4 of OAuth PKCE flow. Called by AuthenticationController after receiving
    /// the OAuth callback. Validates state, retrieves verifier, exchanges code for tokens,
    /// persists to SQLite, schedules refresh.
    ///
    /// In production: receives an AES-encrypted payload via Named Pipe from the UI process.
    /// In this POC: receives a plain JSON OAuthCallbackDto.
    /// </summary>
    public async Task HandleAuthenticationAsync(string encodedMessage)
    {
        // Production: decrypt with AppProtection/BouncyCastle first
        // POC: treat as plain JSON
        var dto = JsonSerializer.Deserialize<OAuthCallbackDto>(encodedMessage, _jsonOptions);
        if (dto is null) return;

        await auth.HandleAuthenticationAsync(dto);
        await Clients.Caller.SendAsync("AuthenticationComplete", dto.State);
        logger.LogInformation("HandleAuthenticationAsync complete for state={State}", dto.State);
    }

    /// <summary>Cancel the token refresh Quartz job (production) / Timer (POC) for a connection.</summary>
    public async Task DeleteRefreshTokenJob(int serverConnectionId)
    {
        await scheduler.DeleteTokenRefreshAsync(serverConnectionId);
        await repo.DeleteTokenAsync(serverConnectionId);
        logger.LogInformation("DeleteRefreshTokenJob: conn {Id}", serverConnectionId);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────

    public override async Task OnConnectedAsync()
    {
        // Send current connection list snapshot to newly connected UI client
        var connections = await repo.GetAllAsync();
        await Clients.Caller.SendAsync("ConnectionsSnapshot", connections);
        await base.OnConnectedAsync();
    }
}
