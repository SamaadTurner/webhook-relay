using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebhookRelay.Api.Controllers;
using WebhookRelay.Api.Data;
using WebhookRelay.Api.Models;
using WebhookRelay.Api.Services;

namespace WebhookRelay.Tests;

public class WebhookEndpointTests : IClassFixture<RelayApiFactory>
{
    private readonly RelayApiFactory _factory;
    private readonly HttpClient _client;

    public WebhookEndpointTests(RelayApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ValidWebhook_IsSavedAsPending_AndReturns202()
    {
        string payload = "{\"order\":1}";

        HttpResponseMessage response = await _client.SendAsync(
            BuildRequest(RelayApiFactory.Source, "delivery-valid-1", payload));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        WebhookEvent saved = await FindEventAsync("delivery-valid-1");
        Assert.Equal(payload, saved.Payload);
        Assert.Equal(EventStatus.Pending, saved.Status);
        Assert.Equal(0, saved.AttemptCount);
    }

    [Fact]
    public async Task SameDeliverySentTwice_IsSavedOnce_AndSecondCallReturns200()
    {
        string payload = "{\"order\":2}";

        HttpResponseMessage first = await _client.SendAsync(
            BuildRequest(RelayApiFactory.Source, "delivery-dup-1", payload));
        HttpResponseMessage second = await _client.SendAsync(
            BuildRequest(RelayApiFactory.Source, "delivery-dup-1", payload));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        ReceiveResponse? body = await second.Content.ReadFromJsonAsync<ReceiveResponse>();
        Assert.True(body!.Duplicate);

        using IServiceScope scope = _factory.Services.CreateScope();
        RelayDbContext db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        int count = await db.Events.CountAsync(e => e.DeliveryId == "delivery-dup-1");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task WrongSignature_Returns401_AndSavesNothing()
    {
        HttpRequestMessage request = BuildRequest(RelayApiFactory.Source, "delivery-badsig-1", "{\"order\":3}");
        request.Headers.Remove(WebhooksController.SignatureHeader);
        request.Headers.Add(WebhooksController.SignatureHeader,
            SignatureValidator.ComputeHeader("{\"order\":3}", "wrong-secret"));

        HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(await EventExistsAsync("delivery-badsig-1"));
    }

    [Fact]
    public async Task MissingSignature_Returns401()
    {
        HttpRequestMessage request = BuildRequest(RelayApiFactory.Source, "delivery-nosig-1", "{}");
        request.Headers.Remove(WebhooksController.SignatureHeader);

        HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UnknownSource_Returns404()
    {
        HttpResponseMessage response = await _client.SendAsync(
            BuildRequest("not-a-real-source", "delivery-unknown-1", "{}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MissingDeliveryId_Returns400()
    {
        HttpRequestMessage request = BuildRequest(RelayApiFactory.Source, "ignored", "{}");
        request.Headers.Remove(WebhooksController.DeliveryIdHeader);

        HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PayloadOverTheLimit_Returns413()
    {
        // The default limit is 256 KB
        string payload = new string('x', 300_000);

        HttpResponseMessage response = await _client.SendAsync(
            BuildRequest(RelayApiFactory.Source, "delivery-big-1", payload));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.False(await EventExistsAsync("delivery-big-1"));
    }

    // Builds a correctly signed webhook request
    internal static HttpRequestMessage BuildRequest(string source, string deliveryId, string payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/{source}")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add(WebhooksController.DeliveryIdHeader, deliveryId);
        request.Headers.Add(WebhooksController.SignatureHeader,
            SignatureValidator.ComputeHeader(payload, RelayApiFactory.Secret));
        return request;
    }

    private async Task<WebhookEvent> FindEventAsync(string deliveryId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        RelayDbContext db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        return await db.Events.SingleAsync(e => e.DeliveryId == deliveryId);
    }

    private async Task<bool> EventExistsAsync(string deliveryId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        RelayDbContext db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        return await db.Events.AnyAsync(e => e.DeliveryId == deliveryId);
    }

    private record ReceiveResponse(int Id, bool Duplicate);
}
