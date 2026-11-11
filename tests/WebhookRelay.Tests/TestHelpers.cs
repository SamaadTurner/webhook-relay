using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WebhookRelay.Api.Data;

namespace WebhookRelay.Tests;

// A clock the tests control. Retry delays and the daily budget both depend on
// the time, and tests can't wait 30 seconds (or until midnight) for real.
public class FakeClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        return Now;
    }

    public void Advance(TimeSpan amount)
    {
        Now = Now.Add(amount);
    }
}

// An in-memory SQLite database for one test. In-memory SQLite only lives while
// its connection is open, so this class holds the connection until Dispose.
public sealed class TestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public TestDatabase()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        using RelayDbContext db = CreateContext();
        db.Database.EnsureCreated();
    }

    public RelayDbContext CreateContext()
    {
        DbContextOptions<RelayDbContext> options = new DbContextOptionsBuilder<RelayDbContext>()
            .UseSqlite(_connection)
            .Options;

        return new RelayDbContext(options);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}

// Stands in for the destination API. The test decides what each call returns,
// and can look at the requests afterwards.
public class FakeDestination : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = new();

    // What to answer with. Change it mid-test to simulate the destination recovering.
    public HttpStatusCode StatusToReturn { get; set; } = HttpStatusCode.OK;

    // When set, every call throws this instead of returning a response
    public Exception? ExceptionToThrow { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content == null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken);

        Requests.Add(new RecordedRequest(
            request.RequestUri!.ToString(),
            body,
            request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value))));

        if (ExceptionToThrow != null)
        {
            throw ExceptionToThrow;
        }

        return new HttpResponseMessage(StatusToReturn);
    }
}

public record RecordedRequest(string Url, string Body, Dictionary<string, string> Headers);
