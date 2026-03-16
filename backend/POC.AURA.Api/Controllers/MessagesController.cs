using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using POC.AURA.Api.Data;
using POC.AURA.Api.DTOs;
using POC.AURA.Api.Entities;
using POC.AURA.Api.Hubs;
using POC.AURA.Api.Infrastructure;

namespace POC.AURA.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MessagesController(
    AppDbContext dbContext,
    IHubContext<ChatHub> hubContext,
    IMemoryCache cache,
    CacheKeyRegistry cacheKeys) : ControllerBase
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private static string ReceiptKey(int groupId, int staffId) => $"receipt:{groupId}:{staffId}";

    // Lấy pointer (LastReadMessageId) — check cache trước, tránh DB round-trip
    [HttpGet("receipt")]
    public async Task<IActionResult> GetReceipt([FromQuery] int groupId, [FromQuery] int staffId)
    {
        var key = ReceiptKey(groupId, staffId);

        if (cache.TryGetValue(key, out int? cachedId))
        {
            return Ok(new ReadReceiptDto { GroupId = groupId, StaffId = staffId, LastReadMessageId = cachedId });
        }

        var receipt = await dbContext.ReadReceipts
            .FirstOrDefaultAsync(r => r.GroupId == groupId && r.StaffId == staffId);

        var pointer = receipt?.LastReadMessageId;
        cache.Set(key, pointer, CacheTtl);
        cacheKeys.Track(key);  // Đăng ký key để CacheController có thể enumerate

        return Ok(new ReadReceiptDto { GroupId = groupId, StaffId = staffId, LastReadMessageId = pointer });
    }

    [HttpGet("{groupId}")]
    public async Task<IActionResult> GetMessages(int groupId, [FromQuery] int? afterMessageId)
    {
        var query = dbContext.Messages.Where(m => m.GroupId == groupId);

        if (afterMessageId.HasValue)
            query = query.Where(m => m.Id > afterMessageId.Value);

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
        var key = ReceiptKey(request.GroupId, request.StaffId);
        cache.TryGetValue(key, out int? cached);

        // Bỏ qua hoàn toàn nếu cache đã có giá trị mới hơn hoặc bằng
        if (cached.HasValue && cached.Value >= request.LastReadMessageId)
            return Ok();

        // Cập nhật cache ngay lập tức
        cache.Set(key, request.LastReadMessageId, CacheTtl);
        cacheKeys.Track(key);

        // Persist lên DB (chỉ khi thực sự có giá trị mới hơn)
        var receipt = await dbContext.ReadReceipts
            .FirstOrDefaultAsync(r => r.GroupId == request.GroupId && r.StaffId == request.StaffId);

        if (receipt == null)
        {
            dbContext.ReadReceipts.Add(new ReadReceipt
            {
                GroupId = request.GroupId,
                StaffId = request.StaffId,
                LastReadMessageId = request.LastReadMessageId
            });
        }
        else
        {
            receipt.LastReadMessageId = request.LastReadMessageId;
        }

        await dbContext.SaveChangesAsync();

        // Broadcast read receipt cho các client khác trong group
        await hubContext.Clients.Group(request.GroupId.ToString())
            .SendAsync("UserReadReceipt", new { staffId = request.StaffId, messageId = request.LastReadMessageId });

        return Ok();
    }
}
