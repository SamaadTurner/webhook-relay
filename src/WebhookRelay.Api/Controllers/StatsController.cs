using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebhookRelay.Api.Data;
using WebhookRelay.Api.Models;
using WebhookRelay.Api.Services;

namespace WebhookRelay.Api.Controllers;

[ApiController]
[RequireApiKey]
public class StatsController : ControllerBase
{
    private readonly RelayDbContext _db;
    private readonly DailyBudget _budget;

    public StatsController(RelayDbContext db, DailyBudget budget)
    {
        _db = db;
        _budget = budget;
    }

    // GET /stats is a quick look at the queue and how much of today's budget is gone
    [HttpGet("stats")]
    public async Task<StatsResponse> Get(CancellationToken cancellationToken)
    {
        // One query that counts the events in each status
        var counts = await _db.Events
            .GroupBy(e => e.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        int CountFor(EventStatus status)
        {
            return counts.FirstOrDefault(c => c.Status == status)?.Count ?? 0;
        }

        int usedToday = await _budget.GetUsedTodayAsync(cancellationToken);

        return new StatsResponse(
            CountFor(EventStatus.Pending),
            CountFor(EventStatus.Delivered),
            CountFor(EventStatus.Failed),
            usedToday,
            _budget.Limit);
    }
}
