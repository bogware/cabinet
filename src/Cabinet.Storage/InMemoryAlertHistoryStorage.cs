using System.Collections.Concurrent;
using Cabinet.Core.Interfaces;

namespace Cabinet.Storage;

/// <summary>
/// In-memory implementation of alert history storage.
/// </summary>
public class InMemoryAlertHistoryStorage : IAlertHistoryStorage
{
    private readonly ConcurrentDictionary<string, AlertRecord> _alerts = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task<AlertRecord> SaveAlertAsync(AlertRecord alert)
    {
        _alerts[alert.Id] = alert;
        return Task.FromResult(alert);
    }

    public Task<List<AlertRecord>> GetAlertsAsync(int offset = 0, int count = 100, string? severity = null, bool? acknowledged = null)
    {
        var query = _alerts.Values.ToList().AsEnumerable();
        if (severity != null)
            query = query.Where(a => a.Severity.Equals(severity, StringComparison.OrdinalIgnoreCase));
        if (acknowledged.HasValue)
            query = query.Where(a => a.Acknowledged == acknowledged.Value);
        var results = query.OrderByDescending(a => a.FiredAt).Skip(offset).Take(count).ToList();
        return Task.FromResult(results);
    }

    public Task<AlertRecord?> GetAlertByIdAsync(string id)
    {
        _alerts.TryGetValue(id, out var alert);
        return Task.FromResult(alert);
    }

    public Task<AlertRecord?> AcknowledgeAlertAsync(string id, string acknowledgedBy)
    {
        if (!_alerts.TryGetValue(id, out var alert)) return Task.FromResult<AlertRecord?>(null);
        alert.Acknowledged = true;
        alert.AcknowledgedBy = acknowledgedBy;
        alert.AcknowledgedAt = DateTime.UtcNow;
        return Task.FromResult<AlertRecord?>(alert);
    }

    public Task<int> GetAlertCountAsync(string? severity = null, bool? acknowledged = null)
    {
        var query = _alerts.Values.ToList().AsEnumerable();
        if (severity != null)
            query = query.Where(a => a.Severity.Equals(severity, StringComparison.OrdinalIgnoreCase));
        if (acknowledged.HasValue)
            query = query.Where(a => a.Acknowledged == acknowledged.Value);
        return Task.FromResult(query.Count());
    }
}
