namespace POC.AURA.Api.Entities;

public class Member
{
    public int GroupId { get; set; }
    public int StaffId { get; set; }

    // Navigation properties
    public Group Group { get; set; } = null!;
}
