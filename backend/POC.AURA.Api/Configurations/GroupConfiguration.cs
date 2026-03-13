using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POC.AURA.Api.Entities;

namespace POC.AURA.Api.Configurations;

public class GroupConfiguration : IEntityTypeConfiguration<Group>
{
    public void Configure(EntityTypeBuilder<Group> builder)
    {
        builder.ToTable("Groups");

        builder.HasKey(g => g.Id);

        builder.Property(g => g.GroupName)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(g => g.CreatedAt)
            .HasDefaultValueSql("GETUTCDATE()");
    }
}
