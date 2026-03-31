namespace POC.AURA.SmartHub.Common.Constants;

public static class EclipseApiUrl
{
    public static string PrintQueues(string serverUrl) => $"{serverUrl}print-queues";
    public static string UserInfo(string oAuthBase)    => $"{oAuthBase}connect/userinfo";

    // OAuth authorize — real: {OAuthBase}/connect/authorize
    // POC: routed to our own mock endpoint
    public static string Authorize(string oAuthBase)   => $"{oAuthBase}connect/authorize";
}
