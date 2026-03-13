using Microsoft.AspNetCore.SignalR;

namespace POC.AURA.Api.Hubs;

public class ChatHub : Hub
{
    private static readonly object _lock = new();

    public override async Task OnConnectedAsync()
    {
       
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await Clients.Others.SendAsync("UserDisconnected", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task SendMessage(string user, string message)
    {
        
        await Clients.All.SendAsync("ReceiveMessage");
    }

    public async Task SendTyping(string user, bool isTyping)
    {
        await Clients.Others.SendAsync("UserTyping", new { user, isTyping });
    }
}
