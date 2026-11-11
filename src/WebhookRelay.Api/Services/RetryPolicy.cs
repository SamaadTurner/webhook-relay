namespace WebhookRelay.Api.Services;

// Works out how long to wait before trying a failed event again.
// The wait doubles each time (30s, 60s, 120s, ...) so a destination that is
// down doesn't get hammered, and it is capped so it never grows forever.
public static class RetryPolicy
{
    // failedAttempts is how many times the event has failed so far (1 after the first failure).
    public static TimeSpan GetDelay(int failedAttempts, int baseDelaySeconds, int maxDelaySeconds)
    {
        if (failedAttempts < 1)
        {
            failedAttempts = 1;
        }

        // Stop doubling after 20 steps. By then we are far past any sensible cap,
        // and this keeps the number from overflowing.
        int doublings = Math.Min(failedAttempts - 1, 20);

        double seconds = baseDelaySeconds * Math.Pow(2, doublings);
        if (seconds > maxDelaySeconds)
        {
            seconds = maxDelaySeconds;
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
