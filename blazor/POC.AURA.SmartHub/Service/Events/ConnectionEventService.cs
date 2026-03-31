namespace POC.AURA.SmartHub.Service.Events;

public class ConnectionEventService : IConnectionEventService
{
    public event EventHandler<TokenRefreshEventArgs>? TokenRefreshed;

    public void OnTokenRefreshed(int serverConnectionId) =>
        TokenRefreshed?.Invoke(this, new TokenRefreshEventArgs(serverConnectionId));
}
