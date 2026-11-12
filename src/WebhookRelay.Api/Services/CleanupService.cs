using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebhookRelay.Api.Data;
using WebhookRelay.Api.Models;
using WebhookRelay.Api.Options;

namespace WebhookRelay.Api.Services;

// Deletes delivered events once they are older than the retention period, so the
// database doesn't grow forever. The cleanup worker calls this on a timer.
// Like DeliveryService, it is its own class so tests can call it directly.
public class CleanupService
{
    private readonly RelayDbContext _db;
    private readonly RelayOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<CleanupService> _logger;

    public CleanupService(
        RelayDbContext db,
        IOptions<RelayOptions> options,
        TimeProvider clock,
        ILogger<CleanupService> logger)
    {
        _db = db;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    // Returns how many events were deleted.
    public async Task<int> DeleteOldEventsAsync(CancellationToken cancellationToken)
    {
        if (_options.RetentionDays <= 0)
        {
            // 0 means "keep everything"
            return 0;
        }

        DateTime cutoff = _clock.GetUtcNow().UtcDateTime.AddDays(-_options.RetentionDays);

        // Only Delivered events are removed. Pending ones still have to be sent, and
        // Failed ones are kept so someone can look at what went wrong and replay them.
        // The delivery attempts go with their event because of the cascade delete
        // on the foreign key.
        int deleted = await _db.Events
            .Where(e => e.Status == EventStatus.Delivered && e.DeliveredAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted > 0)
        {
            _logger.LogInformation(
                "Cleanup deleted {Count} events delivered before {Cutoff}", deleted, cutoff);
        }

        return deleted;
    }
}
