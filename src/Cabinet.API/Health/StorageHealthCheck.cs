using Cabinet.Core.Interfaces;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cabinet.API.Health;

/// <summary>
/// Health check that verifies storage backend connectivity
/// </summary>
public class StorageHealthCheck : IHealthCheck
{
    private readonly ILogStorage _storage;

    public StorageHealthCheck(ILogStorage storage)
    {
        _storage = storage;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Reads one event. Counting them all takes minutes on a large table.
            await foreach (var _ in _storage.QueryAsync(new QueryContext { Limit = 1 }, cancellationToken))
            {
                break;
            }

            return HealthCheckResult.Healthy("Storage accessible.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Storage unavailable", ex);
        }
    }
}
