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
            var count = await _storage.GetCountAsync(null, cancellationToken);
            return HealthCheckResult.Healthy($"Storage accessible. {count} events stored.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Storage unavailable", ex);
        }
    }
}
