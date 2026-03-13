namespace POC.AURA.Api.DTOs;

public class SendMessageRequest
{
    public int GroupId { get; set; }
    public string Type { get; set; } = null!;
    public string Ref { get; set; } = null!;
}

public class MessageDto
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public string Type { get; set; } = null!;
    public string Ref { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}

public class MarkReadRequest
{
    public int GroupId { get; set; }
    public int LastReadMessageId { get; set; }
    public int StaffId { get; set; } // Currently hardcoded/passed for POC from frontend
}
