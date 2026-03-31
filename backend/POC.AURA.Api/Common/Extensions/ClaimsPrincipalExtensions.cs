using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using POC.AURA.Api.Common.Constants;

namespace POC.AURA.Api.Common.Extensions;

/// <summary>
/// Extension methods for reading AURA-specific JWT claims from a <see cref="ClaimsPrincipal"/>.
/// Centralises all claim-name magic strings so controllers and hubs never reference raw strings.
/// </summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Returns the <c>tenant_id</c> claim value.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the claim is absent (misconfigured token).</exception>
    public static string GetTenantId(this ClaimsPrincipal user) =>
        user.FindFirst(ClaimNames.TenantId)?.Value
        ?? throw new InvalidOperationException("Missing 'tenant_id' claim.");

    /// <summary>
    /// Returns the <c>client_type</c> claim value (ui | smarthub | bank).
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the claim is absent.</exception>
    public static string GetClientType(this ClaimsPrincipal user) =>
        user.FindFirst(ClaimNames.ClientType)?.Value
        ?? throw new InvalidOperationException("Missing 'client_type' claim.");

    /// <summary>
    /// Returns the JWT <c>name</c> claim value.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the claim is absent.</exception>
    public static string GetUserName(this ClaimsPrincipal user) =>
        user.FindFirst(JwtRegisteredClaimNames.Name)?.Value
        ?? throw new InvalidOperationException("Missing 'name' claim.");
}
