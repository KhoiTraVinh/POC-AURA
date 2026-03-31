namespace POC.AURA.Api.Data.Entities;

public class BatchJob
{
    public string   Id             { get; set; } = null!;  // GUID string
    public string   TenantId       { get; set; } = null!;
    public string   FileName       { get; set; } = null!;
    public string   FilePath       { get; set; } = null!;
    public long     FileSizeBytes  { get; set; }
    public int      TotalRows      { get; set; }
    public int      ProcessedRows  { get; set; }
    public string   Status         { get; set; } = "queued"; // queued|running|completed|failed|cancelled
    public string?  HangfireJobId  { get; set; }
    public string?  ErrorMessage   { get; set; }
    public DateTime CreatedAt      { get; set; }
    public DateTime? CompletedAt   { get; set; }

    public List<BatchCheckpoint> Checkpoints { get; set; } = [];
}

public class BatchCheckpoint
{
    public int      Id           { get; set; }
    public string   BatchId      { get; set; } = null!;
    public int      ChunkIndex   { get; set; }
    public int      RowsInserted { get; set; }
    public DateTime CompletedAt  { get; set; }

    public BatchJob Batch { get; set; } = null!;
}

public class ImportedRecord
{
    public long     Id         { get; set; }
    public string   BatchId    { get; set; } = null!;
    public string   Name       { get; set; } = null!;
    public string   Category   { get; set; } = null!;
    public decimal  Value      { get; set; }
    public DateTime Timestamp  { get; set; }
    public DateTime ImportedAt { get; set; }
}
