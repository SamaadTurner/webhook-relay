using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WebhookRelay.Api.Data;
using WebhookRelay.Api.Models;
using WebhookRelay.Api.Options;
using WebhookRelay.Api.Services;

namespace WebhookRelay.Tests;

public class CleanupServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly FakeClock _clock = new();

    public void Dispose()
    {
        _database.Dispose();
    }

    // Runs the cleanup once with a fresh database context, like the worker does.
    private async Task<int> RunCleanupAsync(int retentionDays = 30)
    {
        using RelayDbContext db = _database.CreateContext();
        var options = Microsoft.Extensions.Options.Options.Create(
            new RelayOptions { RetentionDays = retentionDays });

        var service = new CleanupService(db, options, _clock, NullLogger<CleanupService>.Instance);
        return await service.DeleteOldEventsAsync(CancellationToken.None);
    }

    // Saves an event that was received (and, if delivered, delivered) some time ago.
    private async Task<int> AddEventAsync(string deliveryId, EventStatus status, TimeSpan age)
    {
        DateTime when = _clock.GetUtcNow().UtcDateTime - age;

        var webhookEvent = new WebhookEvent
        {
            Source = "github",
            DeliveryId = deliveryId,
            Payload = "{}",
            ReceivedAt = when,
            NextAttemptAt = when,
            Status = status,
            AttemptCount = 1,
            DeliveredAt = status == EventStatus.Delivered ? when : null
        };

        webhookEvent.Attempts.Add(new DeliveryAttempt
        {
            AttemptedAt = when,
            StatusCode = status == EventStatus.Delivered ? 200 : 500,
            Succeeded = status == EventStatus.Delivered
        });

        using RelayDbContext db = _database.CreateContext();
        db.Events.Add(webhookEvent);
        await db.SaveChangesAsync();
        return webhookEvent.Id;
    }

    private async Task<List<string>> RemainingDeliveryIdsAsync()
    {
        using RelayDbContext db = _database.CreateContext();
        return await db.Events.OrderBy(e => e.DeliveryId).Select(e => e.DeliveryId).ToListAsync();
    }

    [Fact]
    public async Task DeletesDeliveredEventsOlderThanTheRetentionPeriod()
    {
        await AddEventAsync("old", EventStatus.Delivered, TimeSpan.FromDays(31));
        await AddEventAsync("recent", EventStatus.Delivered, TimeSpan.FromDays(2));

        int deleted = await RunCleanupAsync(retentionDays: 30);

        Assert.Equal(1, deleted);
        Assert.Equal(new[] { "recent" }, await RemainingDeliveryIdsAsync());
    }

    [Fact]
    public async Task KeepsAnEventRightUpToTheCutoff()
    {
        // One minute inside the 30 days stays, one minute past it goes
        await AddEventAsync("just-inside", EventStatus.Delivered, TimeSpan.FromDays(30) - TimeSpan.FromMinutes(1));
        await AddEventAsync("just-past", EventStatus.Delivered, TimeSpan.FromDays(30) + TimeSpan.FromMinutes(1));

        int deleted = await RunCleanupAsync(retentionDays: 30);

        Assert.Equal(1, deleted);
        Assert.Equal(new[] { "just-inside" }, await RemainingDeliveryIdsAsync());
    }

    [Fact]
    public async Task KeepsFailedAndPendingEventsNoMatterHowOld()
    {
        await AddEventAsync("failed", EventStatus.Failed, TimeSpan.FromDays(365));
        await AddEventAsync("pending", EventStatus.Pending, TimeSpan.FromDays(365));

        int deleted = await RunCleanupAsync(retentionDays: 30);

        Assert.Equal(0, deleted);
        Assert.Equal(new[] { "failed", "pending" }, await RemainingDeliveryIdsAsync());
    }

    [Fact]
    public async Task DeletesTheAttemptsThatBelongToADeletedEvent()
    {
        int oldId = await AddEventAsync("old", EventStatus.Delivered, TimeSpan.FromDays(40));
        int recentId = await AddEventAsync("recent", EventStatus.Delivered, TimeSpan.FromDays(1));

        await RunCleanupAsync(retentionDays: 30);

        using RelayDbContext db = _database.CreateContext();
        Assert.Equal(0, await db.DeliveryAttempts.CountAsync(a => a.WebhookEventId == oldId));
        Assert.Equal(1, await db.DeliveryAttempts.CountAsync(a => a.WebhookEventId == recentId));
    }

    [Fact]
    public async Task RetentionOfZeroDeletesNothing()
    {
        await AddEventAsync("ancient", EventStatus.Delivered, TimeSpan.FromDays(1000));

        int deleted = await RunCleanupAsync(retentionDays: 0);

        Assert.Equal(0, deleted);
        Assert.Equal(new[] { "ancient" }, await RemainingDeliveryIdsAsync());
    }

    [Fact]
    public async Task AnEventIsDeletedOnceEnoughTimeHasPassed()
    {
        await AddEventAsync("order-1", EventStatus.Delivered, TimeSpan.Zero);

        // Nothing to do on day 29
        _clock.Advance(TimeSpan.FromDays(29));
        Assert.Equal(0, await RunCleanupAsync(retentionDays: 30));

        // Two days later it is past the 30 days
        _clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, await RunCleanupAsync(retentionDays: 30));
        Assert.Empty(await RemainingDeliveryIdsAsync());
    }

    [Fact]
    public async Task RunningItTwiceDeletesNothingTheSecondTime()
    {
        await AddEventAsync("old", EventStatus.Delivered, TimeSpan.FromDays(60));

        Assert.Equal(1, await RunCleanupAsync());
        Assert.Equal(0, await RunCleanupAsync());
    }

    [Fact]
    public async Task ADeliveryIdCanBeSavedAgainAfterItsEventWasCleanedUp()
    {
        // This is the cost of cleaning up: once the old row is gone, the relay can no
        // longer tell that a delivery ID was seen before. The README calls this out.
        await AddEventAsync("order-1", EventStatus.Delivered, TimeSpan.FromDays(45));
        await RunCleanupAsync(retentionDays: 30);

        await AddEventAsync("order-1", EventStatus.Pending, TimeSpan.Zero);

        Assert.Equal(new[] { "order-1" }, await RemainingDeliveryIdsAsync());
    }
}
