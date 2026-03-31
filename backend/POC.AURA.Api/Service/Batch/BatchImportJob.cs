using Hangfire;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.SqlClient;
using POC.AURA.Api.Common.Constants;
using POC.AURA.Api.Data.Repositories;
using POC.AURA.Api.Infrastructure;
using POC.AURA.Api.Server.Hubs;

namespace POC.AURA.Api.Service.Batch;

/// <summary>
/// Hangfire background job that executes a batch CSV import.
///
/// Responsibilities (single):
///   Execute a previously-uploaded CSV into <c>ImportedRecords</c> via streaming SqlBulkCopy,
///   reporting real-time progress to Angular clients over SignalR.
///
/// Key design decisions — why single-connection streaming beats parallel inserts:
/// <code>
///   SQL Server transaction log writer is a SERIAL mutex.
///   N parallel connections → N threads queued on the same log buffer latch.
///   Result: parallel overhead > any concurrency gain.
///
///   Single connection + SqlBulkCopyOptions.TableLock enables MINIMAL LOGGING:
///   only extent allocations are logged (not per-row records) → ~10× less I/O.
///   Requirement: recovery model = SIMPLE or BULK_LOGGED (SQL Server default in dev).
/// </code>
///
/// Progress reporting:
///   SqlBulkCopy fires <c>SqlRowsCopied</c> on its internal thread every <see cref="ProgressEvery"/> rows.
///   We fire-and-forget a SignalR push from that callback — never blocking the bulk pipeline.
///
/// Retry / idempotency:
///   On Hangfire retry, <see cref="CleanupPartialRowsAsync"/> deletes any rows from the previous attempt
///   before re-inserting, keeping the table consistent.
/// </summary>
[Queue("batch-import")]
[AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 30, 60, 120 })]
public sealed class BatchImportJob(
    IBatchJobRepository     repo,
    IHubContext<AuraHub>    hub,
    IConfiguration          config,
    ILogger<BatchImportJob> logger)
{
    private const int BulkBatchSize = 50_000; // rows per SQL Server commit
    private const int ProgressEvery = 10_000; // SignalR push interval (rows)

    // ── Execute (called by Hangfire) ──────────────────────────────────────

    public async Task ExecuteAsync(string batchId, IJobCancellationToken ct)
    {
        var batch = await repo.GetByIdAsync(batchId, ct.ShutdownToken);
        if (batch is null) { logger.LogWarning("Batch {Id} not found — skipping", batchId); return; }

        // Remove any rows from a previous failed attempt (idempotent retry)
        await CleanupPartialRowsAsync(batchId);

        var connStr   = config.GetConnectionString("DefaultConnection")!;
        var startedAt = DateTime.UtcNow;

        await repo.UpdateStatusAsync(batchId, BatchStatuses.Running, ct.ShutdownToken);
        await PushProgressAsync(batch.TenantId, batchId, 0, batch.TotalRows, startedAt);

        long    inserted = 0;
        string? errorMsg = null;

        try
        {
            ct.ThrowIfCancellationRequested();

            using var fs = new FileStream(batch.FilePath, FileMode.Open,
                FileAccess.Read, FileShare.Read, bufferSize: 1 << 16, useAsync: true);
            using var sr = new StreamReader(fs);
            await sr.ReadLineAsync(ct.ShutdownToken); // skip CSV header

            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(ct.ShutdownToken);

            using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.TableLock, null)
            {
                DestinationTableName = "ImportedRecords",
                BatchSize            = BulkBatchSize,
                BulkCopyTimeout      = 0,
                NotifyAfter          = ProgressEvery,
            };
            bulk.ColumnMappings.Add(0, "BatchId");
            bulk.ColumnMappings.Add(1, "Name");
            bulk.ColumnMappings.Add(2, "Category");
            bulk.ColumnMappings.Add(3, "Value");
            bulk.ColumnMappings.Add(4, "Timestamp");
            bulk.ColumnMappings.Add(5, "ImportedAt");

            bulk.SqlRowsCopied += (_, e) =>
            {
                ct.ThrowIfCancellationRequested();
                inserted = e.RowsCopied;
                _ = PushProgressAsync(batch.TenantId, batchId, inserted, batch.TotalRows, startedAt);
            };

            using var dr = new CsvDataReader(sr, batchId);
            await bulk.WriteToServerAsync(dr, ct.ShutdownToken);

            inserted = batch.TotalRows;
        }
        catch (OperationCanceledException)
        {
            await CancelAsync(batchId, batch.TenantId);
            return;
        }
        catch (Exception ex)
        {
            errorMsg = ex.Message;
            logger.LogError(ex, "Batch {Id} failed after {Rows} rows", batchId, inserted);
        }

        await FinalizeAndNotifyAsync(batchId, batch.TenantId, inserted, errorMsg,
            (long)(DateTime.UtcNow - startedAt).TotalMilliseconds);
    }

    // ── Cancel (called by BatchController) ───────────────────────────────

    public async Task CancelAsync(string batchId, string tenantId)
    {
        await repo.FinalizeAsync(batchId, BatchStatuses.Cancelled, 0);
        await CleanupPartialRowsAsync(batchId);
        DeleteTempFile(batchId);

        await hub.Clients.Group(HubGroups.Ui(tenantId))
            .SendAsync(HubEvents.BatchCancelled, new { batchId });
        logger.LogInformation("Batch {Id} cancelled", batchId);
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private async Task FinalizeAndNotifyAsync(
        string batchId, string tenantId, long inserted, string? errorMsg, long durationMs)
    {
        if (errorMsg is null)
        {
            await repo.FinalizeAsync(batchId, BatchStatuses.Completed, (int)inserted);
            var rps = durationMs > 0 ? inserted * 1000 / durationMs : 0;
            await hub.Clients.Group(HubGroups.Ui(tenantId))
                .SendAsync(HubEvents.BatchCompleted,
                    new { batchId, processedRows = inserted, durationMs, rowsPerSecond = rps });
            logger.LogInformation("Batch {Id}: {Rows} rows in {Ms}ms ({Rps} r/s)",
                batchId, inserted, durationMs, rps);
        }
        else
        {
            await repo.FinalizeAsync(batchId, BatchStatuses.Failed, (int)inserted, errorMsg);
            await hub.Clients.Group(HubGroups.Ui(tenantId))
                .SendAsync(HubEvents.BatchFailed, new { batchId, error = errorMsg });
            throw new InvalidOperationException($"Batch {batchId} failed: {errorMsg}");
        }
    }

    private async Task PushProgressAsync(string tenantId, string batchId, long done, int total, DateTime startedAt)
    {
        var elapsed = Math.Max(1, (DateTime.UtcNow - startedAt).TotalSeconds);
        var pct     = total > 0 ? (int)(done * 100 / total) : 0;
        var rps     = (int)(done / elapsed);
        await hub.Clients.Group(HubGroups.Ui(tenantId))
            .SendAsync(HubEvents.BatchProgress,
                new { batchId, processedRows = done, totalRows = total, percent = pct, rowsPerSecond = rps });
    }

    private async Task CleanupPartialRowsAsync(string batchId)
    {
        var connStr = config.GetConnectionString("DefaultConnection")!;
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("DELETE FROM ImportedRecords WHERE BatchId = @b", conn);
        cmd.Parameters.AddWithValue("@b", batchId);
        var deleted = await cmd.ExecuteNonQueryAsync();
        if (deleted > 0)
            logger.LogInformation("Batch {Id}: removed {N} partial rows", batchId, deleted);
    }

    private void DeleteTempFile(string batchId)
    {
        var path = Path.Combine(Path.GetTempPath(), "aura-batch-uploads", $"{batchId}.csv");
        if (File.Exists(path)) File.Delete(path);
    }
}
