using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using POC.AURA.Api.Data;
using POC.AURA.Api.DTOs;
using POC.AURA.Api.Factories;
using POC.AURA.Api.Hubs;

namespace POC.AURA.Api.Services;

// Facade / Service pattern wrapping database and SignalR interaction
public class MessageService(
    AppDbContext dbContext,
    IHubContext<ChatHub> hubContext) : IMessageService
{
    public async Task<MessageDto> SendMessageAsync(SendMessageRequest request)
    {
        var groupExists = await dbContext.Groups.AnyAsync(g => g.Id == request.GroupId);
        if (!groupExists)
            throw new ArgumentException("Group not found.");

        var message = MessageFactory.CreateMessage(request.GroupId, request.Type, request.Ref);

        dbContext.Messages.Add(message);
        await dbContext.SaveChangesAsync();

        // Broadcast new message notification to the group
        await hubContext.Clients.Group(request.GroupId.ToString())
            .SendAsync("NewMessageNotification", message.Id);

        return new MessageDto
        {
            Id = message.Id,
            GroupId = message.GroupId,
            Type = message.Type,
            Ref = message.Ref,
            CreatedAt = message.CreatedAt
        };
    }

    public async Task<List<MessageDto>> GetMessagesAsync(int groupId, int? afterMessageId)
    {
        var query = dbContext.Messages.Where(m => m.GroupId == groupId);

        if (afterMessageId.HasValue)
            query = query.Where(m => m.Id > afterMessageId.Value);

        return await query
            .OrderBy(m => m.CreatedAt)
            .Select(m => new MessageDto
            {
                Id = m.Id,
                GroupId = m.GroupId,
                Type = m.Type,
                Ref = m.Ref,
                CreatedAt = m.CreatedAt
            })
            .ToListAsync();
    }

    public async Task<ReadReceiptDto> GetReceiptAsync(int groupId, int staffId)
    {
        var receipt = await dbContext.ReadReceipts
            .FirstOrDefaultAsync(r => r.GroupId == groupId && r.StaffId == staffId);

        return new ReadReceiptDto { GroupId = groupId, StaffId = staffId, LastReadMessageId = receipt?.LastReadMessageId };
    }

    public async Task UpdateReadReceiptAsync(int groupId, int staffId, int lastReadMessageId)
    {
        var receipt = await dbContext.ReadReceipts
            .FirstOrDefaultAsync(r => r.GroupId == groupId && r.StaffId == staffId);

        if (receipt == null)
        {
            receipt = MessageFactory.CreateReadReceipt(groupId, staffId, lastReadMessageId);
            dbContext.ReadReceipts.Add(receipt);
        }
        else
        {
            if (receipt.LastReadMessageId >= lastReadMessageId)
                return; // Nothing to update
                
            receipt.LastReadMessageId = lastReadMessageId;
        }

        await dbContext.SaveChangesAsync();

        // Implement logic: "tất cả mọi người trong group xem hết rồi mới hiện đã xem"
        // Calculate the minimum ReadReceipt across all members in the group
        var memberCount = await dbContext.Members.CountAsync(m => m.GroupId == groupId);
        
        int groupReadPointer = 0;
        if (memberCount > 0)
        {
            var groupReceipts = await dbContext.ReadReceipts
                .Where(r => r.GroupId == groupId)
                .ToListAsync();

            // If not everyone has a receipt, the minimum pointer is 0 (since some members haven't read anything)
            if (groupReceipts.Count == memberCount)
            {
                groupReadPointer = groupReceipts.Min(r => r.LastReadMessageId) ?? 0;
            }
        }

        // Broadcast the group's ALL-READ pointer to the group instead of the individual user's
        await hubContext.Clients.Group(groupId.ToString())
            .SendAsync("UserReadReceipt", new { staffId, messageId = groupReadPointer });
    }
}
