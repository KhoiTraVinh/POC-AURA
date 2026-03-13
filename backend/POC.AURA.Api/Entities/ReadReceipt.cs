namespace POC.AURA.Api.Entities;

public class ReadReceipt
{
    public int GroupId { get; set; }
    public int StaffId { get; set; }
    public int? LastReadMessageId { get; set; }

    // Navigation properties
    public Group Group { get; set; } = null!;
    public Message? LastReadMessage { get; set; }
}
