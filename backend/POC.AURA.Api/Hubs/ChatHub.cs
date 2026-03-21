using Microsoft.AspNetCore.SignalR;
using POC.AURA.Api.Infrastructure;
using POC.AURA.Api.Services;

namespace POC.AURA.Api.Hubs;

public class ChatHub(IConnectionManager connectionManager, IMessageService messageService) : Hub
{
    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Auto-leave group — avoid receiving signals after connection is lost
        var groupId = connectionManager.GetGroupId(Context.ConnectionId);
        if (groupId != null)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupId);
            connectionManager.RemoveConnection(Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task JoinGroup(int groupId)
    {
        var groupName = groupId.ToString();
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        connectionManager.AddConnection(Context.ConnectionId, groupName);
    }

    public async Task LeaveGroup(int groupId)
    {
        var groupName = groupId.ToString();
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
        connectionManager.RemoveConnection(Context.ConnectionId);
    }

    /// <summary>
    /// Update read pointer + broadcast UserReadReceipt to other clients in the group.
    /// Replaces HTTP POST /api/messages/read
    /// </summary>
    public async Task MarkRead(int groupId, int staffId, int lastReadMessageId)
    {
        await messageService.UpdateReadReceiptAsync(groupId, staffId, lastReadMessageId);
    }
}
