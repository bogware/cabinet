using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;

namespace Cabinet.API.Services;

/// <summary>
/// Applies saved field extractions to events as they're read, so a pattern added today works on
/// events stored yesterday. Each extraction's named groups become fields, unless the event already
/// has a field by that name.
/// </summary>
public sealed class FieldExtractor
{
    private static readonly TimeSpan ReloadInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private readonly IFieldExtractionStorage _storage;
    private readonly ILogger<FieldExtractor> _logger;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private volatile IReadOnlyList<Compiled> _compiled = [];
    private DateTime _loadedAt = DateTime.MinValue;

    public FieldExtractor(IFieldExtractionStorage storage, ILogger<FieldExtractor> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    /// <summary>Makes changes take effect on the next read, rather than within seconds.</summary>
    public void Invalidate() => _loadedAt = DateTime.MinValue;

    /// <summary>Names of every field an enabled extraction can add.</summary>
    public async Task<IReadOnlySet<string>> FieldNamesAsync(CancellationToken cancellationToken = default) =>
        (await GetAsync(cancellationToken)).SelectMany(extraction => extraction.Names).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The extractions in force, reloaded when they may have changed.</summary>
    public async Task<IReadOnlyList<Compiled>> GetAsync(CancellationToken cancellationToken = default)
    {
        if (DateTime.UtcNow - _loadedAt < ReloadInterval)
        {
            return _compiled;
        }

        await _reloadLock.WaitAsync(cancellationToken);
        try
        {
            if (DateTime.UtcNow - _loadedAt < ReloadInterval)
            {
                return _compiled;
            }

            var compiled = new List<Compiled>();
            foreach (var extraction in (await _storage.GetAllAsync()).Where(e => e.Enabled).OrderByDescending(e => e.Priority))
            {
                try
                {
                    var regex = new Regex(extraction.Pattern, RegexOptions.Compiled, RegexTimeout);
                    var names = regex.GetGroupNames().Where(name => !int.TryParse(name, out _)).ToArray();
                    if (names.Length > 0)
                    {
                        compiled.Add(new Compiled(extraction, regex, names));
                    }
                }
                catch (ArgumentException ex)
                {
                    _logger.LogWarning(ex, "Field extraction {Name} has an invalid pattern, so it's skipped", extraction.Name);
                }
            }

            _compiled = compiled;
            _loadedAt = DateTime.UtcNow;
            return compiled;
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    /// <summary>The event with extracted fields added, or the same event if none apply.</summary>
    public static LogEvent Apply(LogEvent logEvent, IReadOnlyList<Compiled> extractions)
    {
        LogEvent? copy = null;
        foreach (var extraction in extractions)
        {
            if ((extraction.Extraction.SourceType != null && !extraction.Extraction.SourceType.Equals(logEvent.SourceType, StringComparison.OrdinalIgnoreCase))
                || (extraction.Extraction.Index != null && !extraction.Extraction.Index.Equals(logEvent.Index, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Match match;
            try
            {
                match = extraction.Regex.Match(logEvent.RawLine);
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }

            if (!match.Success)
            {
                continue;
            }

            foreach (var name in extraction.Names)
            {
                var group = match.Groups[name];
                if (!group.Success || (copy ?? logEvent).Fields.ContainsKey(name))
                {
                    continue;
                }

                copy ??= Copy(logEvent);
                copy.Fields[name] = group.Value;
            }
        }

        return copy ?? logEvent;
    }

    private static LogEvent Copy(LogEvent logEvent)
    {
        var copy = new LogEvent
        {
            Id = logEvent.Id,
            Timestamp = logEvent.Timestamp,
            IndexTime = logEvent.IndexTime,
            SourceType = logEvent.SourceType,
            Host = logEvent.Host,
            Severity = logEvent.Severity,
            RawLine = logEvent.RawLine,
            SourceFile = logEvent.SourceFile,
            LineNumber = logEvent.LineNumber,
            ParserName = logEvent.ParserName,
            Index = logEvent.Index,
            Tags = logEvent.Tags,
        };
        foreach (var field in logEvent.Fields)
        {
            copy.Fields[field.Key] = field.Value;
        }

        return copy;
    }

    /// <summary>An extraction ready to run.</summary>
    public sealed record Compiled(FieldExtraction Extraction, Regex Regex, string[] Names);
}

/// <summary>Storage whose reads include extracted fields.</summary>
public sealed class ExtractingLogStorage : ILogStorage, IAggregatingLogStorage
{
    private readonly ILogStorage _inner;
    private readonly FieldExtractor _extractor;

    public ExtractingLogStorage(ILogStorage inner, FieldExtractor extractor)
    {
        _inner = inner;
        _extractor = extractor;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<LogEvent> QueryAsync(QueryContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var extractions = await _extractor.GetAsync(cancellationToken);
        await foreach (var logEvent in _inner.QueryAsync(context, cancellationToken))
        {
            yield return extractions.Count == 0 ? logEvent : FieldExtractor.Apply(logEvent, extractions);
        }
    }

    /// <inheritdoc />
    public async Task<LogEvent?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var logEvent = await _inner.GetByIdAsync(id, cancellationToken);
        return logEvent == null ? null : FieldExtractor.Apply(logEvent, await _extractor.GetAsync(cancellationToken));
    }

    /// <inheritdoc />
    public async Task<AggregateResult?> AggregateAsync(AggregateRequest request, CancellationToken cancellationToken = default)
    {
        // Storage doesn't have extracted fields, so aggregates over them run in the engine
        var extracted = await _extractor.FieldNamesAsync(cancellationToken);
        var fields = request.GroupBy.Concat(request.Aggregates.Select(spec => spec.Field).OfType<string>());
        return _inner is IAggregatingLogStorage aggregating && !fields.Any(extracted.Contains)
            ? await aggregating.AggregateAsync(request, cancellationToken)
            : null;
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken = default) => _inner.InitializeAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> StoreAsync(LogEvent logEvent, CancellationToken cancellationToken = default) => _inner.StoreAsync(logEvent, cancellationToken);

    /// <inheritdoc />
    public Task<int> StoreBatchAsync(IEnumerable<LogEvent> events, CancellationToken cancellationToken = default) => _inner.StoreBatchAsync(events, cancellationToken);

    /// <inheritdoc />
    public Task<long> GetCountAsync(QueryContext? context = null, CancellationToken cancellationToken = default) => _inner.GetCountAsync(context, cancellationToken);

    /// <inheritdoc />
    public Task<int> PurgeOldEventsAsync(DateTime before, CancellationToken cancellationToken = default) => _inner.PurgeOldEventsAsync(before, cancellationToken);

    /// <inheritdoc />
    public Task<int> PurgeIndexEventsAsync(string indexName, DateTime before, CancellationToken cancellationToken = default) => _inner.PurgeIndexEventsAsync(indexName, before, cancellationToken);

    /// <inheritdoc />
    public Task<int> DeleteByIndexAsync(string indexName, CancellationToken cancellationToken = default) => _inner.DeleteByIndexAsync(indexName, cancellationToken);

    /// <inheritdoc />
    public Task<StorageStats> GetStatsAsync(CancellationToken cancellationToken = default) => _inner.GetStatsAsync(cancellationToken);
}
