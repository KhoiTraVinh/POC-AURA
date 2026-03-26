namespace POC.AURA.SmartHub.Data.Entities;

public class AuthToken
{
    public int ServerConnectionId { get; set; }
    public ServerConnection ServerConnection { get; set; } = null!;

    /// <summary>
    /// In this POC we store the access token here (re-fetched on expiry).
    /// In production this would be a real OAuth refresh token.
    /// </summary>
    public string AccessToken { get; set; } = "";
    public string TokenType { get; set; } = "Bearer";
    public string? CompanyId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiredAt { get; set; } = DateTime.UtcNow.AddHours(1);
}
