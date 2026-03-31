using Hangfire;
using POC.AURA.Api.Common.Constants;
using POC.AURA.Api.Common.Dtos;
using POC.AURA.Api.Data.Entities;
using POC.AURA.Api.Data.Repositories;

namespace POC.AURA.Api.Service.Batch;

/// <inheritdoc/>
public sealed class BatchImportService(
    IBatchJobRepository      repo,
    IBackgroundJobClient     hangfire,
    ILogger<BatchImportService> logger) : IBatchImportService
{
    private static readonly string UploadDir =
        Path.Combine(Path.GetTempPath(), "aura-batch-uploads");

    /// <inheritdoc/>
    public async Task<BatchUploadResponse> UploadAsync(IFormFile file, string tenantId, CancellationToken ct = default)
    {
        var batchId  = GenerateBatchId();
        var filePath = await SaveFileAsync(file, batchId, ct);
        var total    = await CountRowsAsync(filePath, ct);

        var batch = new BatchJob
        {
            Id            = batchId,
            TenantId      = tenantId,
            FileName      = file.FileName,
            FilePath      = filePath,
            FileSizeBytes = file.Length,
            TotalRows     = total,
            Status        = BatchStatuses.Queued,
            CreatedAt     = DateTime.UtcNow,
        };
        await repo.AddAsync(batch, ct);

        var hangfireJobId = hangfire.Enqueue<BatchImportJob>(j => j.ExecuteAsync(batchId, null!));
        await repo.SetHangfireJobIdAsync(batchId, hangfireJobId, ct);

        logger.LogInformation("Batch {Id} queued: {File} ({Rows} rows)", batchId, file.FileName, total);
        return new BatchUploadResponse(batchId, hangfireJobId, file.FileName, total);
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private static string GenerateBatchId() =>
        Guid.NewGuid().ToString("N")[..16].ToUpper();

    private static async Task<string> SaveFileAsync(IFormFile file, string batchId, CancellationToken ct)
    {
        Directory.CreateDirectory(UploadDir);
        var path = Path.Combine(UploadDir, $"{batchId}.csv");
        await using var fs = new FileStream(path, FileMode.Create);
        await file.CopyToAsync(fs, ct);
        return path;
    }

    /// <summary>Counts data rows by streaming the file once — no full load into memory.</summary>
    private static async Task<int> CountRowsAsync(string filePath, CancellationToken ct)
    {
        var count = 0;
        using var reader = new StreamReader(filePath);
        await reader.ReadLineAsync(ct); // skip header
        while (await reader.ReadLineAsync(ct) is not null) count++;
        return count;
    }
}
