using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POC.AURA.Api.Data.Entities;

namespace POC.AURA.Api.Data.Configurations;

public class BatchJobConfiguration : IEntityTypeConfiguration<BatchJob>
{
    public void Configure(EntityTypeBuilder<BatchJob> builder)
    {
        builder.ToTable("BatchJobs");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).HasMaxLength(50);
        builder.Property(b => b.TenantId).IsRequired().HasMaxLength(100);
        builder.Property(b => b.FileName).IsRequired().HasMaxLength(260);
        builder.Property(b => b.FilePath).IsRequired().HasMaxLength(500);
        builder.Property(b => b.Status).IsRequired().HasMaxLength(20);
        builder.Property(b => b.CreatedAt).HasDefaultValueSql("GETUTCDATE()");

        builder.HasMany(b => b.Checkpoints)
               .WithOne(c => c.Batch)
               .HasForeignKey(c => c.BatchId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

public class BatchCheckpointConfiguration : IEntityTypeConfiguration<BatchCheckpoint>
{
    public void Configure(EntityTypeBuilder<BatchCheckpoint> builder)
    {
        builder.ToTable("BatchCheckpoints");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.BatchId).IsRequired().HasMaxLength(50);
        builder.Property(c => c.CompletedAt).HasDefaultValueSql("GETUTCDATE()");
    }
}

public class ImportedRecordConfiguration : IEntityTypeConfiguration<ImportedRecord>
{
    public void Configure(EntityTypeBuilder<ImportedRecord> builder)
    {
        builder.ToTable("ImportedRecords");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.BatchId).IsRequired().HasMaxLength(50);
        builder.Property(r => r.Name).IsRequired().HasMaxLength(200);
        builder.Property(r => r.Category).IsRequired().HasMaxLength(100);
        builder.Property(r => r.Value).HasColumnType("decimal(18,2)");
        builder.Property(r => r.ImportedAt).HasDefaultValueSql("GETUTCDATE()");

        builder.HasIndex(r => r.BatchId).HasDatabaseName("IX_ImportedRecords_BatchId");
    }
}
