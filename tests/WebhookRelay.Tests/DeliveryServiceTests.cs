using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WebhookRelay.Api.Data;
using WebhookRelay.Api.Models;
using WebhookRelay.Api.Options;
using WebhookRelay.Api.Services;

namespace WebhookRelay.Tests;

public class DeliveryServiceTests : IDisposable
{
    private const string DestinationUrl = "http://destination.test/hook";

    private readonly TestDatabase _database = new();
    private readonly FakeClock _clock = new();
    private readonly FakeDestination _destination = new();

    private readonly RelayOptions _options = new()
    {
        DestinationUrl = DestinationUrl,
        DailyCallBudget = 100,
        MaxAttempts = 3,
        BaseDelaySeconds = 30,
        MaxDelaySeconds = 3600,
        BatchSize = 10
    };

    public void Dispose()
    {
        _database.Dispose();
    }

    // Runs the delivery code once, the same way the background worker does:
    // with a fresh database context each time.
    private async Task<int> RunDeliveryAsync()
    {
        using RelayDbContext db = _database.CreateContext();
        var wrappedOptions = Microsoft.Extensions.Options.Options.Create(_options);

        var service = new DeliveryService(
            db,
            new HttpClient(_destination, disposeHandler: false),
            new DailyBudget(db, wrappedOptions, _clock),
            wrappedOptions,
            _clock,
            NullLogger<DeliveryService>.Instance);

        return await service.ProcessDueEventsAsync(CancellationToken.None);
    }

    private async Task<int> AddPendingEventAsync(string deliveryId, string payload = "{\"n\":1}")
    {
        using RelayDbContext db = _database.CreateContext();
        DateTime now = _clock.GetUtcNow().UtcDateTime;

        var webhookEvent = new WebhookEvent
        {
            Source = "github",
            DeliveryId = deliveryId,
            Payload = payload,
            ReceivedAt = now,
            NextAttemptAt = now,
            Status = EventStatus.Pending
        };

        db.Events.Add(webhookEvent);
        await db.SaveChangesAsync();
        return webhookEvent.Id;
    }

    private async Task<WebhookEvent> LoadEventAsync(int id)
    {
        using RelayDbContext db = _database.CreateContext();
        return await db.Events.Include(e => e.Attempts).SingleAsync(e => e.Id == id);
    }

    [Fact]
    public async Task PendingEvent_IsSentAndMarkedDelivered()
    {
        int id = await AddPendingEventAsync("d-1", "{\"hello\":\"world\"}");

        int callsMade = await RunDeliveryAsync();

        Assert.Equal(1, callsMade);

        RecordedRequest request = Assert.Single(_destination.Requests);
        Assert.Equal(DestinationUrl, request.Url);
        Assert.Equal("{\"hello\":\"world\"}", request.Body);
        Assert.Equal("github", request.Headers[DeliveryService.SourceHeader]);
        Assert.Equal("d-1", request.Headers[DeliveryService.DeliveryIdHeader]);

        WebhookEvent saved = await LoadEventAsync(id);
        Assert.Equal(EventStatus.Delivered, saved.Status);
        Assert.Equal(1, saved.AttemptCount);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, saved.DeliveredAt);

        DeliveryAttempt attempt = Assert.Single(saved.Attempts);
        Assert.True(attempt.Succeeded);
        Assert.Equal(200, attempt.StatusCode);
    }

    [Fact]
    public async Task DeliveredEvent_IsNotSentAgain()
    {
        await AddPendingEventAsync("d-1");

        await RunDeliveryAsync();
        await RunDeliveryAsync();

        Assert.Single(_destination.Requests);
    }

    [Fact]
    public async Task FailedCall_KeepsEventPending_AndSchedulesARetry()
    {
        _destination.StatusToReturn = HttpStatusCode.InternalServerError;
        int id = await AddPendingEventAsync("d-1");
        DateTime sentAt = _clock.GetUtcNow().UtcDateTime;

        await RunDeliveryAsync();

        WebhookEvent saved = await LoadEventAsync(id);
        Assert.Equal(EventStatus.Pending, saved.Status);
        Assert.Equal(1, saved.AttemptCount);
        Assert.Equal(sentAt.AddSeconds(30), saved.NextAttemptAt);
        Assert.Equal("Destination returned 500", saved.LastError);

        DeliveryAttempt attempt = Assert.Single(saved.Attempts);
        Assert.False(attempt.Succeeded);
        Assert.Equal(500, attempt.StatusCode);
    }

    [Fact]
    public async Task EventWaitingForRetry_IsSkippedUntilItsTimeComes()
    {
        _destination.StatusToReturn = HttpStatusCode.ServiceUnavailable;
        await AddPendingEventAsync("d-1");

        await RunDeliveryAsync();                       // fails, retry in 30s
        _clock.Advance(TimeSpan.FromSeconds(29));
        await RunDeliveryAsync();                       // too early, nothing sent

        Assert.Single(_destination.Requests);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await RunDeliveryAsync();                       // now it is due

        Assert.Equal(2, _destination.Requests.Count);
    }

    [Fact]
    public async Task SecondFailure_WaitsTwiceAsLong()
    {
        _destination.StatusToReturn = HttpStatusCode.BadGateway;
        int id = await AddPendingEventAsync("d-1");

        await RunDeliveryAsync();
        _clock.Advance(TimeSpan.FromSeconds(30));
        DateTime secondTry = _clock.GetUtcNow().UtcDateTime;
        await RunDeliveryAsync();

        WebhookEvent saved = await LoadEventAsync(id);
        Assert.Equal(2, saved.AttemptCount);
        Assert.Equal(secondTry.AddSeconds(60), saved.NextAttemptAt);
    }

    [Fact]
    public async Task EventRecovers_WhenDestinationComesBack()
    {
        _destination.StatusToReturn = HttpStatusCode.ServiceUnavailable;
        int id = await AddPendingEventAsync("d-1");

        await RunDeliveryAsync();

        _destination.StatusToReturn = HttpStatusCode.OK;
        _clock.Advance(TimeSpan.FromSeconds(30));
        await RunDeliveryAsync();

        WebhookEvent saved = await LoadEventAsync(id);
        Assert.Equal(EventStatus.Delivered, saved.Status);
        Assert.Equal(2, saved.AttemptCount);
        Assert.Null(saved.LastError);
        Assert.Equal(2, saved.Attempts.Count);
    }

    [Fact]
    public async Task Event_IsMarkedFailed_AfterMaxAttempts()
    {
        _destination.StatusToReturn = HttpStatusCode.InternalServerError;
        int id = await AddPendingEventAsync("d-1");

        // MaxAttempts is 3 in these tests
        await RunDeliveryAsync();
        _clock.Advance(TimeSpan.FromHours(2));
        await RunDeliveryAsync();
        _clock.Advance(TimeSpan.FromHours(2));
        await RunDeliveryAsync();

        WebhookEvent saved = await LoadEventAsync(id);
        Assert.Equal(EventStatus.Failed, saved.Status);
        Assert.Equal(3, saved.AttemptCount);

        // A failed event is left alone from then on
        _clock.Advance(TimeSpan.FromHours(2));
        await RunDeliveryAsync();
        Assert.Equal(3, _destination.Requests.Count);
    }

    [Fact]
    public async Task ConnectionError_IsRecordedAsAFailedAttempt()
    {
        _destination.ExceptionToThrow = new HttpRequestException("Connection refused");
        int id = await AddPendingEventAsync("d-1");

        await RunDeliveryAsync();

        WebhookEvent saved = await LoadEventAsync(id);
        Assert.Equal(EventStatus.Pending, saved.Status);
        Assert.Equal("Connection refused", saved.LastError);

        DeliveryAttempt attempt = Assert.Single(saved.Attempts);
        Assert.Null(attempt.StatusCode);
        Assert.False(attempt.Succeeded);
    }

    [Fact]
    public async Task Timeout_IsRecordedAsAFailedAttempt()
    {
        // HttpClient reports a timeout as a TaskCanceledException
        _destination.ExceptionToThrow = new TaskCanceledException();
        int id = await AddPendingEventAsync("d-1");

        await RunDeliveryAsync();

        WebhookEvent saved = await LoadEventAsync(id);
        Assert.Equal(EventStatus.Pending, saved.Status);
        Assert.Equal("Request timed out", saved.LastError);
    }

    [Fact]
    public async Task Sending_StopsWhenTheDailyBudgetIsUsedUp()
    {
        _options.DailyCallBudget = 2;
        int first = await AddPendingEventAsync("d-1");
        int second = await AddPendingEventAsync("d-2");
        int third = await AddPendingEventAsync("d-3");

        int callsMade = await RunDeliveryAsync();

        Assert.Equal(2, callsMade);
        Assert.Equal(2, _destination.Requests.Count);
        Assert.Equal(EventStatus.Delivered, (await LoadEventAsync(first)).Status);
        Assert.Equal(EventStatus.Delivered, (await LoadEventAsync(second)).Status);

        // The third event was never tried, so it has not used up an attempt
        WebhookEvent waiting = await LoadEventAsync(third);
        Assert.Equal(EventStatus.Pending, waiting.Status);
        Assert.Equal(0, waiting.AttemptCount);
    }

    [Fact]
    public async Task EventsLeftOver_AreSentTheNextDay()
    {
        _options.DailyCallBudget = 1;
        await AddPendingEventAsync("d-1");
        int second = await AddPendingEventAsync("d-2");

        await RunDeliveryAsync();
        await RunDeliveryAsync();   // budget is spent, nothing more goes out today
        Assert.Single(_destination.Requests);

        _clock.Advance(TimeSpan.FromDays(1));
        await RunDeliveryAsync();

        Assert.Equal(2, _destination.Requests.Count);
        Assert.Equal(EventStatus.Delivered, (await LoadEventAsync(second)).Status);
    }

    [Fact]
    public async Task FailedCalls_CountAgainstTheBudgetToo()
    {
        // The destination counts every call we make, not just the ones that work
        _options.DailyCallBudget = 1;
        _destination.StatusToReturn = HttpStatusCode.InternalServerError;
        await AddPendingEventAsync("d-1");

        await RunDeliveryAsync();
        _clock.Advance(TimeSpan.FromMinutes(5));
        await RunDeliveryAsync();

        Assert.Single(_destination.Requests);
    }

    [Fact]
    public async Task OneRun_SendsNoMoreThanTheBatchSize()
    {
        _options.BatchSize = 2;
        await AddPendingEventAsync("d-1");
        await AddPendingEventAsync("d-2");
        await AddPendingEventAsync("d-3");

        int callsMade = await RunDeliveryAsync();

        Assert.Equal(2, callsMade);
    }

    [Fact]
    public async Task Events_AreSentOldestFirst()
    {
        await AddPendingEventAsync("d-old");
        _clock.Advance(TimeSpan.FromMinutes(1));
        await AddPendingEventAsync("d-new");

        await RunDeliveryAsync();

        Assert.Equal("d-old", _destination.Requests[0].Headers[DeliveryService.DeliveryIdHeader]);
        Assert.Equal("d-new", _destination.Requests[1].Headers[DeliveryService.DeliveryIdHeader]);
    }
}
