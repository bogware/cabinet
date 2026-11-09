using Cabinet.Core.Interfaces;

namespace Cabinet.API.Services;

/// <summary>
/// Tells the cluster this instance is running, so that limits shared across instances can be
/// divided between those that are.
/// </summary>
public sealed class ClusterHeartbeatService : BackgroundService
{
    private readonly TimeSpan Interval;

    private readonly IClusterCoordinator _coordinator;
    private readonly ILogger<ClusterHeartbeatService> _logger;

    public ClusterHeartbeatService(IClusterCoordinator coordinator, ILogger<ClusterHeartbeatService> logger, Infrastructure.ServerLimits? limits = null)
    {
        Interval = TimeSpan.FromSeconds(Math.Max(1, (limits ?? new Infrastructure.ServerLimits()).ClusterHeartbeatSeconds));
        _coordinator = coordinator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var last = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var live = await _coordinator.HeartbeatAsync(stoppingToken);
                if (live != last)
                {
                    _logger.LogInformation("{Count} API instance(s) running", live);
                    last = live;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Cluster heartbeat failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
