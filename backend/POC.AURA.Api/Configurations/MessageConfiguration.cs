using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POC.AURA.Api.Entities;

namespace POC.AURA.Api.Configurations;

public class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("Messages");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Type).IsRequired().HasMaxLength(50);
        builder.Property(m => m.Ref).IsRequired().HasMaxLength(100);
        builder.Property(m => m.CreatedAt).HasDefaultValueSql("GETUTCDATE()");

        builder.Property(m => m.TenantId).HasMaxLength(100);
        builder.Property(m => m.Payload);
        builder.Property(m => m.Status).HasMaxLength(20);
        builder.Property(m => m.RequestorUserId).HasMaxLength(200);
        builder.Property(m => m.RequestorConnectionId).HasMaxLength(100);
        builder.Property(m => m.ResultMessage).HasMaxLength(500);

        builder.HasIndex(m => new { m.TenantId, m.Type, m.Status });
    }
}
