namespace POC.AURA.SmartHub.Common.Constants;

public static class ClientOAuthConstant
{
    /// <summary>OAuth client_id registered on the Aura server.</summary>
    public const string ClientId = "Xu9qGOI7";

    /// <summary>
    /// In production: custom URL scheme "eclipsesmarthub://callback".
    /// In this POC: HTTP callback route handled by AuthenticationController.
    /// </summary>
    public const string OAuthRedirectUri = "eclipsesmarthub://callback";

    public const string OAuthScope = "openid profile email offline_access";

    /// <summary>IPC pipe name used by the UI process to forward OAuth callbacks to the Server process.</summary>
    public const string NamedPipeName = "EclipseSmartHubPrintServer";
}
