using Cabinet.Core.Configuration;
using Cabinet.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace Cabinet.API.Services;

/// <summary>
/// Retention for one index. It replaces <c>Storage:RetentionDays</c> for that index.
/// </summary>
public class RetentionPolicyDto
{
    public string Index { get; set; } = string.Empty;
    public int MaxAgeDays { get; set; } = 90;
}

/// <summary>
/// Deletes events past their index's policy, or past <c>Storage:RetentionDays</c> for an index
/// without one. Nothing is deleted unless one of those applies.
/// </summary>
public sealed class RetentionService
{
    /// <summary>Where policies are kept.</summary>
    public const string PoliciesCollection = "retention-policies";

    private readonly ILogger<RetentionService> _logger;
    private readonly ILogStorage _storage;
    private readonly IDocumentStore _documents;
    private readonly int _retentionDays;
    private readonly TamperEvidentAuditLogger? _auditLog;
    private readonly int _auditRetentionDays;

    public RetentionService(
        ILogger<RetentionService> logger,
        ILogStorage storage,
        IDocumentStore documents,
        IOptions<CabinetConfiguration> configuration,
        IConfiguration? settings = null,
        TamperEvidentAuditLogger? auditLog = null)
    {
        _auditLog = auditLog;
        _auditRetentionDays = settings?.GetValue("Audit:RetentionDays", 365) ?? 365;
        _logger = logger;
        _storage = storage;
        _documents = documents;
        _retentionDays = configuration.Value.Storage.RetentionDays;
    }

    public async Task ApplyRetentionPoliciesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_auditLog != null && _auditRetentionDays > 0)
            {
                await _auditLog.PruneAsync(DateTime.UtcNow.AddDays(-_auditRetentionDays), cancellationToken);
            }

            var policies = (await _documents.ListAsync<RetentionPolicyDto>(PoliciesCollection, cancellationToken))
                .ToDictionary(policy => policy.Index, policy => policy.MaxAgeDays, StringComparer.OrdinalIgnoreCase);
            if (policies.Count == 0 && _retentionDays <= 0)
            {
                _logger.LogDebug("No retention configured; keeping all events");
                return;
            }

            var stats = await _storage.GetStatsAsync(cancellationToken);
            var total = 0;
            foreach (var index in stats.EventsByIndex.Keys)
            {
                var days = policies.TryGetValue(index, out var policyDays) ? policyDays : _retentionDays;
                if (days <= 0)
                {
                    continue;
                }

                var before = DateTime.UtcNow.AddDays(-days);
                var deleted = await _storage.PurgeIndexEventsAsync(index, before, cancellationToken);
                total += deleted;
                if (deleted > 0)
                {
                    _logger.LogInformation("Retention deleted {Count} events from index {Index} older than {Date}", deleted, index, before);
                }
            }

            _logger.LogInformation("Retention applied: {Count} events deleted", total);
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
