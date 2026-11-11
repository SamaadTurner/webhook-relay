using Microsoft.EntityFrameworkCore;
using WebhookRelay.Api.Models;

namespace WebhookRelay.Api.Data;

public class RelayDbContext : DbContext
{
    public RelayDbContext(DbContextOptions<RelayDbContext> options) : base(options)
    {
    }

    public DbSet<WebhookEvent> Events => Set<WebhookEvent>();
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();
    public DbSet<DailyUsage> DailyUsage => Set<DailyUsage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WebhookEvent>(entity =>
        {
            entity.Property(e => e.Source).HasMaxLength(100);
            entity.Property(e => e.DeliveryId).HasMaxLength(200);

            // The database enforces "one row per delivery" so a duplicate
            // can't slip in even if two requests arrive at the same moment.
            entity.HasIndex(e => new { e.Source, e.DeliveryId }).IsUnique();

            // The worker looks up events by status and due time on every poll
            entity.HasIndex(e => new { e.Status, e.NextAttemptAt });

            entity.HasMany(e => e.Attempts)
                .WithOne()
                .HasForeignKey(a => a.WebhookEventId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DailyUsage>(entity =>
        {
            entity.HasKey(u => u.Day);
        });
    }
}
