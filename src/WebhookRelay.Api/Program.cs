using Microsoft.EntityFrameworkCore;
using WebhookRelay.Api.Data;
using WebhookRelay.Api.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.Configure<RelayOptions>(builder.Configuration.GetSection("Relay"));

// Anything that needs "now" asks for TimeProvider, so tests can use a fake clock
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddDbContext<RelayDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Relay")));

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
}

app.MapControllers();

app.Run();

// Lets the test project start the app with WebApplicationFactory
public partial class Program
{
}
