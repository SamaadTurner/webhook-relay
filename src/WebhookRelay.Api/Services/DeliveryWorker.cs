using Microsoft.Extensions.Options;
using WebhookRelay.Api.Options;

namespace WebhookRelay.Api.Services;

// Runs for the life of the app and checks for events to send every few seconds.
public class DeliveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RelayOptions _options;
    private readonly ILogger<DeliveryWorker> _logger;

    public DeliveryWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<RelayOptions> options,
        ILogger<DeliveryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Delivery worker started. Checking every {Seconds} seconds.",
            _options.PollIntervalSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.PollIntervalSeconds));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunOnceAsync(stoppingToken);
            }
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
            // The worker is a singleton but the database context is scoped,
            // so each run gets its own scope (and its own context).
            using IServiceScope scope = _scopeFactory.CreateScope();
            DeliveryService delivery = scope.ServiceProvider.GetRequiredService<DeliveryService>();

            await delivery.ProcessDueEventsAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Log and keep going. One bad run should not stop the worker for good.
            _logger.LogError(ex, "Delivery run failed");
        }
    }
}
