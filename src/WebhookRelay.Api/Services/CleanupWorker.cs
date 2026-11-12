using Microsoft.Extensions.Options;
using WebhookRelay.Api.Options;

namespace WebhookRelay.Api.Services;

// Runs the cleanup once at startup and then on a timer (every hour by default).
public class CleanupWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RelayOptions _options;
    private readonly ILogger<CleanupWorker> _logger;

    public CleanupWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<RelayOptions> options,
        ILogger<CleanupWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.RetentionDays <= 0)
        {
            _logger.LogInformation("Cleanup is off (RetentionDays is 0). Delivered events are kept forever.");
            return;
        }

        _logger.LogInformation(
            "Cleanup worker started. Delivered events are deleted after {Days} days, checked every {Minutes} minutes.",
            _options.RetentionDays, _options.CleanupIntervalMinutes);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.CleanupIntervalMinutes));

        try
        {
            // do/while so the first cleanup happens right away and not an hour after startup
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Same reason as in DeliveryWorker: the database context is scoped,
            // so each run needs its own scope.
            using IServiceScope scope = _scopeFactory.CreateScope();
            CleanupService cleanup = scope.ServiceProvider.GetRequiredService<CleanupService>();

            await cleanup.DeleteOldEventsAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Log and try again on the next tick
            _logger.LogError(ex, "Cleanup run failed");
        }
    }
}
