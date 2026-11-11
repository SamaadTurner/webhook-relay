namespace WebhookRelay.Api.Models;

// A record of one try at sending an event to the destination.
public class DeliveryAttempt
{
    public int Id { get; set; }

    public int WebhookEventId { get; set; }

    public DateTime AttemptedAt { get; set; }

    // Null when we never got a response (timeout, connection refused, ...)
    public int? StatusCode { get; set; }

    public bool Succeeded { get; set; }

    public string? Error { get; set; }
}
