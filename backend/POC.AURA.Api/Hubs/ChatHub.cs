using Microsoft.AspNetCore.SignalR;
using POC.AURA.Api.Models;

namespace POC.AURA.Api.Hubs;

public class ChatHub : Hub
{
    private static readonly List<ChatMessage> _messageHistory = new();
    private static readonly object _lock = new();

    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("Connected", Context.ConnectionId);

        List<ChatMessage> history;
        lock (_lock)
        {
            history = [.. _messageHistory];
        }

        await Clients.Caller.SendAsync("MessageHistory", history);
        await Clients.Others.SendAsync("UserConnected", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await Clients.Others.SendAsync("UserDisconnected", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task SendMessage(string user, string message)
    {
        var chatMessage = new ChatMessage
        {
            User = user,
            Message = message,
            Timestamp = DateTime.UtcNow
        };

        lock (_lock)
        {
            _messageHistory.Add(chatMessage);
            if (_messageHistory.Count > 100)
                _messageHistory.RemoveAt(0);
        }

        await Clients.All.SendAsync("ReceiveMessage", chatMessage);
    }

    public async Task SendTyping(string user, bool isTyping)
    {
        await Clients.Others.SendAsync("UserTyping", new { user, isTyping });
    }
}
