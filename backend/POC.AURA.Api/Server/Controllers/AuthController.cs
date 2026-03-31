using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Mvc;
using POC.AURA.Api.Service.Auth;

namespace POC.AURA.Api.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly JwtService _jwt;

    public AuthController(JwtService jwt) => _jwt = jwt;

    [HttpPost("token")]
    public IActionResult GetToken([FromBody] TokenRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId))
            return BadRequest(new { error = "TenantId is required" });
        if (string.IsNullOrWhiteSpace(request.ClientType))
            return BadRequest(new { error = "ClientType is required (ui|smarthub)" });

        var userId   = $"{request.TenantId}_{request.ClientType}_{request.UserName ?? "user"}";
        var userName = request.UserName ?? $"{request.ClientType}@{request.TenantId}";

        var pair = _jwt.GenerateTokenPair(request.TenantId, request.ClientType, userId, userName);
        return Ok(pair);
    }

    [HttpPost("refresh")]
    public IActionResult Refresh([FromBody] RefreshRequest request)
    {
        var principal = _jwt.ValidateRefreshToken(request.RefreshToken);
        if (principal == null)
            return Unauthorized(new { error = "Invalid or expired refresh token" });

        var tenantId   = principal.FindFirst("tenant_id")!.Value;
        var clientType = principal.FindFirst("client_type")!.Value;
        var userId     = principal.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var userName   = principal.FindFirst(JwtRegisteredClaimNames.Name)!.Value;

        var pair = _jwt.GenerateTokenPair(tenantId, clientType, userId, userName);
        return Ok(pair);
    }
}

public record TokenRequest(string TenantId, string ClientType, string? UserName);
public record RefreshRequest(string RefreshToken);
