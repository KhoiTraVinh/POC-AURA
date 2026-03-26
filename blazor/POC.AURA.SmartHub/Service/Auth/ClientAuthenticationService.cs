using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using POC.AURA.SmartHub.Common;
using POC.AURA.SmartHub.Common.Constants;
using POC.AURA.SmartHub.Common.Models;
using POC.AURA.SmartHub.Data;
using POC.AURA.SmartHub.Service.Events;
using POC.AURA.SmartHub.Service.Scheduling;

namespace POC.AURA.SmartHub.Service.Auth;

public class ClientAuthenticationService(
    IServerConnectionRepository repo,
    IConnectionEventService events,
    ITokenSchedulerService scheduler,
    IMemoryCache cache,
    ILogger<ClientAuthenticationService> logger) : IClientAuthenticationService
{
    // ── PKCE helpers ──────────────────────────────────────────────────────

    private static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    private static string GenerateCodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    private static string GenerateState()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    // ── IClientAuthenticationService ──────────────────────────────────────

    public Task<AuthUrlPipe> SignInAsync(ServerConnectionApiModel model, bool isAuthenticateOnly)
    {
        var verifier   = GenerateCodeVerifier();
        var challenge  = GenerateCodeChallenge(verifier);
        var state      = GenerateState();

        // Cache verifier keyed by state (5-minute TTL)
        cache.Set($"pkce:{state}", new PkceEntry(verifier, model.Id), TimeSpan.FromMinutes(5));

        // Build OAuth authorization URL
        // Real: {model.AuthServerUrl}/connect/authorize
        // POC:  our mock endpoint that immediately approves
        var authUrl = BuildAuthUrl(model, challenge, state);

        logger.LogDebug("PKCE SignIn for conn {Id}: state={State}", model.Id, state);
        return Task.FromResult(new AuthUrlPipe(authUrl, state));
    }

    public async Task HandleAuthenticationAsync(OAuthCallbackDto dto)
    {
        if (!cache.TryGetValue<PkceEntry>($"pkce:{dto.State}", out var entry) || entry is null)
        {
            logger.LogWarning("Invalid or expired PKCE state: {State}", dto.State);
            throw new InvalidOperationException("Invalid or expired OAuth state.");
        }

        cache.Remove($"pkce:{dto.State}");

        var tokenResponse = await ExchangeCodeForTokensAsync(dto, entry);

        // Persist token to SQLite
        var expiredAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn > 0
            ? tokenResponse.ExpiresIn : 3600);

        await repo.SaveTokenAsync(entry.ConnectionId, tokenResponse.AccessToken, expiredAt);
        await repo.UpdateStatusAsync(entry.ConnectionId,
            ConnectionStatus.Online, "Authenticated");

        // Mark as authenticated in cache so IsServerAuthenticated returns true
        cache.Set($"auth:{dto.State}", true, TimeSpan.FromHours(1));

        // Store access token for fast retrieval
        cache.Set($"token:{entry.ConnectionId}", tokenResponse.AccessToken,
            expiredAt.AddMinutes(-5));

        // Schedule token refresh at 80% of lifetime
        var period = TimeSpan.FromSeconds(tokenResponse.ExpiresIn > 0
            ? tokenResponse.ExpiresIn : 3600);
        await scheduler.ScheduleTokenRefreshAsync(entry.ConnectionId, period);

        events.OnTokenRefreshed(entry.ConnectionId);

        logger.LogInformation("Authentication complete for connection {Id}", entry.ConnectionId);
    }

    public AuthenticationResult IsServerAuthenticated(string authState)
    {
        var ok = cache.TryGetValue<bool>($"auth:{authState}", out var authenticated) && authenticated;
        return ok ? AuthenticationResult.Success()
                  : AuthenticationResult.Failure(401, "Not authenticated");
    }

    public void Logout(string authState)
    {
        cache.Remove($"auth:{authState}");
        logger.LogInformation("Logged out state: {State}", authState);
    }

    public async Task<string?> GetAccessTokenAsync(int connectionId)
    {
        if (cache.TryGetValue<string>($"token:{connectionId}", out var cached) && cached is not null)
            return cached;

        return await RefreshTokenAsync(connectionId);
    }

    public async Task<string?> RefreshTokenAsync(int connectionId)
    {
        var conn = await repo.GetByIdAsync(connectionId);
        if (conn is null) return null;

        try
        {
            // POC: re-fetch from our simplified token endpoint
            var newToken = await FetchSimplifiedTokenAsync(conn.NormalizedUrl, conn.TenantId);
            if (newToken is null) return null;

            var expiredAt = DateTime.UtcNow.AddMinutes(55);
            await repo.SaveTokenAsync(connectionId, newToken, expiredAt);
            cache.Set($"token:{connectionId}", newToken, expiredAt.AddMinutes(-5));

            events.OnTokenRefreshed(connectionId);
            logger.LogInformation("Token refreshed for connection {Id} ({Name})", connectionId, conn.ServerName);
            return newToken;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Token refresh failed for connection {Id}", connectionId);
            return null;
        }
    }

    // ── Private ───────────────────────────────────────────────────────────

    private static string BuildAuthUrl(ServerConnectionApiModel model, string challenge, string state)
    {
        // POC: points to our own mock authorize endpoint on the SmartHub itself.
        // Real: would point to {ServerUrl}/oauth/connect/authorize on the Aura identity server.
        var baseUri = new Uri(model.ServerUrl);
        var authorizeBase = $"{baseUri.Scheme}://{baseUri.Host}:{baseUri.Port}";

        var query = new Dictionary<string, string?>
        {
            ["client_id"]             = ClientOAuthConstant.ClientId,
            ["response_type"]         = "code",
            ["redirect_uri"]          = ClientOAuthConstant.OAuthRedirectUri,
            ["scope"]                 = ClientOAuthConstant.OAuthScope,
            ["code_challenge"]        = challenge,
            ["code_challenge_method"] = "S256",
            ["state"]                 = state,
        };
        var qs = string.Join("&", query.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value ?? "")}"));

        // POC mock endpoint — replace with real OAuth server URL in production
        return $"{authorizeBase}/oauth/mock-authorize?{qs}";
    }

    private async Task<TokenResponseDto> ExchangeCodeForTokensAsync(OAuthCallbackDto dto, PkceEntry entry)
    {
        var conn = await repo.GetByIdAsync(entry.ConnectionId)
            ?? throw new InvalidOperationException($"Connection {entry.ConnectionId} not found");

        // POC: call our simplified /api/auth/token instead of a real PKCE token exchange.
        // Real: POST to conn.AuthServerUrl with form-encoded body:
        //   client_id, code, code_verifier, grant_type=authorization_code, redirect_uri
        using var http = new HttpClient { BaseAddress = new Uri(conn.NormalizedUrl) };
        var resp = await http.PostAsJsonAsync("api/auth/token", new
        {
            TenantId   = conn.TenantId,
            ClientType = "smarthub",
            UserName   = $"smarthub@{conn.TenantId}"
        });
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = json.GetProperty("accessToken").GetString() ?? "";

        return new TokenResponseDto
        {
            AccessToken = accessToken,
            TokenType   = "Bearer",
            ExpiresIn   = 3600,
            Scope       = ClientOAuthConstant.OAuthScope
        };
    }

    private static async Task<string?> FetchSimplifiedTokenAsync(string baseUrl, string tenantId)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var resp = await http.PostAsJsonAsync("api/auth/token", new
        {
            TenantId   = tenantId,
            ClientType = "smarthub",
            UserName   = $"smarthub@{tenantId}"
        });
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("accessToken").GetString();
    }

    private record PkceEntry(string Verifier, int ConnectionId);
}
