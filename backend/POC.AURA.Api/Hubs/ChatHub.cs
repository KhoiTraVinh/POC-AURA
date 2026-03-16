using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using POC.AURA.Api.Data;
using POC.AURA.Api.DTOs;
using POC.AURA.Api.Entities;
using POC.AURA.Api.Infrastructure;

namespace POC.AURA.Api.Hubs;

public class ChatHub(AppDbContext dbContext, IMemoryCache cache, CacheKeyRegistry cacheKeys) : Hub
{
    // Track ConnectionId → groupId để auto-leave khi disconnect (token hết hạn, mất mạng, đóng tab)
    private static readonly ConcurrentDictionary<string, string> _connectionGroups = new();

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private static string ReceiptKey(int groupId, int staffId) => $"receipt:{groupId}:{staffId}";

    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Tự động rời group — tránh nhận signal thừa sau khi mất kết nối
        if (_connectionGroups.TryRemove(Context.ConnectionId, out var groupId))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task JoinGroup(int groupId)
    {
        var groupName = groupId.ToString();
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        _connectionGroups[Context.ConnectionId] = groupName;
    }

    public async Task LeaveGroup(int groupId)
    {
        var groupName = groupId.ToString();
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
        _connectionGroups.TryRemove(Context.ConnectionId, out _);
    }

    /// <summary>
    /// Cập nhật pointer đã đọc + broadcast UserReadReceipt cho các client khác trong group.
    /// Thay thế HTTP POST /api/messages/read
    /// </summary>
    public async Task MarkRead(int groupId, int staffId, int lastReadMessageId)
    {
        var key = ReceiptKey(groupId, staffId);
        cache.TryGetValue(key, out int? cached);

        // Bỏ qua nếu pointer không tiến lên
        if (cached.HasValue && cached.Value >= lastReadMessageId)
            return;

        // Cập nhật cache ngay lập tức + đăng ký key
        cache.Set(key, (int?)lastReadMessageId, CacheTtl);
        cacheKeys.Track(key);

        // Persist DB
        var receipt = await dbContext.ReadReceipts
            .FirstOrDefaultAsync(r => r.GroupId == groupId && r.StaffId == staffId);

        if (receipt is null)
        {
            dbContext.ReadReceipts.Add(new ReadReceipt
            {
                GroupId = groupId,
                StaffId = staffId,
                LastReadMessageId = lastReadMessageId
            });
        }
        else
        {
            receipt.LastReadMessageId = lastReadMessageId;
        }

        await dbContext.SaveChangesAsync();

        // Broadcast cho tất cả client trong group (kể cả caller để đồng bộ multi-tab)
        await Clients.Group(groupId.ToString())
            .SendAsync("UserReadReceipt", new { staffId, messageId = lastReadMessageId });
    }
}
