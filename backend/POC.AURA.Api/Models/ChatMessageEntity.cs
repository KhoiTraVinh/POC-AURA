namespace POC.AURA.Api.Models;

public class ChatMessageEntity
{
    public int Id { get; set; }
    public string User { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
