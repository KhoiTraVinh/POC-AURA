using Microsoft.EntityFrameworkCore;

namespace POC.AURA.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
    }
}
