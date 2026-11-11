namespace WebhookRelay.Api.Options;

// Settings from the "Relay" section of appsettings.json (or environment variables).
public class RelayOptions
{
    // Where events get forwarded to
    public string DestinationUrl { get; set; } = "";

    // Most calls we may make to the destination per day (UTC)
    public int DailyCallBudget { get; set; } = 1000;

    // After this many failed tries an event is marked Failed
    public int MaxAttempts { get; set; } = 5;

    // Wait before the first retry. Each retry after that waits twice as long.
    public int BaseDelaySeconds { get; set; } = 30;

    // Longest we will ever wait between retries
    public int MaxDelaySeconds { get; set; } = 3600;

    // How often the worker checks for events to send
    public int PollIntervalSeconds { get; set; } = 5;

    // How many events the worker picks up per check
    public int BatchSize { get; set; } = 20;

    // Largest request body we accept, in bytes
    public int MaxPayloadBytes { get; set; } = 262144;

    // Tests turn this off so the worker doesn't run in the background
    public bool WorkerEnabled { get; set; } = true;

    // Required in the X-Api-Key header for the admin endpoints
    public string AdminApiKey { get; set; } = "";

    // One entry per system that is allowed to send us webhooks.
    // The key is the name used in the URL: POST /webhooks/{source}
    public Dictionary<string, SourceOptions> Sources { get; set; } = new();
}

public class SourceOptions
{
    // Shared secret used to check the signature on each webhook
    public string Secret { get; set; } = "";
}
