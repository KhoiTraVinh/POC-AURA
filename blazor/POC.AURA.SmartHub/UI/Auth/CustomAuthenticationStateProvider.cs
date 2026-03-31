using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using POC.AURA.SmartHub.Common.Constants;
using POC.AURA.SmartHub.UI.Services;
using System.Security.Claims;

namespace POC.AURA.SmartHub.UI.Auth;

/// <summary>
/// Reads the session cookie (bz_session / adsvr-state), validates it with
/// BlazorConnectionHub.IsServerAuthenticated, and returns the Blazor AuthenticationState.
///
/// In production: reads encrypted cookie via DPAPI.
/// In this POC: reads from ProtectedSessionStorage.
/// </summary>
public class CustomAuthenticationStateProvider(
    ProtectedSessionStorage sessionStorage,
    SignalRService signalR,
    ILogger<CustomAuthenticationStateProvider> logger) : AuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous =
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            var result = await sessionStorage.GetAsync<string>(AppConstants.SessionStateCookieName);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Value))
                return Anonymous;

            var authState = result.Value;
            var authResult = await signalR.IsServerAuthenticatedAsync(authState);

            if (!authResult.IsAuthenticated)
                return Anonymous;

            // Build claims identity from the validated session
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, authState),
                new Claim(ClaimTypes.Role, "SmartHubUser")
            ], "SmartHubAuth");

            return new AuthenticationState(new ClaimsPrincipal(identity));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GetAuthenticationStateAsync failed");
            return Anonymous;
        }
    }

    public async Task MarkAuthenticatedAsync(string authState)
    {
        await sessionStorage.SetAsync(AppConstants.SessionStateCookieName, authState);
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public async Task MarkLoggedOutAsync()
    {
        await sessionStorage.DeleteAsync(AppConstants.SessionStateCookieName);
        NotifyAuthenticationStateChanged(Task.FromResult(Anonymous));
    }
}
