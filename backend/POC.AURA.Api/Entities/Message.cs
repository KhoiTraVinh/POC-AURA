namespace POC.AURA.Api.Entities;

public class Message
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public string Type { get; set; } = null!;
    public string Ref { get; set; } = null!;
    public DateTime CreatedAt { get; set; }

    // Navigation properties
    public Group Group { get; set; } = null!;
}
