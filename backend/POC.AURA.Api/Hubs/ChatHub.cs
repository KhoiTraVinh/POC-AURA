using Microsoft.AspNetCore.SignalR;
using POC.AURA.Api.Services;

namespace POC.AURA.Api.Hubs;

public class ChatHub(IMessageService messageService) : Hub
{
    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // SignalR automatically removes the connection from all groups on disconnect.
        // No manual cleanup needed.
        await base.OnDisconnectedAsync(exception);
    }

    public async Task JoinGroup(int groupId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, groupId.ToString());
    }

    public async Task LeaveGroup(int groupId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupId.ToString());
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
