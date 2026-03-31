using POC.AURA.Api.Data.Entities;

namespace POC.AURA.Api.Data.Repositories;

/// <summary>
/// Persistence operations for <see cref="BatchJob"/>.
/// Keeps all BatchJob DB logic in one place — controllers and services stay DB-agnostic.
/// </summary>
public interface IBatchJobRepository
{
    Task<BatchJob?>              GetByIdAsync(string batchId, CancellationToken ct = default);
    Task<BatchJob?>              GetByIdForTenantAsync(string batchId, string tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<BatchJob>> ListForTenantAsync(string tenantId, int take = 20, CancellationToken ct = default);
    Task                         AddAsync(BatchJob batch, CancellationToken ct = default);
    Task                         UpdateStatusAsync(string batchId, string status, CancellationToken ct = default);
    Task                         FinalizeAsync(string batchId, string status, int processedRows, string? error = null, CancellationToken ct = default);
    Task                         SetHangfireJobIdAsync(string batchId, string hangfireJobId, CancellationToken ct = default);
}
