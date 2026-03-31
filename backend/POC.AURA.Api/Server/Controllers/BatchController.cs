using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POC.AURA.Api.Common.Constants;
using POC.AURA.Api.Common.Dtos;
using POC.AURA.Api.Common.Extensions;
using POC.AURA.Api.Data;
using POC.AURA.Api.Data.Repositories;
using POC.AURA.Api.Service.Batch;

namespace POC.AURA.Api.Server.Controllers;

/// <summary>
/// REST API for the batch CSV import feature.
///
/// Responsibilities (thin controller):
///   Route HTTP requests → delegate to <see cref="IBatchImportService"/> or <see cref="BatchImportJob"/>.
///   Map results to HTTP responses.
///   No business logic, no direct DB access.
/// </summary>
[ApiController]
[Route("api/batch")]
[Authorize]
public sealed class BatchController(
    IBatchImportService  importService,
    IBatchJobRepository  repo,
    BatchImportJob       importJob,
    AppDbContext         db) : ControllerBase
{
    // ── POST /api/batch/upload ────────────────────────────────────────────

    [HttpPost("upload")]
    [RequestSizeLimit(500 * 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "No file provided." });
        if (!file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Only CSV files are supported." });

        var response = await importService.UploadAsync(file, User.GetTenantId(), ct);
        return Ok(response);
    }

    // ── DELETE /api/batch/{batchId} — cancel ─────────────────────────────

    [HttpDelete("{batchId}")]
    public async Task<IActionResult> Cancel(string batchId, CancellationToken ct)
    {
        var batch = await repo.GetByIdForTenantAsync(batchId, User.GetTenantId(), ct);
        if (batch is null) return NotFound();

        if (batch.Status is BatchStatuses.Completed or BatchStatuses.Cancelled)
            return BadRequest(new { error = $"Cannot cancel a {batch.Status} job." });

        if (batch.HangfireJobId is not null)
            BackgroundJob.Delete(batch.HangfireJobId);

        await importJob.CancelAsync(batchId, batch.TenantId);
        return Ok(new { batchId, status = BatchStatuses.Cancelled });
    }

    // ── GET /api/batch/{batchId} — status ────────────────────────────────

    [HttpGet("{batchId}")]
    public async Task<IActionResult> Status(string batchId, CancellationToken ct)
    {
        var batch = await repo.GetByIdForTenantAsync(batchId, User.GetTenantId(), ct);
        if (batch is null) return NotFound();
        return Ok(ToDto(batch));
    }

    // ── GET /api/batch — list ─────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var list = await repo.ListForTenantAsync(User.GetTenantId(), ct: ct);
        return Ok(list.Select(ToDto));
    }

    // ── GET /api/batch/{batchId}/records — paginated data viewer ─────────

    [HttpGet("{batchId}/records")]
    public async Task<IActionResult> Records(
        string  batchId,
        [FromQuery] int     page     = 1,
        [FromQuery] int     pageSize = 100,
        [FromQuery] string? search   = null,
        [FromQuery] string? category = null,
        CancellationToken ct = default)
    {
        var batch = await repo.GetByIdForTenantAsync(batchId, User.GetTenantId(), ct);
        if (batch is null) return NotFound();

        var query = db.ImportedRecords.Where(r => r.BatchId == batchId);
        if (!string.IsNullOrWhiteSpace(search))   query = query.Where(r => r.Name.Contains(search));
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(r => r.Category == category);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(r => r.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new RecordDto(r.Id, r.Name, r.Category, r.Value, r.Timestamp, r.ImportedAt))
            .ToListAsync(ct);

        return Ok(new RecordsPageDto(total, page, pageSize,
            (int)Math.Ceiling((double)total / pageSize), items));
    }

    // ── Mapping ───────────────────────────────────────────────────────────

    private static BatchJobDto ToDto(Data.Entities.BatchJob b) => new(
        b.Id, b.FileName, b.FileSizeBytes, b.TotalRows, b.ProcessedRows,
        b.TotalRows > 0 ? b.ProcessedRows * 100 / b.TotalRows : 0,
        b.Status, b.CreatedAt, b.CompletedAt, b.ErrorMessage);
}
