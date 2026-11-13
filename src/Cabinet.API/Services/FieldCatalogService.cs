using System.Collections.Concurrent;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query.Natural;

namespace Cabinet.API.Services;

/// <summary>
/// What a caller's data looks like, for reading plain-English searches and suggesting fields and
/// values: field names and common values from recent events. It reads through the caller's index
/// scope, so it never suggests a host or a user from an index they can't read, and it's kept for
/// a couple of minutes per scope.
/// </summary>
public sealed class FieldCatalogService
{
    /// <summary>Recent events the catalog is built from.</summary>
    public const int SampleSize = 2000;

    /// <summary>Most distinct values a field may have for its values to be suggested.</summary>
    public const int MaxValuesPerField = 25;

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private static readonly HashSet<string> AlwaysListed = new(StringComparer.OrdinalIgnoreCase) { "host", "sourcetype", "index" };

    private readonly ConcurrentDictionary<string, (DateTime Built, FieldCatalog Catalog)> _cache = new(StringComparer.Ordinal);
    private readonly ILogger<FieldCatalogService> _logger;

    public FieldCatalogService(ILogger<FieldCatalogService> logger)
    {
        _logger = logger;
    }

    /// <summary>The catalog for what <paramref name="storage"/> lets its caller read.</summary>
    /// <param name="scopeKey">Identifies the caller's access, so callers who read different indexes don't share a catalog.</param>
    public async Task<FieldCatalog> GetAsync(ILogStorage storage, string scopeKey, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(scopeKey, out var cached) && DateTime.UtcNow - cached.Built < Lifetime)
        {
            return cached.Catalog;
        }

        FieldCatalog catalog;
        try
        {
            catalog = await BuildAsync(storage, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reading questions still works on the built-in fields alone
            _logger.LogWarning(ex, "Couldn't sample events for the field catalog; using built-in fields only");
            return FieldCatalog.Empty;
        }

        _cache[scopeKey] = (DateTime.UtcNow, catalog);
        return catalog;
    }

    private static async Task<FieldCatalog> BuildAsync(ILogStorage storage, CancellationToken cancellationToken)
    {
        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var tooMany = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Note(string field, object? value)
        {
            if (value is not (string or long or int or bool) || tooMany.Contains(field))
            {
                return;
            }

            var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(text) || text.Length > 64)
            {
                return;
            }

            if (!values.TryGetValue(field, out var known))
            {
                known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                values[field] = known;
            }

            known.Add(text);
            if (known.Count > (AlwaysListed.Contains(field) ? 500 : MaxValuesPerField))
            {
                tooMany.Add(field);
                values.Remove(field);
            }
        }

        await foreach (var logEvent in storage.QueryAsync(new QueryContext { Limit = SampleSize }, cancellationToken))
        {
            Note("host", logEvent.Host);
            Note("sourcetype", logEvent.SourceType);
            Note("index", logEvent.Index);
            foreach (var (name, value) in logEvent.Fields)
            {
                fields.Add(name);
                Note(name, value);
            }
        }

        var stats = await storage.GetStatsAsync(cancellationToken);
        foreach (var index in stats.EventsByIndex.Keys)
        {
            Note("index", index);
        }

        return new FieldCatalog(fields, values.ToDictionary(pair => pair.Key, pair => (IReadOnlyCollection<string>)pair.Value, StringComparer.OrdinalIgnoreCase));
    }
}
