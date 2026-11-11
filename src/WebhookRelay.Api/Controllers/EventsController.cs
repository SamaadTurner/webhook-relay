using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebhookRelay.Api.Data;
using WebhookRelay.Api.Models;

namespace WebhookRelay.Api.Controllers;

// Admin endpoints for looking at events and sending them again.
[ApiController]
[RequireApiKey]
public class EventsController : ControllerBase
{
    private const int MaxPageSize = 100;

    private readonly RelayDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<EventsController> _logger;

    public EventsController(RelayDbContext db, TimeProvider clock, ILogger<EventsController> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    // GET /events?status=Failed&source=github&page=1&pageSize=20
    [HttpGet("events")]
    public async Task<ActionResult<EventPage>> List(
        [FromQuery] EventStatus? status,
        [FromQuery] string? source,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (page < 1)
        {
            page = 1;
        }

        // Cap the page size so one request can't pull the whole table
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        IQueryable<WebhookEvent> query = _db.Events.AsNoTracking();

        if (status != null)
        {
            query = query.Where(e => e.Status == status);
        }

        if (!string.IsNullOrEmpty(source))
        {
            query = query.Where(e => e.Source == source);
        }

        int total = await query.CountAsync(cancellationToken);

        List<EventSummary> items = await query
            .OrderByDescending(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new EventSummary(
                e.Id, e.Source, e.DeliveryId, e.Status, e.AttemptCount,
                e.ReceivedAt, e.NextAttemptAt, e.DeliveredAt, e.LastError))
            .ToListAsync(cancellationToken);

        return new EventPage(total, page, pageSize, items);
    }

    // GET /events/42 returns the payload and every delivery attempt
    [HttpGet("events/{id:int}")]
    public async Task<ActionResult<EventDetail>> Get(int id, CancellationToken cancellationToken)
    {
        WebhookEvent? webhookEvent = await _db.Events
            .AsNoTracking()
            .Include(e => e.Attempts)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

        if (webhookEvent == null)
        {
            return NotFound();
        }

        List<AttemptSummary> attempts = webhookEvent.Attempts
            .OrderBy(a => a.AttemptedAt)
            .Select(a => new AttemptSummary(a.AttemptedAt, a.StatusCode, a.Succeeded, a.Error))
            .ToList();

        return new EventDetail(
            webhookEvent.Id, webhookEvent.Source, webhookEvent.DeliveryId, webhookEvent.Status,
            webhookEvent.AttemptCount, webhookEvent.ReceivedAt, webhookEvent.NextAttemptAt,
            webhookEvent.DeliveredAt, webhookEvent.LastError, webhookEvent.Payload, attempts);
    }

    // POST /events/42/replay puts a failed event back in the queue.
    // Typical use: the destination was down for hours, events ran out of retries,
    // and now it is fixed.
    [HttpPost("events/{id:int}/replay")]
    public async Task<IActionResult> Replay(int id, CancellationToken cancellationToken)
    {
        WebhookEvent? webhookEvent = await _db.Events.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (webhookEvent == null)
        {
            return NotFound();
        }

        if (webhookEvent.Status != EventStatus.Failed)
        {
            return Conflict(new { error = $"Only failed events can be replayed. This one is {webhookEvent.Status}." });
        }

        webhookEvent.Status = EventStatus.Pending;
        webhookEvent.AttemptCount = 0;
        webhookEvent.NextAttemptAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Event {EventId} queued for replay", id);
        return Ok(new { id, status = webhookEvent.Status });
    }

    // POST /backfill queues every finished event received in a date range to be
    // sent again. This is for when the destination lost data and needs it resent.
    // Nothing is sent here. The events go back to Pending and the worker sends
    // them like any others, so a big backfill still respects the daily budget
    // and may take more than one day.
    [HttpPost("backfill")]
    public async Task<ActionResult<BackfillResponse>> Backfill(
        [FromBody] BackfillRequest request, CancellationToken cancellationToken)
    {
        DateTime from = ToUtc(request.From);
        DateTime to = ToUtc(request.To);

        if (from >= to)
        {
            return BadRequest(new { error = "'from' must be earlier than 'to'." });
        }

        // Events that are still Pending are already on their way, so leave them alone
        IQueryable<WebhookEvent> query = _db.Events
            .Where(e => e.ReceivedAt >= from && e.ReceivedAt < to)
            .Where(e => e.Status == EventStatus.Delivered || e.Status == EventStatus.Failed);

        if (!string.IsNullOrEmpty(request.Source))
        {
            query = query.Where(e => e.Source == request.Source);
        }

        List<WebhookEvent> events = await query.ToListAsync(cancellationToken);
        DateTime now = _clock.GetUtcNow().UtcDateTime;

        foreach (WebhookEvent webhookEvent in events)
        {
            webhookEvent.Status = EventStatus.Pending;
            webhookEvent.AttemptCount = 0;
            webhookEvent.NextAttemptAt = now;
            webhookEvent.LastError = null;
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Backfill queued {Count} events received from {From} to {To}",
            events.Count, from, to);

        return new BackfillResponse(events.Count);
    }

    // Everything in the database is UTC. A date sent without a time zone is
    // treated as UTC too, and a local time is converted.
    private static DateTime ToUtc(DateTime value)
    {
        if (value.Kind == DateTimeKind.Local)
        {
            return value.ToUniversalTime();
        }

        return DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
