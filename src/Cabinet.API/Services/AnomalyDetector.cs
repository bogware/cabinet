using System.Globalization;
using System.Text.RegularExpressions;
using Cabinet.Alerts;
using Cabinet.Core.Interfaces;
using Cabinet.Query;
using Cabinet.Query.Bql;

namespace Cabinet.API.Services;

/// <summary>What an anomaly rule watches for.</summary>
public enum AnomalyKind
{
    /// <summary>A count far above its usual level for this time window.</summary>
    Spike,

    /// <summary>Something that usually logs went quiet.</summary>
    Silence,

    /// <summary>A value of a field never seen in the baseline, such as a new user or country.</summary>
    NewValue,

    /// <summary>A kind of log line never seen in the baseline.</summary>
    NewPattern,
}

/// <summary>
/// A rule that learns what's normal from recent history instead of a fixed threshold. Each window
/// is compared with the windows before it, once, when it ends.
/// </summary>
public sealed class AnomalyRule
{
    /// <summary>The rule's ID.</summary>
    public string Id { get; set; } = Cabinet.Core.Utilities.Ulid.NewUlid();

    /// <summary>A name people recognize.</summary>
    public required string Name { get; set; }

    /// <summary>Whether it runs.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>What it watches for.</summary>
    public AnomalyKind Kind { get; set; }

    /// <summary>The events it looks at, as a search.</summary>
    public string Query { get; set; } = "find *";

    /// <summary>For Spike and Silence: a field to judge each value of separately, such as host.</summary>
    public string? GroupBy { get; set; }

    /// <summary>For NewValue: the field whose new values matter.</summary>
    public string? Field { get; set; }

    /// <summary>Minutes in each window.</summary>
    public int WindowMinutes { get; set; } = 15;

    /// <summary>How many windows before the current one make the baseline.</summary>
    public int BaselineWindows { get; set; } = 96;

    /// <summary>For Spike: how many standard deviations above the mean counts as a spike.</summary>
    public double Threshold { get; set; } = 3;

    /// <summary>For Spike, the fewest events in a window worth an alert; for Silence, the fewest in the baseline.</summary>
    public int MinCount { get; set; } = 10;

    /// <summary>The alert's severity.</summary>
    public string Severity { get; set; } = "Warning";

    /// <summary>The end of the last window checked.</summary>
    public DateTime? LastWindowEnd { get; set; }

    /// <summary>What the last check found, in a sentence.</summary>
    public string? LastResult { get; set; }
}

/// <summary>Something an anomaly rule found.</summary>
/// <param name="Key">What it's about: a group, a value or a pattern.</param>
/// <param name="Message">A sentence saying what's unusual.</param>
public sealed record AnomalyFinding(string Key, string Message);

/// <summary>Anomaly rules, kept in the shared document store.</summary>
public sealed class AnomalyRuleStore
{
    private const string Collection = "anomaly-rules";
    private readonly IDocumentStore _documents;

    public AnomalyRuleStore(IDocumentStore documents)
    {
        _documents = documents;
    }

    public async Task<List<AnomalyRule>> ListAsync(CancellationToken cancellationToken = default) =>
        (await _documents.ListAsync<AnomalyRule>(Collection, cancellationToken)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public Task<AnomalyRule?> GetAsync(string id, CancellationToken cancellationToken = default) => _documents.GetAsync<AnomalyRule>(Collection, id, cancellationToken);

    public Task SaveAsync(AnomalyRule rule, CancellationToken cancellationToken = default) => _documents.PutAsync(Collection, rule.Id, rule, cancellationToken);

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) => _documents.DeleteAsync(Collection, id, cancellationToken);
}

/// <summary>
/// Checks each anomaly rule when its window ends, on the leader only, and raises an alert for
/// each finding. Searches read through an unprivileged scope, so values in alerts are masked as
/// for a Viewer.
/// </summary>
public sealed class AnomalyDetector : BackgroundService
{
    /// <summary>Most findings one check alerts on; past that, one alert says how many more.</summary>
    public const int MaxAlertsPerCheck = 20;

    private static readonly Regex FieldName = new(@"^[A-Za-z_][A-Za-z0-9_.]{0,127}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly IServiceScopeFactory _scopes;
    private readonly ILeaderLease _lease;
    private readonly ILogger<AnomalyDetector> _logger;

    public AnomalyDetector(IServiceScopeFactory scopes, ILogger<AnomalyDetector> logger, ILeaderLease? lease = null)
    {
        _scopes = scopes;
        _logger = logger;
        _lease = lease ?? new SingleInstanceLease();
    }

    /// <summary>Whether a field name can go into the rule's search as written.</summary>
    public static bool IsValidField(string? field) => field == null || FieldName.IsMatch(field);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_lease.IsLeader)
                {
                    await CheckDueRulesAsync(DateTime.UtcNow, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Anomaly check failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>Checks every rule whose window has ended since it was last checked.</summary>
    internal async Task CheckDueRulesAsync(DateTime now, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<AnomalyRuleStore>();
        var engine = scope.ServiceProvider.GetRequiredService<IQueryEngine>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<AlertDispatcher>();

        foreach (var rule in (await store.ListAsync(cancellationToken)).Where(r => r.Enabled))
        {
            var windowEnd = WindowEnd(now, rule.WindowMinutes);
            if (rule.LastWindowEnd is { } last && last >= windowEnd)
            {
                continue;
            }

            try
            {
                var findings = await EvaluateAsync(rule, windowEnd, engine, cancellationToken);
                foreach (var finding in findings.Take(MaxAlertsPerCheck))
                {
                    await DispatchAsync(dispatcher, rule, finding, cancellationToken);
                }

                if (findings.Count > MaxAlertsPerCheck)
                {
                    await DispatchAsync(dispatcher, rule, new AnomalyFinding("(more)", $"{findings.Count - MaxAlertsPerCheck} more findings weren't sent; run the rule's test to see them all."), cancellationToken);
                }

                rule.LastResult = findings.Count == 0 ? "Nothing unusual" : $"{findings.Count} finding{(findings.Count == 1 ? string.Empty : "s")}";
            }
            catch (QueryParseException ex)
            {
                rule.LastResult = $"The rule's search failed: {ex.Message}";
                _logger.LogWarning("Anomaly rule {Name} failed: {Message}", rule.Name, ex.Message);
            }

            rule.LastWindowEnd = windowEnd;
            await store.SaveAsync(rule, cancellationToken);
        }
    }

    /// <summary>The end of the last whole window before a time.</summary>
    public static DateTime WindowEnd(DateTime now, int windowMinutes)
    {
        var ticks = TimeSpan.FromMinutes(Math.Clamp(windowMinutes, 1, 1440)).Ticks;
        return new DateTime(now.Ticks - now.Ticks % ticks, DateTimeKind.Utc);
    }

    /// <summary>What a rule finds in the window ending at <paramref name="windowEnd"/>.</summary>
    public static async Task<List<AnomalyFinding>> EvaluateAsync(AnomalyRule rule, DateTime windowEnd, IQueryEngine engine, CancellationToken cancellationToken)
    {
        if (!IsValidField(rule.GroupBy) || !IsValidField(rule.Field))
        {
            throw new QueryParseException("Field names are letters, digits, '_' and '.'");
        }

        var window = TimeSpan.FromMinutes(Math.Clamp(rule.WindowMinutes, 1, 1440));
        var baselineWindows = Math.Clamp(rule.BaselineWindows, 2, 2000);
        var start = windowEnd - window;
        var baselineStart = start - window * baselineWindows;

        return rule.Kind switch
        {
            AnomalyKind.Spike or AnomalyKind.Silence => await CountsAsync(rule, engine, window, baselineWindows, baselineStart, start, windowEnd, cancellationToken),
            AnomalyKind.NewValue => await NewValuesAsync(rule, engine, baselineStart, start, windowEnd, cancellationToken),
            AnomalyKind.NewPattern => await NewPatternsAsync(rule, engine, baselineStart, start, windowEnd, cancellationToken),
            _ => [],
        };
    }

    private static Task<Cabinet.Query.QueryResult> RunAsync(IQueryEngine engine, string query, DateTime from, DateTime to, int limit, CancellationToken cancellationToken) =>
        engine.ExecuteAsync(new QueryExecutionContext
        {
            Query = query,
            StartTime = from,
            EndTime = to.AddTicks(-1),
            Limit = limit,
            Timeout = TimeSpan.FromMinutes(2),
        }, cancellationToken);

    private static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static double Count(IDictionary<string, object?> row) =>
        row.TryGetValue("count", out var count) && count != null ? Convert.ToDouble(count, CultureInfo.InvariantCulture) : 0;

    private static DateTime? AsTime(object? value) => value switch
    {
        DateTime time => time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : DateTime.SpecifyKind(time, DateTimeKind.Utc),
        DateTimeOffset offset => offset.UtcDateTime,
        string text when DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) => parsed,
        _ => null,
    };

    private static async Task<List<AnomalyFinding>> CountsAsync(
        AnomalyRule rule, IQueryEngine engine, TimeSpan window, int baselineWindows, DateTime baselineStart, DateTime start, DateTime end, CancellationToken cancellationToken)
    {
        var by = string.IsNullOrEmpty(rule.GroupBy) ? string.Empty : $", {rule.GroupBy}";
        var result = await RunAsync(engine, $"{rule.Query} | bin _time span={(int)window.TotalMinutes}m | stats count by _time{by}", baselineStart, end, 100_000, cancellationToken);

        // Each group's count in each window; a window with no row had none
        var counts = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in result.Events.Cast<IDictionary<string, object?>>())
        {
            if (!row.TryGetValue("_time", out var time) || AsTime(time) is not { } at)
            {
                continue;
            }

            var key = string.IsNullOrEmpty(rule.GroupBy) ? "(all)" : Text(row.TryGetValue(rule.GroupBy, out var group) ? group : null);
            var slot = (int)Math.Floor((at - baselineStart) / window);
            if (slot < 0 || slot > baselineWindows)
            {
                continue;
            }

            if (!counts.TryGetValue(key, out var series))
            {
                series = new double[baselineWindows + 1];
                counts[key] = series;
            }

            series[slot] += Count(row);
        }

        var findings = new List<AnomalyFinding>();
        var label = string.IsNullOrEmpty(rule.GroupBy) ? "Events" : $"{rule.GroupBy}={{0}}";
        foreach (var (key, series) in counts)
        {
            var baseline = series.Take(baselineWindows).ToArray();
            var current = series[baselineWindows];
            var mean = baseline.Average();
            var deviation = Math.Sqrt(baseline.Select(v => (v - mean) * (v - mean)).Average());
            var who = string.Format(CultureInfo.InvariantCulture, label, key);

            if (rule.Kind == AnomalyKind.Spike)
            {
                // A flat baseline has no deviation; a count of one more than usual isn't a spike
                var spread = Math.Max(deviation, Math.Max(1, Math.Sqrt(mean)));
                if (current >= rule.MinCount && current > mean + rule.Threshold * spread)
                {
                    findings.Add(new AnomalyFinding(key, $"{who}: {current:0} in the last {window.TotalMinutes:0} minutes, against a usual {mean:0.#} (±{deviation:0.#})"));
                }
            }
            else if (current == 0 && baseline.Sum() >= rule.MinCount)
            {
                findings.Add(new AnomalyFinding(key, $"{who}: nothing in the last {window.TotalMinutes:0} minutes, after {baseline.Sum():0} in the {baselineWindows} windows before"));
            }
        }

        return findings.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<List<AnomalyFinding>> NewValuesAsync(AnomalyRule rule, IQueryEngine engine, DateTime baselineStart, DateTime start, DateTime end, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(rule.Field))
        {
            throw new QueryParseException("A new-value rule needs a field");
        }

        var query = $"{rule.Query} | stats count by {rule.Field}";
        var seen = (await RunAsync(engine, query, baselineStart, start, 100_000, cancellationToken)).Events
            .Cast<IDictionary<string, object?>>()
            .Select(row => Text(row.TryGetValue(rule.Field, out var value) ? value : null))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (await RunAsync(engine, query, start, end, 10_000, cancellationToken)).Events
            .Cast<IDictionary<string, object?>>()
            .Select(row => (Value: Text(row.TryGetValue(rule.Field, out var value) ? value : null), Count: (long)Count(row)))
            .Where(v => !seen.Contains(v.Value))
            .OrderByDescending(v => v.Count)
            .Select(v => new AnomalyFinding(v.Value, $"New {rule.Field}: {v.Value} ({v.Count} event{(v.Count == 1 ? string.Empty : "s")}), not seen before"))
            .ToList();
    }

    private static async Task<List<AnomalyFinding>> NewPatternsAsync(AnomalyRule rule, IQueryEngine engine, DateTime baselineStart, DateTime start, DateTime end, CancellationToken cancellationToken)
    {
        // The baseline's lines shape the patterns; a current line that starts its own is new
        var drain = new BqlDrain();
        foreach (var row in (await RunAsync(engine, $"{rule.Query} | table RawLine", baselineStart, start, 200_000, cancellationToken)).Events.Cast<IDictionary<string, object?>>())
        {
            drain.Add(Text(row.TryGetValue("RawLine", out var line) ? line : null));
        }

        var known = drain.Patterns.Select(p => p.Id).ToHashSet();
        foreach (var row in (await RunAsync(engine, $"{rule.Query} | table RawLine", start, end, 50_000, cancellationToken)).Events.Cast<IDictionary<string, object?>>())
        {
            drain.Add(Text(row.TryGetValue("RawLine", out var line) ? line : null));
        }

        return drain.Patterns
            .Where(p => !known.Contains(p.Id))
            .OrderByDescending(p => p.Count)
            .Select(p => new AnomalyFinding(p.Template, $"New kind of line ({p.Count}×): {p.Template}"))
            .ToList();
    }

    private static Task DispatchAsync(AlertDispatcher dispatcher, AnomalyRule rule, AnomalyFinding finding, CancellationToken cancellationToken) =>
        dispatcher.DispatchAsync(
            new Alert
            {
                Severity = Enum.TryParse<AlertSeverity>(rule.Severity, true, out var severity) ? severity : AlertSeverity.Warning,
                Title = $"Anomaly: {rule.Name}",
                Message = finding.Message,
                Source = $"Anomaly/{rule.Id}",
                Metadata = new Dictionary<string, object> { ["rule_id"] = rule.Id, ["kind"] = rule.Kind.ToString(), ["key"] = finding.Key },
            },
            new AlertRecord
            {
                SearchName = rule.Name,
                Query = rule.Query,
                ResultCount = 1,
                Channel = "anomaly",
                RuleId = rule.Id,
                GroupValue = finding.Key.Length > 200 ? finding.Key[..200] : finding.Key,
                Message = finding.Message,
            },
            cancellationToken);
}
