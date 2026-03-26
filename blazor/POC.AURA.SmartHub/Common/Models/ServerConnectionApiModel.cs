namespace POC.AURA.SmartHub.Common.Models;

public class ServerConnectionApiModel
{
    public int Id { get; set; }
    public string ServerName { get; set; } = "";
    public string? Company { get; set; }
    public ConnectionStatus Status { get; set; }
    public string? Message { get; set; }
    public DateTime UpdatedAt { get; set; }

    private string _serverUrl = "";

    /// <summary>Getter guarantees trailing slash.</summary>
    public string ServerUrl
    {
        get => _serverUrl;
        set => _serverUrl = value.TrimEnd('/') + '/';
    }

    /// <summary>
    /// Token endpoint derived from ServerUrl.
    /// Real pattern: {ServerUrl}/oauth/connect/token
    /// POC: {ServerUrl}api/auth/token  (our simplified auth endpoint)
    /// </summary>
    public string AuthServerUrl => $"{ServerUrl}api/auth/token";
}
