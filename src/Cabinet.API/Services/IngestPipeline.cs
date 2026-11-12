using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query.Bql;

namespace Cabinet.API.Services;

/// <summary>What an ingest rule does to the events it matches.</summary>
public enum PipelineAction
{
    /// <summary>Doesn't store them.</summary>
    Drop,

    /// <summary>Stores one in every N, the same ones however often they're sent.</summary>
    Sample,

    /// <summary>Stores them in another index.</summary>
    Route,

    /// <summary>Replaces what a regular expression matches, in the line and in every field.</summary>
    Redact,
}

/// <summary>One step of the ingest pipeline. Rules run in order; Drop and a sampled-out event stop it.</summary>
public sealed class PipelineRule
{
    public string Id { get; set; } = Cabinet.Core.Utilities.Ulid.NewUlid();
    public required string Name { get; set; }
    public bool Enabled { get; set; } = true;
    public int Order { get; set; }

    /// <summary>A regular expression the line has to match. Empty matches every line.</summary>
    public string? Pattern { get; set; }

    /// <summary>A BQL where condition the event has to satisfy, such as <c>severity &lt;= 1 AND host = "web-1"</c>.</summary>
    public string? Condition { get; set; }

    public PipelineAction Action { get; set; }

    /// <summary>For Sample: keep one in this many.</summary>
    public int SampleRate { get; set; } = 10;

    /// <summary>For Route: the index.</summary>
    public string? TargetIndex { get; set; }

    /// <summary>For Redact: what to replace.</summary>
    public string? RedactPattern { get; set; }

    /// <summary>For Redact: what to put instead.</summary>
    public string Replacement { get; set; } = "[REDACTED]";

    /// <summary>Whether no later rule runs on what this one matched.</summary>
    public bool Stop { get; set; }
}

/// <summary>What one rule has done since this instance started.</summary>
public sealed class PipelineRuleStats
{
    public long Matched;
    public long Dropped;
    public long BytesDropped;
    public long Routed;
    public long Redacted;
}

/// <summary>
/// Runs the ingest pipeline on events before they're stored. Rules live in the shared document
/// store, and each instance reloads them every 30 seconds.
/// </summary>
public sealed class IngestPipeline
{
    public const string Collection = "ingest-pipeline";
    private static readonly TimeSpan ReloadEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private readonly IDocumentStore _documents;
    private readonly ILogger<IngestPipeline> _logger;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private volatile Compiled[] _rules = [];
    private DateTime _loadedAt = DateTime.MinValue;

    public IngestPipeline(IDocumentStore documents, ILogger<IngestPipeline> logger)
    {
        _documents = documents;
        _logger = logger;
    }

    /// <summary>Per rule, what it did since this instance started.</summary>
    public ConcurrentDictionary<string, PipelineRuleStats> Stats { get; } = new(StringComparer.Ordinal);

    private sealed record Compiled(PipelineRule Rule, BqlEventMatcher? Matcher, Regex? Redact);

    /// <summary>Checks a rule compiles, returning why not.</summary>
    public static string? Validate(PipelineRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Length > 200) return "Give the rule a name of up to 200 characters.";
        if (rule.Action == PipelineAction.Sample && rule.SampleRate is < 2 or > 1_000_000) return "Sample keeps one in 2 to 1,000,000.";
        if (rule.Action == PipelineAction.Route && (string.IsNullOrWhiteSpace(rule.TargetIndex) || !Regex.IsMatch(rule.TargetIndex, "^[A-Za-z0-9._-]{1,64}$"))) return "Route needs an index name of letters, digits, '.', '_' or '-'.";
        if (rule.Action == PipelineAction.Redact && string.IsNullOrEmpty(rule.RedactPattern)) return "Redact needs a pattern of what to replace.";
        try
        {
            Compile(rule);
            return null;
        }
        catch (Exception ex) when (ex is Cabinet.Query.QueryParseException or ArgumentException)
        {
            return $"The rule doesn't compile: {ex.Message}";
        }
    }

    private static Compiled Compile(PipelineRule rule) => new(
        rule,
        string.IsNullOrWhiteSpace(rule.Pattern) && string.IsNullOrWhiteSpace(rule.Condition) ? null : new BqlEventMatcher(rule.Pattern, rule.Condition, null),
        rule.Action == PipelineAction.Redact && rule.RedactPattern != null ? new Regex(rule.RedactPattern, RegexOptions.CultureInvariant, RegexTimeout) : null);

    /// <summary>Reloads the rules now, as after a change.</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        var compiled = new List<Compiled>();
        foreach (var rule in (await _documents.ListAsync<PipelineRule>(Collection, cancellationToken)).Where(r => r.Enabled).OrderBy(r => r.Order).ThenBy(r => r.Name, StringComparer.Ordinal))
        {
            try
            {
                compiled.Add(Compile(rule));
            }
            catch (Exception ex) when (ex is Cabinet.Query.QueryParseException or ArgumentException)
            {
                _logger.LogError("Ingest rule {Name} doesn't compile and is skipped: {Message}", rule.Name, ex.Message);
            }
        }

        _rules = compiled.ToArray();
        _loadedAt = DateTime.UtcNow;
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - _loadedAt < ReloadEvery) return;
        if (!await _reloadLock.WaitAsync(0, cancellationToken)) return;
        try
        {
            await ReloadAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep running the rules already loaded rather than stop ingest
            _logger.LogWarning(ex, "Couldn't reload the ingest pipeline");
            _loadedAt = DateTime.UtcNow;
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    /// <summary>The events to store, changed as the rules say, without the dropped ones.</summary>
    public async Task<List<LogEvent>> ApplyAsync(IEnumerable<LogEvent> events, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        var rules = _rules;
        var kept = new List<LogEvent>();
        foreach (var logEvent in events)
        {
            if (rules.Length == 0 || Apply(rules, logEvent))
            {
                kept.Add(logEvent);
            }
        }

        return kept;
    }

    /// <summary>What the rules would do to one event, for testing them: null when it's dropped.</summary>
    public static LogEvent? Preview(IEnumerable<PipelineRule> rules, LogEvent logEvent) =>
        Apply(rules.Where(r => r.Enabled).OrderBy(r => r.Order).Select(Compile).ToArray(), logEvent, stats: null) ? logEvent : null;

    private bool Apply(Compiled[] rules, LogEvent logEvent) => Apply(rules, logEvent, Stats);

    private static bool Apply(Compiled[] rules, LogEvent logEvent, ConcurrentDictionary<string, PipelineRuleStats>? stats)
    {
        foreach (var (rule, matcher, redact) in rules)
        {
            if (matcher != null && !matcher.TryMatch(logEvent, out _))
            {
                continue;
            }

            var counts = stats?.GetOrAdd(rule.Id, _ => new PipelineRuleStats());
            if (counts != null) Interlocked.Increment(ref counts.Matched);

            switch (rule.Action)
            {
                case PipelineAction.Drop:
                    Dropped(counts, logEvent);
                    return false;

                case PipelineAction.Sample when !Keeps(logEvent, rule.SampleRate):
                    Dropped(counts, logEvent);
                    return false;

                case PipelineAction.Route:
                    logEvent.Index = rule.TargetIndex!;
                    if (counts != null) Interlocked.Increment(ref counts.Routed);
                    break;

                case PipelineAction.Redact when redact != null:
                    if (RedactEvent(logEvent, redact, rule.Replacement) && counts != null) Interlocked.Increment(ref counts.Redacted);
                    break;
            }

            if (rule.Stop)
            {
                break;
            }
        }

        return true;
    }

    private static void Dropped(PipelineRuleStats? counts, LogEvent logEvent)
    {
        if (counts == null) return;
        Interlocked.Increment(ref counts.Dropped);
        Interlocked.Add(ref counts.BytesDropped, Encoding.UTF8.GetByteCount(logEvent.RawLine ?? string.Empty));
    }

    /// <summary>
    /// Whether sampling keeps an event: decided by its ID, so a resent event gets the same answer
    /// and retries don't turn sampling into duplicates.
    /// </summary>
    public static bool Keeps(LogEvent logEvent, int rate)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(logEvent.Id), hash);
        return BitConverter.ToUInt32(hash) % (uint)Math.Max(1, rate) == 0;
    }

    private static bool RedactEvent(LogEvent logEvent, Regex pattern, string replacement)
    {
        var changed = false;
        string Replace(string text)
        {
            try
            {
                var replaced = pattern.Replace(text, replacement);
                changed |= !ReferenceEquals(replaced, text) && replaced != text;
                return replaced;
            }
            catch (RegexMatchTimeoutException)
            {
                // Can't tell what's in it, so keep none of it
                changed = true;
                return replacement;
            }
        }

        logEvent.RawLine = Replace(logEvent.RawLine ?? string.Empty);
        foreach (var field in logEvent.Fields.Where(f => f.Value is string).ToList())
        {
            logEvent.Fields[field.Key] = Replace((string)field.Value!);
        }

        return changed;
    }
}

/// <summary>Runs the ingest pipeline on everything stored, so no route to storage skips it.</summary>
public sealed class PipelineLogStorage : ILogStorage, IAggregatingLogStorage
{
    private readonly ILogStorage _inner;
    private readonly IngestPipeline _pipeline;

    public PipelineLogStorage(ILogStorage inner, IngestPipeline pipeline)
    {
        _inner = inner;
        _pipeline = pipeline;
    }

    public async Task<bool> StoreAsync(LogEvent logEvent, CancellationToken cancellationToken = default) =>
        (await _pipeline.ApplyAsync([logEvent], cancellationToken)) is [var kept] && await _inner.StoreAsync(kept, cancellationToken);

    public async Task<int> StoreBatchAsync(IEnumerable<LogEvent> events, CancellationToken cancellationToken = default)
    {
        var kept = await _pipeline.ApplyAsync(events, cancellationToken);
        return kept.Count == 0 ? 0 : await _inner.StoreBatchAsync(kept, cancellationToken);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => _inner.InitializeAsync(cancellationToken);
    public IAsyncEnumerable<LogEvent> QueryAsync(QueryContext context, CancellationToken cancellationToken = default) => _inner.QueryAsync(context, cancellationToken);
    public Task<LogEvent?> GetByIdAsync(string id, CancellationToken cancellationToken = default) => _inner.GetByIdAsync(id, cancellationToken);
    public Task<long> GetCountAsync(QueryContext? context = null, CancellationToken cancellationToken = default) => _inner.GetCountAsync(context, cancellationToken);
    public Task<int> PurgeOldEventsAsync(DateTime before, CancellationToken cancellationToken = default) => _inner.PurgeOldEventsAsync(before, cancellationToken);
    public Task<int> PurgeIndexEventsAsync(string indexName, DateTime before, CancellationToken cancellationToken = default) => _inner.PurgeIndexEventsAsync(indexName, before, cancellationToken);
    public Task<int> DeleteByIndexAsync(string indexName, CancellationToken cancellationToken = default) => _inner.DeleteByIndexAsync(indexName, cancellationToken);
    public Task<StorageStats> GetStatsAsync(CancellationToken cancellationToken = default) => _inner.GetStatsAsync(cancellationToken);

    public Task<AggregateResult?> AggregateAsync(AggregateRequest request, CancellationToken cancellationToken = default) =>
        _inner is IAggregatingLogStorage aggregating ? aggregating.AggregateAsync(request, cancellationToken) : Task.FromResult<AggregateResult?>(null);
}
