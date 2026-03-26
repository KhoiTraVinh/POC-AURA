using Microsoft.AspNetCore.Mvc;
using POC.AURA.SmartHub.Common.Models;
using POC.AURA.SmartHub.UI.Services;
using System.Text.Json;

namespace POC.AURA.SmartHub.UI.Controllers;

/// <summary>
/// Handles the OAuth 2.0 callback after the user authenticates on the Aura identity server.
///
/// In production:
///   - Receives an AES-encrypted payload forwarded via Named Pipe from the URL scheme handler
///   - POST /api/Authentication/oauth_callback  { encryptedString }
///   - Decrypts → calls BlazorConnectionHub.HandleAuthenticationAsync
///
/// In this POC:
///   - Receives the plain OAuthCallbackDto from the mock authorize endpoint
///   - GET /api/Authentication/oauth_callback?code=...&state=...
///   - Forwards to BlazorConnectionHub as JSON
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AuthenticationController(
    SignalRService signalR,
    ILogger<AuthenticationController> logger) : ControllerBase
{
    [HttpGet("oauth_callback")]
    public async Task<IActionResult> OAuthCallbackGet(
        [FromQuery] string code,
        [FromQuery] string scope,
        [FromQuery] string state,
        [FromQuery] string? session_state)
    {
        return await HandleCallbackAsync(new OAuthCallbackDto
        {
            Code         = code,
            Scope        = scope ?? "",
            State        = state,
            SessionState = session_state ?? ""
        });
    }

    [HttpPost("oauth_callback")]
    public async Task<IActionResult> OAuthCallbackPost([FromBody] OAuthCallbackRequest request)
    {
        // Production: decrypt request.EncryptedString with AppProtection/BouncyCastle first
        // POC: treat EncryptedString as plain JSON OAuthCallbackDto
        var dto = JsonSerializer.Deserialize<OAuthCallbackDto>(request.EncryptedString,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (dto is null) return BadRequest("Invalid payload");
        return await HandleCallbackAsync(dto);
    }

    private async Task<IActionResult> HandleCallbackAsync(OAuthCallbackDto dto)
    {
        logger.LogInformation("OAuth callback received: state={State}", dto.State);

        var json = JsonSerializer.Serialize(dto);
        await signalR.HandleAuthenticationAsync(json);

        // Redirect browser to the dashboard after successful auth
        return Redirect("/");
    }
}

public record OAuthCallbackRequest(string EncryptedString);
