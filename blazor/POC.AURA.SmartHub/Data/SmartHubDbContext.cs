using Microsoft.EntityFrameworkCore;
using POC.AURA.SmartHub.Data.Entities;

namespace POC.AURA.SmartHub.Data;

public class SmartHubDbContext(DbContextOptions<SmartHubDbContext> options) : DbContext(options)
{
    public DbSet<ServerConnection> ServerConnections => Set<ServerConnection>();
    public DbSet<AuthToken> AuthTokens => Set<AuthToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuthToken>()
            .HasKey(t => t.ServerConnectionId);

        modelBuilder.Entity<ServerConnection>()
            .HasOne(s => s.AuthToken)
            .WithOne(t => t.ServerConnection)
            .HasForeignKey<AuthToken>(t => t.ServerConnectionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
