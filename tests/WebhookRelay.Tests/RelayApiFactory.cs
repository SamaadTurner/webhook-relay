using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace WebhookRelay.Tests;

// Starts the real API in memory for integration tests.
// Each factory gets its own SQLite file so test classes don't share data.
public class RelayApiFactory : WebApplicationFactory<Program>
{
    public const string Source = "github";
    public const string Secret = "test-secret";
    public const string AdminApiKey = "test-admin-key";

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"webhook-relay-tests-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Relay", $"Data Source={_databasePath}");
        builder.UseSetting($"Relay:Sources:{Source}:Secret", Secret);
        builder.UseSetting("Relay:AdminApiKey", AdminApiKey);
        builder.UseSetting("Relay:DestinationUrl", "http://destination.test/hook");

        // The tests call the delivery and cleanup code directly, so the background workers stay off
        builder.UseSetting("Relay:WorkerEnabled", "false");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        // SQLite keeps the file open through its connection pool until we clear it
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
