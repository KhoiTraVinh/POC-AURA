using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using POC.AURA.Api.Data;
using POC.AURA.Api.DTOs;
using POC.AURA.Api.Entities;
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
        // Auto-seed Member for POC testing (Because there is no Add Member API)
        var member = await dbContext.Members.FirstOrDefaultAsync(m => m.GroupId == groupId && m.StaffId == staffId);
        if (member == null)
        {
            dbContext.Members.Add(new Member { GroupId = groupId, StaffId = staffId });
        }

        var receipt = await dbContext.ReadReceipts
            .FirstOrDefaultAsync(r => r.GroupId == groupId && r.StaffId == staffId);

        if (receipt == null)
        {
            receipt = MessageFactory.CreateReadReceipt(groupId, staffId, lastReadMessageId);
            dbContext.ReadReceipts.Add(receipt);
        }
        else
        {
            // Update only if pointer moved forward
            if (receipt.LastReadMessageId < lastReadMessageId)
            {
                receipt.LastReadMessageId = lastReadMessageId;
            }
        }

        await dbContext.SaveChangesAsync();

        // Calculate the minimum ReadReceipt across all members in the group
        var memberCount = await dbContext.Members.CountAsync(m => m.GroupId == groupId);
        
        int groupReadPointer = 0;
        if (memberCount > 0)
        {
            var receiptStats = await dbContext.ReadReceipts
                .Where(r => r.GroupId == groupId)
                .GroupBy(r => r.GroupId)
                .Select(g => new 
                {
                    Count = g.Count(),
                    MinLastRead = g.Min(r => r.LastReadMessageId)
                })
                .FirstOrDefaultAsync();

            // If everyone has a receipt, find the minimum pointer
            // Zero RAM allocation for entities! Processed 100% on DB layer.
            if (receiptStats != null && receiptStats.Count == memberCount)
            {
                groupReadPointer = receiptStats.MinLastRead ?? 0;
            }
        }

        // ALWAYS broadcast the group's ALL-READ pointer (even if local pointer didn't change)
        // This ensures a newly refreshed browser tab gets the latest group pointer immediately.
        await hubContext.Clients.Group(groupId.ToString())
            .SendAsync("UserReadReceipt", new { staffId, messageId = groupReadPointer });
    }
}
