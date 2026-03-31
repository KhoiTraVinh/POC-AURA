using POC.AURA.SmartHub.Common.Models;

namespace POC.AURA.SmartHub.Service.Auth;

public interface IClientAuthenticationService
{
    /// <summary>
    /// Step 1 of PKCE flow: generate code_verifier, code_challenge, state.
    /// Cache verifier keyed by state. Return the OAuth authorization URL + state.
    /// </summary>
    Task<AuthUrlPipe> SignInAsync(ServerConnectionApiModel model, bool isAuthenticateOnly);

    /// <summary>
    /// Step 4 of PKCE flow: validate state → retrieve verifier → exchange code for tokens
    /// → persist token → schedule refresh.
    /// </summary>
    Task HandleAuthenticationAsync(OAuthCallbackDto dto);

    /// <summary>Check in-memory cache whether authState maps to a valid authenticated session.</summary>
    AuthenticationResult IsServerAuthenticated(string authState);

    /// <summary>Remove cache entry for the given authState.</summary>
    void Logout(string authState);

    /// <summary>Get a valid access token for the connection, refreshing if near expiry.</summary>
    Task<string?> GetAccessTokenAsync(int connectionId);

    /// <summary>Force a token refresh (called by ITokenSchedulerService).</summary>
    Task<string?> RefreshTokenAsync(int connectionId);
}
