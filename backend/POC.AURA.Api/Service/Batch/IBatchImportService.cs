using POC.AURA.Api.Common.Dtos;

namespace POC.AURA.Api.Service.Batch;

/// <summary>
/// Orchestrates the upload pipeline: save file → count rows → persist BatchJob → enqueue Hangfire job.
/// Separates upload concerns from the Hangfire execution job (<see cref="BatchImportJob"/>).
/// </summary>
public interface IBatchImportService
{
    /// <summary>Saves the uploaded CSV, creates a BatchJob record, and enqueues execution.</summary>
    Task<BatchUploadResponse> UploadAsync(IFormFile file, string tenantId, CancellationToken ct = default);
}
