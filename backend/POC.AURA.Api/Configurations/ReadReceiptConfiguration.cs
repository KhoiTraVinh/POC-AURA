using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POC.AURA.Api.Entities;

namespace POC.AURA.Api.Configurations;

public class ReadReceiptConfiguration : IEntityTypeConfiguration<ReadReceipt>
{
    public void Configure(EntityTypeBuilder<ReadReceipt> builder)
    {
        builder.ToTable("ReadReceipts");

        // Composite primary key
        builder.HasKey(r => new { r.GroupId, r.StaffId });

        // Index on StaffId
        builder.HasIndex(r => r.StaffId);

        // Foreign Keys
        builder.HasOne(r => r.Group)
            .WithMany(g => g.ReadReceipts)
            .HasForeignKey(r => r.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.LastReadMessage)
            .WithMany()
            .HasForeignKey(r => r.LastReadMessageId)
            .OnDelete(DeleteBehavior.ClientSetNull);
    }
}
