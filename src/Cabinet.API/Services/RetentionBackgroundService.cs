using Cabinet.Core.Interfaces;

namespace Cabinet.API.Services;

/// <summary>
/// Background service that periodically applies log retention policies.
/// Runs every hour and delegates to RetentionService.
/// </summary>
public class RetentionBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RetentionBackgroundService> _logger;

    public RetentionBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<RetentionBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var retentionService = scope.ServiceProvider.GetService<RetentionService>();

                // One instance is enough. It's checked hourly, so a new leader starts within the hour.
                if (retentionService != null && _serviceProvider.GetService<ILeaderLease>()?.IsLeader != false)
                {
                    await retentionService.ApplyRetentionPoliciesAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown — exit the loop without logging an error
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying retention policies");
            }

            try
            {
                // Run every hour
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Retention background service stopped");
    }
}
