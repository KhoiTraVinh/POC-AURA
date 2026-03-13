using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace POC.AURA.Api.Hubs;

public class ChatHub : Hub
{
    // Track ConnectionId → groupId để auto-leave khi disconnect (token hết hạn, mất mạng, đóng tab)
    private static readonly ConcurrentDictionary<string, string> _connectionGroups = new();

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
}
