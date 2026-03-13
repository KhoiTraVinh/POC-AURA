using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using POC.AURA.Api.Data;
using POC.AURA.Api.DTOs;
using POC.AURA.Api.Entities;
using POC.AURA.Api.Hubs;

namespace POC.AURA.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MessagesController(AppDbContext dbContext, IHubContext<ChatHub> hubContext) : ControllerBase
{
    // Lấy pointer (LastReadMessageId) hiện tại — client dùng khi connect lần đầu hoặc reconnect
    [HttpGet("receipt")]
    public async Task<IActionResult> GetReceipt([FromQuery] int groupId, [FromQuery] int staffId)
    {
        var receipt = await dbContext.ReadReceipts
            .FirstOrDefaultAsync(r => r.GroupId == groupId && r.StaffId == staffId);

        return Ok(new ReadReceiptDto
        {
            GroupId = groupId,
            StaffId = staffId,
            LastReadMessageId = receipt?.LastReadMessageId
        });
    }

    [HttpGet("{groupId}")]
    public async Task<IActionResult> GetMessages(int groupId, [FromQuery] int? afterMessageId)
    {
        var query = dbContext.Messages.Where(m => m.GroupId == groupId);
        
        if (afterMessageId.HasValue)
        {
            query = query.Where(m => m.Id > afterMessageId.Value);
        }

        var messages = await query
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

        return Ok(messages);
    }

    [HttpPost]
    public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request)
    {
        var groupExists = await dbContext.Groups.AnyAsync(g => g.Id == request.GroupId);
        if (!groupExists)
            return BadRequest("Group not found.");

        var message = new Message
        {
            GroupId = request.GroupId,
            Type = request.Type,
            Ref = request.Ref,
            CreatedAt = DateTime.UtcNow
        };

        dbContext.Messages.Add(message);
        await dbContext.SaveChangesAsync();

        // Broadcast ONLY the notification to the group
        await hubContext.Clients.Group(request.GroupId.ToString())
            .SendAsync("NewMessageNotification", message.Id);

        return Ok(new MessageDto
        {
            Id = message.Id,
            GroupId = message.GroupId,
            Type = message.Type,
            Ref = message.Ref,
            CreatedAt = message.CreatedAt
        });
    }

    [HttpPost("read")]
    public async Task<IActionResult> MarkAsRead([FromBody] MarkReadRequest request)
    {
        var receipt = await dbContext.ReadReceipts
            .FirstOrDefaultAsync(r => r.GroupId == request.GroupId && r.StaffId == request.StaffId);

        if (receipt == null)
        {
            receipt = new ReadReceipt
            {
                GroupId = request.GroupId,
                StaffId = request.StaffId,
                LastReadMessageId = request.LastReadMessageId
            };
            dbContext.ReadReceipts.Add(receipt);
        }
        else
        {
            // Only update if it's a newer message
            if (receipt.LastReadMessageId < request.LastReadMessageId || receipt.LastReadMessageId == null)
            {
                receipt.LastReadMessageId = request.LastReadMessageId;
            }
        }

        await dbContext.SaveChangesAsync();

        // Broadcast read receipt to the group
        await hubContext.Clients.Group(request.GroupId.ToString())
            .SendAsync("UserReadReceipt", new { staffId = request.StaffId, messageId = request.LastReadMessageId });

        return Ok();
    }
}
