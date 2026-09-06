using GatewayApi.Models;
using Microsoft.EntityFrameworkCore;

namespace GatewayApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Project> Projects => Set<Project>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // These are storage-level guardrails, not replacements for endpoint
        // validation. They protect imports, tests, and any future caller that
        // reaches the DbContext without traversing the HTTP contract.
        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Owner).HasMaxLength(100).IsRequired();
            // Preserve UTC sub-second precision so ordering/correlation does not
            // collapse events created within the same MySQL second.
            entity.Property(e => e.CreatedAt).HasColumnType("datetime(6)");
        });
    }
}
