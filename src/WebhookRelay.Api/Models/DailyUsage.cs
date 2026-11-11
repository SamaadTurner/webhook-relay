namespace WebhookRelay.Api.Models;

// How many calls we have made to the destination on one day (UTC).
public class DailyUsage
{
    public DateOnly Day { get; set; }

    public int CallsUsed { get; set; }
}
