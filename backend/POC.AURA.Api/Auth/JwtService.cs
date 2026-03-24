using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace POC.AURA.Api.Auth;

public record TokenPair(string AccessToken, string RefreshToken, long ExpiresAt);

public class JwtService
{
    // Demo key - in production use configuration
    private const string SecretKey = "poc-aura-super-secret-key-for-demo-only-32chars!!";
    public static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes(SecretKey));

    public TokenPair GenerateTokenPair(string tenantId, string clientType, string userId, string userName)
    {
        var now = DateTimeOffset.UtcNow;
        var accessExpiry = now.AddMinutes(10);
        var refreshExpiry = now.AddHours(24);

        var accessToken = CreateToken([
            new Claim("tenant_id", tenantId),
            new Claim("client_type", clientType),
            new Claim(JwtRegisteredClaimNames.Sub, userId),
            new Claim(JwtRegisteredClaimNames.Name, userName),
            new Claim("token_type", "access"),
        ], accessExpiry);

        var refreshToken = CreateToken([
            new Claim("tenant_id", tenantId),
            new Claim("client_type", clientType),
            new Claim(JwtRegisteredClaimNames.Sub, userId),
            new Claim(JwtRegisteredClaimNames.Name, userName),
            new Claim("token_type", "refresh"),
        ], refreshExpiry);

        return new TokenPair(accessToken, refreshToken, accessExpiry.ToUnixTimeSeconds());
    }

    public ClaimsPrincipal? ValidateRefreshToken(string token)
    {
        var handler = new JwtSecurityTokenHandler();
        try
        {
            var principal = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = SigningKey,
                ValidateIssuer = false,
                ValidateAudience = false,
                ClockSkew = TimeSpan.Zero
            }, out _);

            return principal.FindFirst("token_type")?.Value == "refresh" ? principal : null;
        }
        catch { return null; }
    }

    private static string CreateToken(Claim[] claims, DateTimeOffset expiry)
    {
        var creds = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(claims: claims, expires: expiry.UtcDateTime, signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
