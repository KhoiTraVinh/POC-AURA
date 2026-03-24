using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using POC.AURA.Api.Data;
using POC.AURA.Api.Entities;
using POC.AURA.Api.Models;

namespace POC.AURA.Api.Hubs;

/// <summary>
/// Multi-tenant hub for print job routing.
/// Groups: ui-{tenantId} for Angular UI clients, smarthub-{tenantId} for Blazor SmartHub clients.
/// Print jobs are saved to DB before routing — SmartHub can recover pending jobs on reconnect.
/// Completion is reported via HTTP API (POST /api/print/complete), not via hub invoke.
/// </summary>
[Authorize]
public class PrintHub : Hub
{
    private readonly AppDbContext _db;
    private readonly ILogger<PrintHub> _logger;

    private string TenantId => Context.User!.FindFirst("tenant_id")!.Value;
    private string ClientType => Context.User!.FindFirst("client_type")!.Value;
    private string UserId => Context.User!.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)!.Value;
    private string UserName => Context.User!.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Name)!.Value;

    public PrintHub(AppDbContext db, ILogger<PrintHub> logger)
    {
        _db = db;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var group = $"{ClientType}-{TenantId}";
        await Groups.AddToGroupAsync(Context.ConnectionId, group);

        await Clients.Group($"ui-{TenantId}").SendAsync("ClientConnected", new
        {
            ConnectionId = Context.ConnectionId,
            TenantId,
            ClientType,
            UserId,
            UserName,
            Timestamp = DateTime.UtcNow
        });

        _logger.LogInformation("[PrintHub] {ClientType} connected for {TenantId}", ClientType, TenantId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"{ClientType}-{TenantId}");

        await Clients.Group($"ui-{TenantId}").SendAsync("ClientDisconnected", new
        {
            ConnectionId = Context.ConnectionId,
            TenantId,
            ClientType,
            UserId
        });

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Called by Angular UI: submit a print job.
    /// Saves to DB (pending), routes to smarthub-{tenantId} group.
    /// Completion comes back via HTTP POST /api/print/complete from SmartHub.
    /// </summary>
    public async Task SubmitPrintJob(PrintJobRequest request)
    {
        var id = Guid.NewGuid().ToString("N")[..10].ToUpper();

        var record = new PrintJobRecord
        {
            Id = id,
            TenantId = TenantId,
            DocumentName = request.DocumentName,
            Content = request.Content,
            Copies = request.Copies,
            RequestorConnectionId = Context.ConnectionId,
            Status = "pending",
            CreatedAt = DateTime.UtcNow
        };
        _db.PrintJobs.Add(record);
        await _db.SaveChangesAsync();

        var job = new PrintJob(id, TenantId, request.DocumentName, request.Content,
            request.Copies, Context.ConnectionId, DateTime.UtcNow);

        // Route to SmartHub of this tenant only
        await Clients.Group($"smarthub-{TenantId}").SendAsync("ExecutePrintJob", job);

        // Confirm to caller that job was queued
        await Clients.Caller.SendAsync("PrintJobQueued", job);

        _logger.LogInformation("[PrintHub] Job {Id} queued for {TenantId}", id, TenantId);
    }
}
