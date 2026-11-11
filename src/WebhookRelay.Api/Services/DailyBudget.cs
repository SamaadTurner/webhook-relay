using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebhookRelay.Api.Data;
using WebhookRelay.Api.Models;
using WebhookRelay.Api.Options;

namespace WebhookRelay.Api.Services;

// Keeps track of how many calls we have made to the destination today.
// The count lives in the database, not in memory, so it survives a restart.
// Otherwise a crash in the afternoon would reset the count and we could go
// over the destination's limit.
public class DailyBudget
{
    private readonly RelayDbContext _db;
    private readonly RelayOptions _options;
    private readonly TimeProvider _clock;

    public DailyBudget(RelayDbContext db, IOptions<RelayOptions> options, TimeProvider clock)
    {
        _db = db;
        _options = options.Value;
        _clock = clock;
    }

    public int Limit => _options.DailyCallBudget;

    // Uses up one call if there is one left today. Returns false when the budget is spent.
    // The call is counted before it is made, so a crash mid-call can only make us
    // count one too many, never one too few.
    public async Task<bool> TrySpendAsync(CancellationToken cancellationToken)
    {
        DateOnly today = Today();

        DailyUsage? usage = await _db.DailyUsage.FirstOrDefaultAsync(u => u.Day == today, cancellationToken);
        if (usage == null)
        {
            // First call of the day
            usage = new DailyUsage { Day = today, CallsUsed = 0 };
            _db.DailyUsage.Add(usage);
        }

        if (usage.CallsUsed >= _options.DailyCallBudget)
        {
            return false;
        }

        usage.CallsUsed++;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> GetUsedTodayAsync(CancellationToken cancellationToken)
    {
        DateOnly today = Today();

        DailyUsage? usage = await _db.DailyUsage
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Day == today, cancellationToken);

        return usage?.CallsUsed ?? 0;
    }

    private DateOnly Today()
    {
        return DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
    }
}
