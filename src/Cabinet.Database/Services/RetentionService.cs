using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cabinet.Database.Services;

public class RetentionService
{
    private readonly ILogger<RetentionService> _logger;
    private readonly ILogStorage _storage;

    public RetentionService(ILogger<RetentionService> logger, ILogStorage storage)
    {
        _logger = logger;
        _storage = storage;
    }

    public async Task ApplyRetentionPoliciesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var before = DateTime.UtcNow.AddDays(-30); // Default 30 days retention
            var deleted = await _storage.PurgeOldEventsAsync(before, cancellationToken);
            _logger.LogInformation("Retention policy applied: deleted {Count} events older than {Date}", deleted, before);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply retention policies");
        }
    }
}
