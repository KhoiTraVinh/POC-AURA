namespace POC.AURA.Api.Common.Dtos;

/// <summary>Response after successfully uploading a CSV file.</summary>
public record BatchUploadResponse(
    string BatchId,
    string HangfireJobId,
    string FileName,
    int    TotalRows);

/// <summary>Single batch job summary returned in list and status endpoints.</summary>
public record BatchJobDto(
    string    Id,
    string    FileName,
    long      FileSizeBytes,
    int       TotalRows,
    int       ProcessedRows,
    int       Percent,
    string    Status,
    DateTime  CreatedAt,
    DateTime? CompletedAt,
    string?   ErrorMessage = null);

/// <summary>Paginated result for GET /api/batch/{id}/records.</summary>
public record RecordsPageDto(
    int                    Total,
    int                    Page,
    int                    PageSize,
    int                    TotalPages,
    IReadOnlyList<RecordDto> Items);

/// <summary>Single imported record row.</summary>
public record RecordDto(
    long     Id,
    string   Name,
    string   Category,
    decimal  Value,
    DateTime Timestamp,
    DateTime ImportedAt);
