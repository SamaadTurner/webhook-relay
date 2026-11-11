namespace WebhookRelay.Api.Models;

public enum EventStatus
{
    // Waiting to be sent (or waiting for the next retry)
    Pending = 0,

    // The destination answered with a 2xx status
    Delivered = 1,

    // We gave up after the maximum number of attempts
    Failed = 2
}
