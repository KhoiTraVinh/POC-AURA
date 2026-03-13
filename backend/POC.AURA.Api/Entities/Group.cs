namespace POC.AURA.Api.Entities;

public class Group
{
    public int Id { get; set; }
    public string GroupName { get; set; } = null!;
    public DateTime CreatedAt { get; set; }

    // Navigation properties
    public ICollection<Member> Members { get; set; } = new List<Member>();
    public ICollection<Message> Messages { get; set; } = new List<Message>();
    public ICollection<ReadReceipt> ReadReceipts { get; set; } = new List<ReadReceipt>();
}
