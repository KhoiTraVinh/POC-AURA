using Microsoft.EntityFrameworkCore;
using POC.AURA.Api.Entities;

namespace POC.AURA.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Group> Groups { get; set; }
    public DbSet<Member> Members { get; set; }
    public DbSet<Message> Messages { get; set; }
    public DbSet<ReadReceipt> ReadReceipts { get; set; }
    public DbSet<PrintJobRecord> PrintJobs { get; set; }
    public DbSet<BankTransactionRecord> BankTransactions { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        modelBuilder.Entity<PrintJobRecord>(b =>
        {
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasMaxLength(20);
            b.Property(e => e.TenantId).IsRequired().HasMaxLength(100);
            b.Property(e => e.DocumentName).IsRequired().HasMaxLength(500);
            b.Property(e => e.Content).HasMaxLength(4000);
            b.Property(e => e.RequestorConnectionId).HasMaxLength(100);
            b.Property(e => e.Status).HasMaxLength(20);
            b.Property(e => e.ResultMessage).HasMaxLength(500);
            b.HasIndex(e => new { e.TenantId, e.Status });
            b.ToTable("PrintJobs");
        });

        modelBuilder.Entity<BankTransactionRecord>(b =>
        {
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasMaxLength(20);
            b.Property(e => e.TenantId).IsRequired().HasMaxLength(100);
            b.Property(e => e.Description).IsRequired().HasMaxLength(500);
            b.Property(e => e.Currency).HasMaxLength(10);
            b.Property(e => e.RequestorConnectionId).HasMaxLength(100);
            b.Property(e => e.Status).HasMaxLength(20);
            b.Property(e => e.ResultMessage).HasMaxLength(500);
            b.Property(e => e.Amount).HasColumnType("decimal(18,2)");
            b.HasIndex(e => new { e.TenantId, e.Status });
            b.ToTable("BankTransactions");
        });
    }
}
