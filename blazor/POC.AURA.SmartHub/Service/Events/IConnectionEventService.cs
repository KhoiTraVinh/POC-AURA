namespace POC.AURA.SmartHub.Service.Events;

public class TokenRefreshEventArgs(int serverConnectionId) : EventArgs
{
    public int ServerConnectionId { get; } = serverConnectionId;
}

public interface IConnectionEventService
{
    event EventHandler<TokenRefreshEventArgs>? TokenRefreshed;
    void OnTokenRefreshed(int serverConnectionId);
}
