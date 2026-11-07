using Cabinet.Core.Configuration;
using Cabinet.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace Cabinet.API.Services;

/// <summary>
/// Deletes events older than <c>Storage:RetentionDays</c>.
/// Nothing is deleted unless a positive value is configured.
/// </summary>
public sealed class RetentionService
{
    private readonly ILogger<RetentionService> _logger;
    private readonly ILogStorage _storage;
    private readonly int _retentionDays;

    public RetentionService(
        ILogger<RetentionService> logger,
        ILogStorage storage,
        IOptions<CabinetConfiguration> configuration)
    {
        _logger = logger;
        _storage = storage;
        _retentionDays = configuration.Value.Storage.RetentionDays;
    }

    public async Task ApplyRetentionPoliciesAsync(CancellationToken cancellationToken = default)
    {
        if (_retentionDays <= 0)
        {
            _logger.LogDebug("Retention disabled (Storage:RetentionDays={Days}); keeping all events", _retentionDays);
            return;
        }

        try
        {
            var before = DateTime.UtcNow.AddDays(-_retentionDays);
            var deleted = await _storage.PurgeOldEventsAsync(before, cancellationToken);
            _logger.LogInformation("Retention policy applied: deleted {Count} events older than {Date}", deleted, before);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply retention policies");
        }
    }
}
