using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebhookRelay.Api.Controllers;
using WebhookRelay.Api.Data;
using WebhookRelay.Api.Models;

namespace WebhookRelay.Tests;

// xUnit makes a new instance of this class for every test, so each test gets
// its own app and its own empty database. That keeps counts predictable.
public class AdminEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly RelayApiFactory _factory = new();
    private readonly HttpClient _client;

    public AdminEndpointTests()
    {
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add(RequireApiKeyAttribute.HeaderName, RelayApiFactory.AdminApiKey);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // Puts an event straight into the database in whatever state the test needs
    private async Task<int> AddEventAsync(
        string deliveryId,
        EventStatus status,
        DateTime? receivedAt = null,
        string source = "github")
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        RelayDbContext db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();

        DateTime received = receivedAt ?? DateTime.UtcNow;
        var webhookEvent = new WebhookEvent
        {
            Source = source,
            DeliveryId = deliveryId,
            Payload = "{\"id\":\"" + deliveryId + "\"}",
            ReceivedAt = received,
            NextAttemptAt = received,
            Status = status,
            AttemptCount = status == EventStatus.Pending ? 0 : 3,
            LastError = status == EventStatus.Failed ? "Destination returned 500" : null
        };

        if (status == EventStatus.Failed)
        {
            webhookEvent.Attempts.Add(new DeliveryAttempt
            {
                AttemptedAt = received,
                StatusCode = 500,
                Succeeded = false,
                Error = "Destination returned 500"
            });
        }

        db.Events.Add(webhookEvent);
        await db.SaveChangesAsync();
        return webhookEvent.Id;
    }

    private async Task<WebhookEvent> LoadEventAsync(int id)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        RelayDbContext db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        return await db.Events.AsNoTracking().SingleAsync(e => e.Id == id);
    }

    [Theory]
    [InlineData("/events")]
    [InlineData("/events/1")]
    [InlineData("/stats")]
    public async Task AdminEndpoints_Return401_WithoutApiKey(string url)
    {
        using HttpClient anonymous = _factory.CreateClient();

        HttpResponseMessage response = await anonymous.GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminEndpoints_Return401_WithWrongApiKey()
    {
        using HttpClient wrongKey = _factory.CreateClient();
        wrongKey.DefaultRequestHeaders.Add(RequireApiKeyAttribute.HeaderName, "not-the-key");

        HttpResponseMessage response = await wrongKey.GetAsync("/events");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReplayAndBackfill_Return401_WithoutApiKey()
    {
        using HttpClient anonymous = _factory.CreateClient();
        int id = await AddEventAsync("d-1", EventStatus.Failed);

        HttpResponseMessage replay = await anonymous.PostAsync($"/events/{id}/replay", null);
        HttpResponseMessage backfill = await anonymous.PostAsJsonAsync("/backfill",
            new { from = "2020-01-01T00:00:00Z", to = "2030-01-01T00:00:00Z" });

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, backfill.StatusCode);
        Assert.Equal(EventStatus.Failed, (await LoadEventAsync(id)).Status);
    }

    [Fact]
    public async Task Health_IsOpen_AndReportsHealthy()
    {
        using HttpClient anonymous = _factory.CreateClient();

        HttpResponseMessage response = await anonymous.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ListEvents_ReturnsNewestFirst()
    {
        await AddEventAsync("d-1", EventStatus.Delivered);
        await AddEventAsync("d-2", EventStatus.Pending);

        EventPage? page = await _client.GetFromJsonAsync<EventPage>("/events", Json);

        Assert.Equal(2, page!.Total);
        Assert.Equal("d-2", page.Items[0].DeliveryId);
        Assert.Equal("d-1", page.Items[1].DeliveryId);
    }

    [Fact]
    public async Task ListEvents_CanFilterByStatusAndSource()
    {
        await AddEventAsync("d-1", EventStatus.Delivered);
        await AddEventAsync("d-2", EventStatus.Failed);
        await AddEventAsync("d-3", EventStatus.Failed, source: "stripe");

        EventPage? failed = await _client.GetFromJsonAsync<EventPage>("/events?status=Failed", Json);
        EventPage? failedStripe = await _client.GetFromJsonAsync<EventPage>(
            "/events?status=Failed&source=stripe", Json);

        Assert.Equal(2, failed!.Total);
        Assert.All(failed.Items, item => Assert.Equal(EventStatus.Failed, item.Status));

        EventSummary only = Assert.Single(failedStripe!.Items);
        Assert.Equal("d-3", only.DeliveryId);
    }

    [Fact]
    public async Task ListEvents_PagesResults()
    {
        for (int i = 1; i <= 5; i++)
        {
            await AddEventAsync($"d-{i}", EventStatus.Pending);
        }

        EventPage? secondPage = await _client.GetFromJsonAsync<EventPage>("/events?page=2&pageSize=2", Json);

        Assert.Equal(5, secondPage!.Total);
        Assert.Equal(2, secondPage.Items.Count);
        Assert.Equal("d-3", secondPage.Items[0].DeliveryId);
        Assert.Equal("d-2", secondPage.Items[1].DeliveryId);
    }

    [Fact]
    public async Task ListEvents_CapsThePageSize()
    {
        EventPage? page = await _client.GetFromJsonAsync<EventPage>("/events?pageSize=5000", Json);

        Assert.Equal(100, page!.PageSize);
    }

    [Fact]
    public async Task GetEvent_ReturnsPayloadAndAttempts()
    {
        int id = await AddEventAsync("d-1", EventStatus.Failed);

        EventDetail? detail = await _client.GetFromJsonAsync<EventDetail>($"/events/{id}", Json);

        Assert.Equal("d-1", detail!.DeliveryId);
        Assert.Equal("{\"id\":\"d-1\"}", detail.Payload);
        Assert.Equal(DateTimeKind.Utc, detail.ReceivedAt.Kind);

        AttemptSummary attempt = Assert.Single(detail.Attempts);
        Assert.Equal(500, attempt.StatusCode);
    }

    [Fact]
    public async Task GetEvent_Returns404_ForUnknownId()
    {
        HttpResponseMessage response = await _client.GetAsync("/events/9999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Replay_PutsAFailedEventBackInTheQueue()
    {
        int id = await AddEventAsync("d-1", EventStatus.Failed);

        HttpResponseMessage response = await _client.PostAsync($"/events/{id}/replay", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        WebhookEvent saved = await LoadEventAsync(id);
        Assert.Equal(EventStatus.Pending, saved.Status);
        Assert.Equal(0, saved.AttemptCount);
    }

    [Theory]
    [InlineData(EventStatus.Pending)]
    [InlineData(EventStatus.Delivered)]
    public async Task Replay_Returns409_WhenEventHasNotFailed(EventStatus status)
    {
        int id = await AddEventAsync("d-1", status);

        HttpResponseMessage response = await _client.PostAsync($"/events/{id}/replay", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(status, (await LoadEventAsync(id)).Status);
    }

    [Fact]
    public async Task Replay_Returns404_ForUnknownId()
    {
        HttpResponseMessage response = await _client.PostAsync("/events/9999/replay", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Backfill_RequeuesFinishedEventsInTheRange_AndLeavesTheRest()
    {
        var march = new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc);
        var april = new DateTime(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc);

        int deliveredInRange = await AddEventAsync("d-1", EventStatus.Delivered, march);
        int failedInRange = await AddEventAsync("d-2", EventStatus.Failed, march);
        int deliveredOutside = await AddEventAsync("d-3", EventStatus.Delivered, april);

        HttpResponseMessage response = await _client.PostAsJsonAsync("/backfill",
            new { from = "2026-03-01T00:00:00Z", to = "2026-04-01T00:00:00Z" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        BackfillResponse? body = await response.Content.ReadFromJsonAsync<BackfillResponse>(Json);
        Assert.Equal(2, body!.Queued);

        Assert.Equal(EventStatus.Pending, (await LoadEventAsync(deliveredInRange)).Status);
        Assert.Equal(EventStatus.Pending, (await LoadEventAsync(failedInRange)).Status);
        Assert.Equal(0, (await LoadEventAsync(failedInRange)).AttemptCount);
        Assert.Equal(EventStatus.Delivered, (await LoadEventAsync(deliveredOutside)).Status);
    }

    [Fact]
    public async Task Backfill_EndOfRangeIsNotIncluded()
    {
        var exactlyAtEnd = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        int id = await AddEventAsync("d-1", EventStatus.Delivered, exactlyAtEnd);

        HttpResponseMessage response = await _client.PostAsJsonAsync("/backfill",
            new { from = "2026-03-01T00:00:00Z", to = "2026-04-01T00:00:00Z" });

        BackfillResponse? body = await response.Content.ReadFromJsonAsync<BackfillResponse>(Json);
        Assert.Equal(0, body!.Queued);
        Assert.Equal(EventStatus.Delivered, (await LoadEventAsync(id)).Status);
    }

    [Fact]
    public async Task Backfill_CanBeLimitedToOneSource()
    {
        var march = new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc);
        int github = await AddEventAsync("d-1", EventStatus.Delivered, march, source: "github");
        int stripe = await AddEventAsync("d-2", EventStatus.Delivered, march, source: "stripe");

        await _client.PostAsJsonAsync("/backfill",
            new { from = "2026-03-01T00:00:00Z", to = "2026-04-01T00:00:00Z", source = "stripe" });

        Assert.Equal(EventStatus.Delivered, (await LoadEventAsync(github)).Status);
        Assert.Equal(EventStatus.Pending, (await LoadEventAsync(stripe)).Status);
    }

    [Fact]
    public async Task Backfill_Returns400_WhenFromIsNotBeforeTo()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/backfill",
            new { from = "2026-04-01T00:00:00Z", to = "2026-03-01T00:00:00Z" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Stats_CountsEventsByStatus_AndShowsTheBudget()
    {
        await AddEventAsync("d-1", EventStatus.Pending);
        await AddEventAsync("d-2", EventStatus.Delivered);
        await AddEventAsync("d-3", EventStatus.Delivered);
        await AddEventAsync("d-4", EventStatus.Failed);

        StatsResponse? stats = await _client.GetFromJsonAsync<StatsResponse>("/stats", Json);

        Assert.Equal(1, stats!.Pending);
        Assert.Equal(2, stats.Delivered);
        Assert.Equal(1, stats.Failed);
        Assert.Equal(0, stats.CallsUsedToday);
        Assert.Equal(1000, stats.DailyCallBudget);
    }
}
