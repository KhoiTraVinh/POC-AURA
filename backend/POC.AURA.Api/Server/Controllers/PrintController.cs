using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using POC.AURA.Api.Common.Constants;
using POC.AURA.Api.Common.Extensions;
using POC.AURA.Api.Common.Models;
using POC.AURA.Api.Data.Repositories;
using POC.AURA.Api.Server.Hubs;
using POC.AURA.Api.Service;

namespace POC.AURA.Api.Server.Controllers;

/// <summary>
/// HTTP API for the Blazor SmartHub print processor.
/// <para>All endpoints require <c>client_type = "smarthub"</c> in the JWT.</para>
/// <list type="bullet">
///   <item><c>GET  /api/print/pending</c>  — Fetch unprocessed jobs on reconnect.</item>
///   <item><c>POST /api/print/complete</c> — Report job done/failed; notifies Angular via SignalR.</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PrintController : ControllerBase
{
    private readonly IJobRepository      _jobs;
    private readonly IHubContext<AuraHub> _hub;
    private readonly IConnectionTracker  _tracker;
    private readonly ILogger<PrintController> _logger;

    private string TenantId  => User.GetTenantId();
    private string ClientType => User.GetClientType();

    public PrintController(
        IJobRepository       jobs,
        IHubContext<AuraHub> hub,
        IConnectionTracker   tracker,
        ILogger<PrintController> logger)
    {
        _jobs    = jobs;
        _hub     = hub;
        _tracker = tracker;
        _logger  = logger;
    }

    /// <summary>
    /// Returns all <c>pending</c> print jobs for the caller's tenant.
    /// Called by SmartHub on startup/reconnect to recover unprocessed work.
    /// </summary>
    [HttpGet("pending")]
    public async Task<IActionResult> GetPending()
    {
        if (ClientType != ClientTypes.SmartHub) return Forbid();

        var messages = await _jobs.GetPendingAsync(TenantId, MessageTypes.PrintJob);

        var jobs = messages.Select(m =>
        {
            var p = JsonSerializer.Deserialize<JsonElement>(m.Payload ?? "{}");
            return new PrintJob(
                m.Ref,
                m.TenantId!,
                p.TryGetProperty("documentName", out var dn) ? dn.GetString() ?? "" : "",
                p.TryGetProperty("content",      out var ct) ? ct.GetString() ?? "" : "",
                p.TryGetProperty("copies",       out var cp) ? cp.GetInt32()       : 1,
                m.RequestorConnectionId ?? "",
                m.CreatedAt);
        });

        return Ok(jobs);
    }

    /// <summary>
    /// Marks a print job as <c>completed</c> or <c>failed</c> and notifies the
    /// original Angular requestor via SignalR.
    /// </summary>
    [HttpPost("complete")]
    public async Task<IActionResult> Complete([FromBody] CompleteJobRequest req)
    {
        if (ClientType != ClientTypes.SmartHub) return Forbid();

        var message = await _jobs.FindByRefAsync(req.JobId, TenantId, MessageTypes.PrintJob);
        if (message is null)
            return NotFound(new { error = $"Job {req.JobId} not found for tenant {TenantId}" });

        await _jobs.CompleteAsync(req.JobId, TenantId, MessageTypes.PrintJob, req.Success, req.Message);

        var result = new PrintJobResult(
            message.Ref, TenantId,
            message.RequestorConnectionId ?? "",
            req.Success, req.Message, DateTime.UtcNow);

        // Route to user's current connections (handles reconnect scenario)
        var submitterConnections = _tracker.GetConnectionIds(message.RequestorUserId ?? "");

        if (submitterConnections.Count > 0)
        {
            await _hub.Clients
                .Clients(submitterConnections)
                .SendAsync(HubEvents.PrintJobComplete, result);

            await _hub.Clients
                .GroupExcept(HubGroups.Ui(TenantId), submitterConnections)
                .SendAsync(HubEvents.PrintJobStatusUpdate, result);
        }
        else
        {
            _logger.LogInformation("[PrintAPI] Requestor {UserId} is offline; broadcasting to tenant group",
                message.RequestorUserId);
            await _hub.Clients
                .Group(HubGroups.Ui(TenantId))
                .SendAsync(HubEvents.PrintJobComplete, result);
        }

        _logger.LogInformation("[PrintAPI] Job {Id} {Status} for {TenantId}",
            req.JobId, req.Success ? "completed" : "failed", TenantId);

        return Ok(new { message = "Job completion recorded" });
    }
}
