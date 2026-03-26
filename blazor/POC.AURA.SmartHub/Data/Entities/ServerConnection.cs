using POC.AURA.SmartHub.Common;

namespace POC.AURA.SmartHub.Data.Entities;

public class ServerConnection
{
    public int Id { get; set; }
    public string ServerName { get; set; } = "";
    public string ServerUrl { get; set; } = "";
    public string TenantId { get; set; } = "";
    public ConnectionStatus Status { get; set; } = ConnectionStatus.Disconnected;
    public string? Message { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? Company { get; set; }

    public AuthToken? AuthToken { get; set; }

    public string NormalizedUrl => ServerUrl.TrimEnd('/') + '/';
}
