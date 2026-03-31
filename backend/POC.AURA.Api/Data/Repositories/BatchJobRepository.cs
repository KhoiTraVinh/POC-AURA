using Microsoft.EntityFrameworkCore;
using POC.AURA.Api.Data.Entities;

namespace POC.AURA.Api.Data.Repositories;

/// <inheritdoc/>
public sealed class BatchJobRepository(AppDbContext db) : IBatchJobRepository
{
    public Task<BatchJob?> GetByIdAsync(string batchId, CancellationToken ct = default) =>
        db.BatchJobs.FirstOrDefaultAsync(b => b.Id == batchId, ct);

    public Task<BatchJob?> GetByIdForTenantAsync(string batchId, string tenantId, CancellationToken ct = default) =>
        db.BatchJobs.AsNoTracking()
          .FirstOrDefaultAsync(b => b.Id == batchId && b.TenantId == tenantId, ct);

    public async Task<IReadOnlyList<BatchJob>> ListForTenantAsync(string tenantId, int take = 20, CancellationToken ct = default) =>
        await db.BatchJobs.AsNoTracking()
                .Where(b => b.TenantId == tenantId)
                .OrderByDescending(b => b.CreatedAt)
                .Take(take)
                .ToListAsync(ct);

    public async Task AddAsync(BatchJob batch, CancellationToken ct = default)
    {
        db.BatchJobs.Add(batch);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateStatusAsync(string batchId, string status, CancellationToken ct = default)
    {
        var batch = await db.BatchJobs.FindAsync([batchId], ct);
        if (batch is null) return;
        batch.Status = status;
        await db.SaveChangesAsync(ct);
    }

    public async Task FinalizeAsync(string batchId, string status, int processedRows, string? error = null, CancellationToken ct = default)
    {
        var batch = await db.BatchJobs.FindAsync([batchId], ct);
        if (batch is null) return;
        batch.Status        = status;
        batch.ProcessedRows = processedRows;
        batch.ErrorMessage  = error;
        batch.CompletedAt   = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetHangfireJobIdAsync(string batchId, string hangfireJobId, CancellationToken ct = default)
    {
        var batch = await db.BatchJobs.FindAsync([batchId], ct);
        if (batch is null) return;
        batch.HangfireJobId = hangfireJobId;
        await db.SaveChangesAsync(ct);
    }
}
