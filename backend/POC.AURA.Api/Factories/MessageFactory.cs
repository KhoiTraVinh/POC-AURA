using POC.AURA.Api.Entities;

namespace POC.AURA.Api.Factories;

// Factory pattern for creating entities
public static class MessageFactory
{
    public static Message CreateMessage(int groupId, string type, string reference)
    {
        return new Message
        {
            GroupId = groupId,
            Type = type,
            Ref = reference,
            CreatedAt = DateTime.UtcNow
        };
    }

    public static ReadReceipt CreateReadReceipt(int groupId, int staffId, int lastReadMessageId)
    {
        return new ReadReceipt
        {
            GroupId = groupId,
            StaffId = staffId,
            LastReadMessageId = lastReadMessageId
        };
    }
}
