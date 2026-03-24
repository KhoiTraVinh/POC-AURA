using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using POC.AURA.Api.Data;
using POC.AURA.Api.Hubs;
using POC.AURA.Api.Models;

namespace POC.AURA.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PrintController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IHubContext<PrintHub> _hub;
    private readonly ILogger<PrintController> _logger;

    private string TenantId => User.FindFirst("tenant_id")!.Value;
    private string ClientType => User.FindFirst("client_type")!.Value;

    public PrintController(AppDbContext db, IHubContext<PrintHub> hub, ILogger<PrintController> logger)
    {
        _db = db;
        _hub = hub;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/print/pending
    /// Called by SmartHub on startup/reconnect to retrieve unprocessed jobs for its tenant.
    /// Returns jobs with status = "pending".
    /// </summary>
    [HttpGet("pending")]
    public async Task<IActionResult> GetPending()
    {
        if (ClientType != "smarthub")
            return Forbid();

        var jobs = await _db.PrintJobs
            .Where(j => j.TenantId == TenantId && j.Status == "pending")
            .OrderBy(j => j.CreatedAt)
            .Select(j => new PrintJob(j.Id, j.TenantId, j.DocumentName, j.Content,
                j.Copies, j.RequestorConnectionId, j.CreatedAt))
            .ToListAsync();

        return Ok(jobs);
    }

    /// <summary>
    /// POST /api/print/complete
    /// Called by SmartHub to report a print job as completed or failed.
    /// Updates DB and notifies Angular UI via SignalR.
    /// </summary>
    [HttpPost("complete")]
    public async Task<IActionResult> Complete([FromBody] CompleteJobRequest req)
    {
        if (ClientType != "smarthub")
            return Forbid();

        var job = await _db.PrintJobs.FindAsync(req.JobId);
        if (job == null || job.TenantId != TenantId)
            return NotFound(new { error = $"Job {req.JobId} not found for tenant {TenantId}" });

        job.Status = req.Success ? "completed" : "failed";
        job.CompletedAt = DateTime.UtcNow;
        job.ResultMessage = req.Message;
        await _db.SaveChangesAsync();

        var result = new PrintJobResult(
            job.Id,
            job.TenantId,
            job.RequestorConnectionId,
            req.Success,
            req.Message,
            DateTime.UtcNow
        );

        // Notify the original requester directly
        await _hub.Clients.Client(job.RequestorConnectionId)
            .SendAsync("PrintJobComplete", result);

        // Broadcast to other UI clients in this tenant
        await _hub.Clients.GroupExcept($"ui-{TenantId}", [job.RequestorConnectionId])
            .SendAsync("PrintJobStatusUpdate", result);

        _logger.LogInformation("[PrintAPI] Job {Id} {Status} for {TenantId}", job.Id, job.Status, TenantId);

        return Ok(new { message = "Job completion recorded" });
    }
}
