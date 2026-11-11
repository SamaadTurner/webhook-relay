using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebhookRelay.Api.Data;
using WebhookRelay.Api.Models;
using WebhookRelay.Api.Options;
using WebhookRelay.Api.Services;

namespace WebhookRelay.Api.Controllers;

[ApiController]
[Route("webhooks")]
public class WebhooksController : ControllerBase
{
    public const string DeliveryIdHeader = "X-Delivery-Id";
    public const string SignatureHeader = "X-Signature-256";

    private readonly RelayDbContext _db;
    private readonly RelayOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(
        RelayDbContext db,
        IOptions<RelayOptions> options,
        TimeProvider clock,
        ILogger<WebhooksController> logger)
    {
        _db = db;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    // Receives a webhook, saves it, and answers right away. Forwarding happens
    // later in the background worker, so a slow destination never makes the
    // sender wait (most senders time out after a few seconds and retry).
    [HttpPost("{source}")]
    public async Task<IActionResult> Receive(string source, CancellationToken cancellationToken)
    {
        if (!_options.Sources.TryGetValue(source, out SourceOptions? sourceOptions))
        {
            return NotFound(new { error = $"Unknown source '{source}'." });
        }

        string? deliveryId = Request.Headers[DeliveryIdHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(deliveryId) || deliveryId.Length > 200)
        {
            return BadRequest(new { error = $"The {DeliveryIdHeader} header is required (max 200 characters)." });
        }

        string? payload = await ReadBodyAsync(cancellationToken);
        if (payload == null)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                new { error = $"Payload is larger than {_options.MaxPayloadBytes} bytes." });
        }

        // The signature is checked against the raw body, so this has to happen
        // before anything parses or changes the payload.
        string? signature = Request.Headers[SignatureHeader].FirstOrDefault();
        if (!SignatureValidator.IsValid(payload, sourceOptions.Secret, signature))
        {
            _logger.LogWarning("Rejected webhook from {Source}: bad or missing signature", source);
            return Unauthorized(new { error = "Signature is missing or does not match." });
        }

        WebhookEvent? existing = await FindEventAsync(source, deliveryId, cancellationToken);
        if (existing != null)
        {
            // Same delivery sent again. Answer with success so the sender stops retrying.
            return Ok(new { id = existing.Id, duplicate = true });
        }

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        var newEvent = new WebhookEvent
        {
            Source = source,
            DeliveryId = deliveryId,
            Payload = payload,
            ReceivedAt = now,
            Status = EventStatus.Pending,
            NextAttemptAt = now
        };

        _db.Events.Add(newEvent);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two copies of the same delivery arrived at the same time and the other
            // request saved first. The unique index stopped this one, so treat it as
            // a duplicate. If we still can't find the row, it was some other error.
            _db.Entry(newEvent).State = EntityState.Detached;
            existing = await FindEventAsync(source, deliveryId, cancellationToken);
            if (existing == null)
            {
                throw;
            }

            return Ok(new { id = existing.Id, duplicate = true });
        }

        _logger.LogInformation("Saved event {EventId} from {Source} ({DeliveryId})",
            newEvent.Id, source, deliveryId);

        return Accepted(new { id = newEvent.Id, duplicate = false });
    }

    private Task<WebhookEvent?> FindEventAsync(string source, string deliveryId, CancellationToken cancellationToken)
    {
        return _db.Events
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Source == source && e.DeliveryId == deliveryId, cancellationToken);
    }

    // Reads the raw request body as text. Returns null if it is over the size limit.
    private async Task<string?> ReadBodyAsync(CancellationToken cancellationToken)
    {
        if (Request.ContentLength > _options.MaxPayloadBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;

        // Content-Length can be missing, so also count the bytes while reading
        while ((read = await Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > _options.MaxPayloadBytes)
            {
                return null;
            }
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
