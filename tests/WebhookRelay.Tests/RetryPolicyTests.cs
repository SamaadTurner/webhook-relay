using WebhookRelay.Api.Services;

namespace WebhookRelay.Tests;

public class RetryPolicyTests
{
    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(5, 480)]
    public void GetDelay_DoublesAfterEachFailure(int failedAttempts, int expectedSeconds)
    {
        TimeSpan delay = RetryPolicy.GetDelay(failedAttempts, baseDelaySeconds: 30, maxDelaySeconds: 3600);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void GetDelay_NeverGoesOverTheMaximum()
    {
        // 30 * 2^9 = 15360 seconds, which is over the one hour cap
        TimeSpan delay = RetryPolicy.GetDelay(10, baseDelaySeconds: 30, maxDelaySeconds: 3600);

        Assert.Equal(TimeSpan.FromHours(1), delay);
    }

    [Fact]
    public void GetDelay_HandlesAVeryLargeAttemptCount()
    {
        TimeSpan delay = RetryPolicy.GetDelay(int.MaxValue, baseDelaySeconds: 30, maxDelaySeconds: 3600);

        Assert.Equal(TimeSpan.FromHours(1), delay);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void GetDelay_TreatsZeroOrNegativeAsFirstFailure(int failedAttempts)
    {
        TimeSpan delay = RetryPolicy.GetDelay(failedAttempts, baseDelaySeconds: 30, maxDelaySeconds: 3600);

        Assert.Equal(TimeSpan.FromSeconds(30), delay);
    }
}
