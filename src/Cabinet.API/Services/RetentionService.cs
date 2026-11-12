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

    /// <summary>Whether events go to the archive before they're deleted.</summary>
    public bool ArchiveBeforeDelete { get; set; }
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
    private readonly Archive.EventArchiver? _archiver;
    private readonly bool _archiveByDefault;

    public RetentionService(
        ILogger<RetentionService> logger,
        ILogStorage storage,
        IDocumentStore documents,
        IOptions<CabinetConfiguration> configuration,
        IConfiguration? settings = null,
        TamperEvidentAuditLogger? auditLog = null,
        Archive.EventArchiver? archiver = null)
    {
        _archiver = archiver;
        _archiveByDefault = settings?.GetValue("Archive:BeforeRetention", false) ?? false;
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
                .ToDictionary(policy => policy.Index, StringComparer.OrdinalIgnoreCase);
            if (policies.Count == 0 && _retentionDays <= 0)
            {
                _logger.LogDebug("No retention configured; keeping all events");
                return;
            }

            var stats = await _storage.GetStatsAsync(cancellationToken);
            var total = 0;
            foreach (var index in stats.EventsByIndex.Keys)
            {
                var policy = policies.GetValueOrDefault(index);
                var days = policy?.MaxAgeDays ?? _retentionDays;
                if (days <= 0)
                {
                    continue;
                }

                var before = DateTime.UtcNow.AddDays(-days);
                if (policy?.ArchiveBeforeDelete ?? _archiveByDefault)
                {
                    if (_archiver == null)
                    {
                        // Deleting what was meant to be kept can't be undone
                        _logger.LogError("Index {Index} archives before retention, but no archive is set up (Archive:Type); its events weren't deleted", index);
                        continue;
                    }

                    try
                    {
                        await _archiver.ArchiveAsync(_storage, index, before, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Archiving index {Index} failed, so its old events weren't deleted", index);
                        continue;
                    }
                }

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
