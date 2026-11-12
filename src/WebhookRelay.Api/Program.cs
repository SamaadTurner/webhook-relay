using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using WebhookRelay.Api.Data;
using WebhookRelay.Api.Options;
using WebhookRelay.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        // Send statuses as "Pending" / "Delivered" / "Failed" instead of 0 / 1 / 2
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.AddOpenApi();

builder.Services.Configure<RelayOptions>(builder.Configuration.GetSection("Relay"));

// Anything that needs "now" asks for TimeProvider, so tests can use a fake clock
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddDbContext<RelayDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Relay")));

builder.Services.AddScoped<DailyBudget>();
builder.Services.AddScoped<CleanupService>();

// DeliveryService gets its own HttpClient. The timeout matters: without one,
// a destination that never answers would block the worker forever.
builder.Services.AddHttpClient<DeliveryService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

bool workerEnabled = builder.Configuration.GetValue("Relay:WorkerEnabled", true);
if (workerEnabled)
{
    builder.Services.AddHostedService<DeliveryWorker>();
    builder.Services.AddHostedService<CleanupWorker>();
}

// /health also checks that the database can be reached
builder.Services.AddHealthChecks().AddDbContextCheck<RelayDbContext>();

var app = builder.Build();

// Create or update the database on startup. Fine for a single small service;
// with more than one instance this should be a separate deploy step.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
    db.Database.Migrate();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    // A stand-in destination so the whole flow can be tried on one machine.
    // appsettings.Development.json points DestinationUrl here. It only exists
    // in Development, never in a real deployment.
    app.MapPost("/dev/destination", async (HttpRequest request, ILogger<Program> logger) =>
    {
        using var reader = new StreamReader(request.Body);
        string body = await reader.ReadToEndAsync();

        logger.LogInformation("Dev destination received delivery {DeliveryId}: {Body}",
            request.Headers[DeliveryService.DeliveryIdHeader].ToString(), body);

        return Results.Ok();
    });
}

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

// Lets the test project start the app with WebApplicationFactory
public partial class Program
{
}
