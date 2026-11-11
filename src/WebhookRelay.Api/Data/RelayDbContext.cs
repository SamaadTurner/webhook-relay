using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
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

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite stores dates as text with no time zone. We only ever save UTC,
        // so mark every date we read back as UTC. Without this the API would
        // return times without the "Z" on the end.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

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

    private class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
    {
        public UtcDateTimeConverter()
            : base(
                value => value,
                value => DateTime.SpecifyKind(value, DateTimeKind.Utc))
        {
        }
    }
}
