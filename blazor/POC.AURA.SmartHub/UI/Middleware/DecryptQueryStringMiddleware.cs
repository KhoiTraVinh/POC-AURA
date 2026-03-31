using System.Text;

namespace POC.AURA.SmartHub.UI.Middleware;

/// <summary>
/// Middleware that runs before route matching to handle the OAuth callback redirect.
///
/// In production: decrypts the Base64-encoded query string from the custom URL scheme handler
///   (eclipsesmarthub://callback?encrypted=...) before ASP.NET Core parses the request.
///
/// In this POC: pass-through (query strings from the mock authorize endpoint are plain text).
/// </summary>
public class DecryptQueryStringMiddleware(RequestDelegate next, ILogger<DecryptQueryStringMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api/Authentication/oauth_callback"))
        {
            // Production: Base64-decode and AES-decrypt the query string here
            // using AppProtection.DecryptString(encryptedQueryString)
            // POC: query string is already plain text — pass through
            logger.LogDebug("OAuth callback intercepted (POC: no decryption needed)");
        }

        await next(context);
    }
}

public static class DecryptQueryStringMiddlewareExtensions
{
    public static IApplicationBuilder UseDecryptQueryString(this IApplicationBuilder app)
        => app.UseMiddleware<DecryptQueryStringMiddleware>();
}
