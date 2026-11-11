namespace WebhookRelay.Api.Models;

// One webhook that we received and need to forward.
public class WebhookEvent
{
    public int Id { get; set; }

    // Which system sent it, taken from the URL (for example "github")
    public string Source { get; set; } = "";

    // The sender's own ID for this delivery. Source + DeliveryId is unique,
    // which is how we notice when the same webhook is sent twice.
    public string DeliveryId { get; set; } = "";

    // The request body exactly as it arrived
    public string Payload { get; set; } = "";

    public DateTime ReceivedAt { get; set; }

    public EventStatus Status { get; set; } = EventStatus.Pending;

    public int AttemptCount { get; set; }

    // The worker skips this event until this time has passed
    public DateTime NextAttemptAt { get; set; }

    public DateTime? DeliveredAt { get; set; }

    public string? LastError { get; set; }

    public List<DeliveryAttempt> Attempts { get; set; } = new();
}
