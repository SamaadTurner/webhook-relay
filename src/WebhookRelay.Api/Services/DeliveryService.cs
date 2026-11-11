using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebhookRelay.Api.Data;
using WebhookRelay.Api.Models;
using WebhookRelay.Api.Options;

namespace WebhookRelay.Api.Services;

// Sends saved events to the destination. The background worker calls this on a timer.
// It is its own class (not inside the worker) so tests can call it directly.
public class DeliveryService
{
    public const string SourceHeader = "X-Relay-Source";
    public const string DeliveryIdHeader = "X-Relay-Delivery-Id";

    private readonly RelayDbContext _db;
    private readonly HttpClient _http;
    private readonly DailyBudget _budget;
    private readonly RelayOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<DeliveryService> _logger;

    public DeliveryService(
        RelayDbContext db,
        HttpClient http,
        DailyBudget budget,
        IOptions<RelayOptions> options,
        TimeProvider clock,
        ILogger<DeliveryService> logger)
    {
        _db = db;
        _http = http;
        _budget = budget;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    // Sends every event that is due, up to the batch size and today's budget.
    // Returns how many calls were made to the destination.
    public async Task<int> ProcessDueEventsAsync(CancellationToken cancellationToken)
    {
        DateTime now = _clock.GetUtcNow().UtcDateTime;

        List<WebhookEvent> dueEvents = await _db.Events
            .Where(e => e.Status == EventStatus.Pending && e.NextAttemptAt <= now)
            .OrderBy(e => e.NextAttemptAt)
            .ThenBy(e => e.Id)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        int callsMade = 0;

        foreach (WebhookEvent webhookEvent in dueEvents)
        {
            if (!await _budget.TrySpendAsync(cancellationToken))
            {
                // Out of calls for today. The rest stay Pending and go out after midnight UTC.
                _logger.LogWarning("Daily budget of {Limit} calls is used up. Waiting until tomorrow.", _budget.Limit);
                break;
            }

            await SendAsync(webhookEvent, cancellationToken);
            callsMade++;
        }

        return callsMade;
    }

    private async Task SendAsync(WebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        var attempt = new DeliveryAttempt
        {
            WebhookEventId = webhookEvent.Id,
            AttemptedAt = _clock.GetUtcNow().UtcDateTime
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.DestinationUrl)
            {
                Content = new StringContent(webhookEvent.Payload, Encoding.UTF8, "application/json")
            };

            // The destination can use the delivery ID to ignore an event it has already
            // handled. That matters because we may send the same event more than once
            // (see the README section on crashes).
            request.Headers.Add(SourceHeader, webhookEvent.Source);
            request.Headers.Add(DeliveryIdHeader, webhookEvent.DeliveryId);

            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);

            attempt.StatusCode = (int)response.StatusCode;
            attempt.Succeeded = response.IsSuccessStatusCode;
            if (!attempt.Succeeded)
            {
                attempt.Error = $"Destination returned {(int)response.StatusCode}";
            }
        }
        catch (HttpRequestException ex)
        {
            // Could not connect, DNS failure, connection dropped, ...
            attempt.Succeeded = false;
            attempt.Error = ex.Message;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient throws this on a timeout. If the app itself is shutting down
            // the token is cancelled and we let the exception go up.
            attempt.Succeeded = false;
            attempt.Error = "Request timed out";
        }

        _db.DeliveryAttempts.Add(attempt);
        webhookEvent.AttemptCount++;

        if (attempt.Succeeded)
        {
            webhookEvent.Status = EventStatus.Delivered;
            webhookEvent.DeliveredAt = attempt.AttemptedAt;
            webhookEvent.LastError = null;
        }
        else if (webhookEvent.AttemptCount >= _options.MaxAttempts)
        {
            webhookEvent.Status = EventStatus.Failed;
            webhookEvent.LastError = attempt.Error;
            _logger.LogError("Event {EventId} failed after {Attempts} attempts: {Error}",
                webhookEvent.Id, webhookEvent.AttemptCount, attempt.Error);
        }
        else
        {
            TimeSpan delay = RetryPolicy.GetDelay(
                webhookEvent.AttemptCount, _options.BaseDelaySeconds, _options.MaxDelaySeconds);

            webhookEvent.NextAttemptAt = attempt.AttemptedAt + delay;
            webhookEvent.LastError = attempt.Error;
            _logger.LogWarning("Event {EventId} attempt {Attempt} failed ({Error}). Retrying in {Delay}.",
                webhookEvent.Id, webhookEvent.AttemptCount, attempt.Error, delay);
        }

        // Save after every event, not once at the end of the batch. If the app dies
        // partway through, the events already sent keep their result.
        await _db.SaveChangesAsync(cancellationToken);
    }
}
