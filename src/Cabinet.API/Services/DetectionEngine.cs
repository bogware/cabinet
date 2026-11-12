using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Cabinet.Alerts;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Core.Utilities;
using Cabinet.Query;
using Cabinet.Query.Bql;

namespace Cabinet.API.Services;

/// <summary>
/// Saves an alert to history, where people can see and acknowledge it, and sends it through the
/// configured channels. Every alert goes through here.
/// </summary>
public sealed class AlertDispatcher
{
    private readonly IAlertHistoryStorage _history;
    private readonly IAlertService _alerts;
    private readonly ILogger<AlertDispatcher> _logger;

    public AlertDispatcher(IAlertHistoryStorage history, IAlertService alerts, ILogger<AlertDispatcher> logger)
    {
        _history = history;
        _alerts = alerts;
        _logger = logger;
    }

    /// <summary>Records and sends an alert. Failing to deliver it doesn't lose the record.</summary>
    public async Task DispatchAsync(Alert alert, AlertRecord record, CancellationToken cancellationToken = default)
    {
        record.Severity = alert.Severity.ToString();

        // History keeps no log content: it isn't encrypted or masked as events are, and its
        // readers may not be allowed that index. The event ID leads to the line.
        record.Message ??= alert.Message;
        record.FiredAt = alert.Timestamp;
        await _history.SaveAlertAsync(record);

        try
        {
            await _alerts.SendAlertAsync(alert, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to deliver alert {Title}, though it's in the alert history", alert.Title);
        }
    }
}

/// <summary>Reads and writes detection rules: the built-in ones, and those people have added.</summary>
public sealed class DetectionRuleStore
{
    private const string Rules = "detection-rules";

    /// <summary>Holds which built-in rules are turned on.</summary>
    private const string BuiltinStates = "detection-builtin-states";

    private readonly IDocumentStore _documents;
    private readonly bool _builtinsOnByDefault;

    public DetectionRuleStore(IDocumentStore documents, IConfiguration configuration)
    {
        _documents = documents;
        _builtinsOnByDefault = configuration.GetValue<bool>("Detection:BuiltinRulesEnabled");
    }

    /// <summary>Every rule, built-in first.</summary>
    public async Task<List<DetectionRule>> ListAsync(CancellationToken cancellationToken = default)
    {
        var states = await _documents.ListAsync<BuiltinState>(BuiltinStates, cancellationToken);
        var enabled = states.ToDictionary(state => state.Id, state => state.Enabled);

        var rules = BuiltinDetectionRules.All.Select(rule => new DetectionRule
        {
            Id = rule.Id,
            Name = rule.Name,
            Description = rule.Description,
            Severity = rule.Severity,
            Pattern = rule.Pattern,
            Condition = rule.Condition,
            Extract = rule.Extract,
            GroupBy = rule.GroupBy,
            Threshold = rule.Threshold,
            WindowSeconds = rule.WindowSeconds,
            CooldownSeconds = rule.CooldownSeconds,
            Technique = rule.Technique,
            Builtin = true,
            Enabled = enabled.TryGetValue(rule.Id, out var on) ? on : _builtinsOnByDefault,
        }).ToList();

        rules.AddRange(await _documents.ListAsync<DetectionRule>(Rules, cancellationToken));
        return rules;
    }

    /// <summary>Reads a rule.</summary>
    public async Task<DetectionRule?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        (await ListAsync(cancellationToken)).FirstOrDefault(rule => rule.Id == id);

    /// <summary>Saves a rule of your own.</summary>
    public Task SaveAsync(DetectionRule rule, CancellationToken cancellationToken = default) =>
        _documents.PutAsync(Rules, rule.Id, rule, cancellationToken);

    /// <summary>Turns a rule on or off.</summary>
    public async Task<bool> SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default)
    {
        var rule = await GetAsync(id, cancellationToken);
        if (rule == null)
        {
            return false;
        }

        if (rule.Builtin)
        {
            await _documents.PutAsync(BuiltinStates, id, new BuiltinState { Id = id, Enabled = enabled }, cancellationToken);
        }
        else
        {
            rule.Enabled = enabled;
            await SaveAsync(rule, cancellationToken);
        }

        return true;
    }

    /// <summary>Deletes a rule of your own.</summary>
    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
        _documents.DeleteAsync(Rules, id, cancellationToken);

    private sealed class BuiltinState
    {
        public string Id { get; set; } = string.Empty;
        public bool Enabled { get; set; }
    }
}

/// <summary>
/// Checks ingested events against detection rules, off the ingest path. Counts and cooldowns are
/// per instance, so with several instances a threshold counts what each one received.
/// </summary>
public sealed class DetectionEngine : BackgroundService
{
    private const int MaxRecentEventIds = 200_000;
    private readonly int MaxRegexTimeouts;

    private readonly TimeSpan RuleReloadInterval;

    private readonly DetectionRuleStore _rules;
    private readonly DetectionActionExecutor _executor;
    private readonly ILogger<DetectionEngine> _logger;
    private readonly IClusterCoordinator _coordinator;
    private readonly Channel<IReadOnlyList<LogEvent>> _pending = Channel.CreateBounded<IReadOnlyList<LogEvent>>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly HashSet<string> _recentIds = new();
    private readonly Queue<string> _recentOrder = new();
    private List<CompiledRule> _compiled = new();
    private DateTime _rulesLoadedAt = DateTime.MinValue;
    private long _pendingBatches;
    private long _processedBatches;

    public DetectionEngine(DetectionRuleStore rules, DetectionActionExecutor executor, ILogger<DetectionEngine> logger, IClusterCoordinator? coordinator = null, Infrastructure.ServerLimits? limits = null)
    {
        limits ??= new Infrastructure.ServerLimits();
        MaxRegexTimeouts = limits.DetectionMaxRegexTimeouts;
        RuleReloadInterval = TimeSpan.FromSeconds(limits.DetectionRuleReloadSeconds);
        _rules = rules;
        _executor = executor;
        _logger = logger;
        _coordinator = coordinator ?? new InMemoryClusterCoordinator();
    }

    /// <summary>
    /// Queues stored events for checking. Waits if checking has fallen far behind, which slows
    /// ingest rather than letting detections silently miss events.
    /// </summary>
    public async ValueTask EnqueueAsync(IReadOnlyList<LogEvent> events, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _pendingBatches);
        await _pending.Writer.WriteAsync(events, cancellationToken);
    }

    /// <summary>Waits until everything queued so far has been checked. For tests.</summary>
    internal async Task WaitUntilIdleAsync(CancellationToken cancellationToken = default)
    {
        while (Interlocked.Read(ref _processedBatches) < Interlocked.Read(ref _pendingBatches))
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    /// <summary>Makes rule changes take effect now, rather than within seconds.</summary>
    internal void InvalidateRules() => _rulesLoadedAt = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var batch in _pending.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await CheckAsync(batch, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed to check {Count} events against detection rules", batch.Count);
                }
                finally
                {
                    Interlocked.Increment(ref _processedBatches);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("Detection engine stopping");
        }
    }

    internal async Task CheckAsync(IReadOnlyList<LogEvent> events, CancellationToken cancellationToken = default)
    {
        await ReloadRulesIfDueAsync(cancellationToken);
        if (_compiled.Count == 0)
        {
            return;
        }

        foreach (var logEvent in events)
        {
            // A sender retrying a batch mustn't count the same events twice
            if (!RememberEvent(logEvent.Id))
            {
                continue;
            }

            foreach (var rule in _compiled)
            {
                await CheckRuleAsync(rule, logEvent, cancellationToken);
            }
        }

        _compiled.RemoveAll(rule =>
        {
            if (rule.Matcher.Timeouts < MaxRegexTimeouts)
            {
                return false;
            }

            _logger.LogError("Detection rule {Rule} is too slow, so it's off until the rules next reload", rule.Rule.Name);
            return true;
        });
    }

    private bool RememberEvent(string id)
    {
        if (!_recentIds.Add(id))
        {
            return false;
        }

        _recentOrder.Enqueue(id);
        if (_recentOrder.Count > MaxRecentEventIds)
        {
            _recentIds.Remove(_recentOrder.Dequeue());
        }

        return true;
    }

    private async Task CheckRuleAsync(CompiledRule compiled, LogEvent logEvent, CancellationToken cancellationToken)
    {
        if (!compiled.Matcher.TryMatch(logEvent, out var fields))
        {
            return;
        }

        var rule = compiled.Rule;
        var groupValue = string.Empty;
        if (!string.IsNullOrEmpty(rule.GroupBy))
        {
            // Without the field there's nothing to count by, so it isn't this rule's event
            if (!fields.TryGetValue(rule.GroupBy, out var raw) || raw?.ToString() is not { Length: > 0 } text)
            {
                return;
            }

            groupValue = text;
        }

        // Counted where every instance can see it, so a threshold covers events however they
        // were spread across instances, and only one instance fires
        var window = TimeSpan.FromSeconds(rule.WindowSeconds);
        var count = rule.Threshold <= 1
            ? 1
            : await _coordinator.RecordMatchAsync(rule.Id, groupValue, logEvent.Id, logEvent.Timestamp, logEvent.Timestamp - window, cancellationToken);
        if (count < rule.Threshold)
        {
            return;
        }

        if (!await _coordinator.TryFireAsync(rule.Id, groupValue, TimeSpan.FromSeconds(rule.CooldownSeconds), cancellationToken))
        {
            return;
        }

        await FireAsync(rule, groupValue, (int)Math.Min(count, int.MaxValue), logEvent, cancellationToken);
    }

    private Task FireAsync(DetectionRule rule, string groupValue, int count, LogEvent logEvent, CancellationToken cancellationToken)
    {
        var subject = string.IsNullOrEmpty(groupValue) ? string.Empty : $" for {rule.GroupBy}={groupValue}";
        _logger.LogInformation("Detection {Rule} fired{Subject}", rule.Name, subject);
        return _executor.ExecuteAsync(new DetectionFiring(rule, groupValue, count, logEvent), cancellationToken);
    }

    private async Task ReloadRulesIfDueAsync(CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - _rulesLoadedAt < RuleReloadInterval)
        {
            return;
        }

        _rulesLoadedAt = DateTime.UtcNow;
        var compiled = new List<CompiledRule>();
        foreach (var rule in (await _rules.ListAsync(cancellationToken)).Where(rule => rule.Enabled))
        {
            try
            {
                var matcher = new BqlEventMatcher(rule.Pattern, rule.Condition, rule.Extract);

                compiled.Add(new CompiledRule(rule, matcher));
            }
            catch (QueryParseException ex)
            {
                _logger.LogError(ex, "Detection rule {Rule} isn't valid, so it's skipped", rule.Name);
            }
        }

        _compiled = compiled;
    }

    private sealed record CompiledRule(DetectionRule Rule, BqlEventMatcher Matcher);
}

/// <summary>Hands everything stored to the detection engine, whichever route it arrived by.</summary>
public sealed class DetectingLogStorage : ILogStorage, IAggregatingLogStorage
{
    private readonly ILogStorage _inner;
    private readonly DetectionEngine _engine;

    public DetectingLogStorage(ILogStorage inner, DetectionEngine engine)
    {
        _inner = inner;
        _engine = engine;
    }

    /// <inheritdoc />
    public async Task<bool> StoreAsync(LogEvent logEvent, CancellationToken cancellationToken = default)
    {
        var stored = await _inner.StoreAsync(logEvent, cancellationToken);
        await _engine.EnqueueAsync([logEvent], cancellationToken);
        return stored;
    }

    /// <inheritdoc />
    public async Task<int> StoreBatchAsync(IEnumerable<LogEvent> events, CancellationToken cancellationToken = default)
    {
        var batch = events as IReadOnlyList<LogEvent> ?? events.ToList();
        var stored = await _inner.StoreBatchAsync(batch, cancellationToken);
        await _engine.EnqueueAsync(batch, cancellationToken);
        return stored;
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken = default) => _inner.InitializeAsync(cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<LogEvent> QueryAsync(QueryContext context, CancellationToken cancellationToken = default) => _inner.QueryAsync(context, cancellationToken);

    /// <inheritdoc />
    public Task<LogEvent?> GetByIdAsync(string id, CancellationToken cancellationToken = default) => _inner.GetByIdAsync(id, cancellationToken);

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

    /// <inheritdoc />
    public Task<AggregateResult?> AggregateAsync(AggregateRequest request, CancellationToken cancellationToken = default) =>
        _inner is IAggregatingLogStorage aggregating ? aggregating.AggregateAsync(request, cancellationToken) : Task.FromResult<AggregateResult?>(null);
}
