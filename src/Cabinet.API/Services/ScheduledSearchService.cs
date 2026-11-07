using System.Collections.Concurrent;
using Cabinet.Alerts;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query;

namespace Cabinet.API.Services;

/// <summary>
/// BackgroundService that manages and executes scheduled searches on cron schedules.
/// Checks every 30 seconds which searches should run, executes their queries,
/// evaluates alert conditions, and fires alerts when triggered.
/// Persists searches to IScheduledSearchStorage so they survive restarts.
/// </summary>
public sealed class ScheduledSearchService : BackgroundService
{
    private readonly ConcurrentDictionary<string, ScheduledSearch> _searches = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IScheduledSearchStorage _storage;
    private readonly ILogger<ScheduledSearchService> _logger;
    private readonly ILeaderLease _lease;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);
    private const int MaxConcurrentSearches = 4;
    private const int MaxResults = QueryContext.MaxLimit;
    private DateTime _lastCheckedMinute = DateTime.MinValue;

    public ScheduledSearchService(
        IServiceScopeFactory scopeFactory,
        IScheduledSearchStorage storage,
        ILogger<ScheduledSearchService> logger,
        ILeaderLease? lease = null)
    {
        _lease = lease ?? new SingleInstanceLease();
        _scopeFactory = scopeFactory;
        _storage = storage;
        _logger = logger;
    }

    /// <summary>
    /// Register a scheduled search for periodic execution
    /// </summary>
    public async Task RegisterAsync(ScheduledSearch search)
    {
        _searches[search.Id] = search;
        await _storage.SaveAsync(search);
        _logger.LogInformation("Registered scheduled search '{Name}' (id: {Id}, cron: {Cron})",
            search.Name, search.Id, search.CronExpression);
    }

    /// <summary>
    /// Unregister a scheduled search
    /// </summary>
    public async Task<bool> UnregisterAsync(string id)
    {
        var removed = _searches.TryRemove(id, out _);
        if (removed)
        {
            await _storage.DeleteAsync(id);
            _logger.LogInformation("Unregistered scheduled search {Id}", id);
        }
        return removed;
    }

    /// <summary>
    /// Get all registered scheduled searches
    /// </summary>
    public IReadOnlyList<ScheduledSearch> GetAll()
    {
        return _searches.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// Get a scheduled search by ID
    /// </summary>
    public ScheduledSearch? Get(string id)
    {
        _searches.TryGetValue(id, out var search);
        return search;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Scheduled search service started");

        // Load persisted searches on startup
        await LoadPersistedSearchesAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Another instance may have changed them, and a failed load shouldn't be permanent
                await LoadPersistedSearchesAsync(stoppingToken);

                // Every instance serves these through the API, but only one runs them
                if (_lease.IsLeader)
                {
                    await CheckAndRunSearchesAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in scheduled search check cycle");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Scheduled search service stopped");
    }

    private async Task LoadPersistedSearchesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var searches = await _storage.GetAllAsync(cancellationToken);
            foreach (var search in searches)
            {
                _searches[search.Id] = search;
            }

            foreach (var removed in _searches.Keys.Except(searches.Select(search => search.Id)).ToList())
            {
                _searches.TryRemove(removed, out _);
            }

            _logger.LogDebug("Loaded {Count} persisted scheduled searches", searches.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load persisted scheduled searches");
        }
    }

    private async Task CheckAndRunSearchesAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var previous = _lastCheckedMinute == DateTime.MinValue ? null : (DateTime?)_lastCheckedMinute;
        _lastCheckedMinute = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc);

        // Run together, so a slow search can't make the others miss their minute
        using var slots = new SemaphoreSlim(MaxConcurrentSearches, MaxConcurrentSearches);
        var running = new List<Task>();
        foreach (var search in _searches.Values.Where(search => search.Enabled && ShouldRun(search, now, previous)))
        {
            await slots.WaitAsync(cancellationToken);
            running.Add(RunReleasingAsync(search, now, slots, cancellationToken));
        }

        await Task.WhenAll(running);
    }

    private async Task RunReleasingAsync(ScheduledSearch search, DateTime now, SemaphoreSlim slots, CancellationToken cancellationToken)
    {
        try
        {
            await RunSearchAsync(search, now, cancellationToken);
        }
        finally
        {
            slots.Release();
        }
    }

    private async Task RunSearchAsync(ScheduledSearch search, DateTime now, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Executing scheduled search '{Name}' (id: {Id})", search.Name, search.Id);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var queryEngine = scope.ServiceProvider.GetRequiredService<IQueryEngine>();

            var context = new QueryExecutionContext
            {
                Query = search.Query,
                Limit = MaxResults,
                Timeout = TimeSpan.FromMinutes(5)
            };

            // Apply time range from the search configuration
            var timeRange = ParseTimeRange(search.TimeRange);
            if (timeRange.HasValue)
            {
                context.StartTime = now.Add(timeRange.Value);
                context.EndTime = now;
            }

            var result = await queryEngine.ExecuteAsync(context, cancellationToken);
            var resultCount = result.Events.Count;

            search.LastRunAt = now;
            search.LastRunResultCount = resultCount;
            search.LastRunSuccess = true;
            search.LastRunError = null;

            _logger.LogDebug(
                "Scheduled search '{Name}' completed: {Count} results",
                search.Name, resultCount);

            // Evaluate alert condition
            if (search.AlertAction != null)
            {
                await EvaluateAlertAsync(search, resultCount, now, scope.ServiceProvider, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            search.LastRunAt = now;
            search.LastRunSuccess = false;
            search.LastRunError = ex.Message;
            _logger.LogError(ex, "Scheduled search '{Name}' (id: {Id}) failed", search.Name, search.Id);
        }

        // Persist run state
        try
        {
            await _storage.SaveAsync(search);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist scheduled search run state for '{Name}'", search.Name);
        }
    }

    private async Task EvaluateAlertAsync(
        ScheduledSearch search,
        int resultCount,
        DateTime now,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var alertAction = search.AlertAction!;

        // Check cooldown
        if (alertAction.LastAlertAt.HasValue &&
            now - alertAction.LastAlertAt.Value < alertAction.Cooldown)
        {
            _logger.LogDebug(
                "Scheduled search '{Name}' alert in cooldown until {Until}",
                search.Name, alertAction.LastAlertAt.Value + alertAction.Cooldown);
            return;
        }

        // Evaluate condition
        var shouldAlert = EvaluateCondition(alertAction.Condition, alertAction.Threshold, resultCount);
        if (!shouldAlert)
        {
            return;
        }

        // Fire alert
        try
        {
            var dispatcher = serviceProvider.GetRequiredService<AlertDispatcher>();
            var alert = new Alert
            {
                Severity = (AlertSeverity)alertAction.Severity,
                Title = $"Scheduled Search Alert: {search.Name}",
                Message = $"Search '{search.Name}' returned {resultCount} results. Condition: {alertAction.Condition} (threshold: {alertAction.Threshold})",
                Source = $"ScheduledSearch/{search.Id}",
                Metadata = new Dictionary<string, object>
                {
                    ["search_id"] = search.Id,
                    ["search_name"] = search.Name,
                    ["query"] = search.Query,
                    ["result_count"] = resultCount,
                    ["time_range"] = search.TimeRange,
                    ["condition"] = alertAction.Condition,
                    ["threshold"] = alertAction.Threshold
                }
            };

            await dispatcher.DispatchAsync(
                alert,
                new AlertRecord
                {
                    SearchName = search.Name,
                    Query = search.Query,
                    ResultCount = resultCount,
                    Channel = "scheduled-search",
                },
                cancellationToken);
            alertAction.LastAlertAt = now;

            _logger.LogInformation(
                "Alert fired for scheduled search '{Name}': {Count} results matched condition '{Condition}'",
                search.Name, resultCount, alertAction.Condition);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send alert for scheduled search '{Name}'", search.Name);
        }
    }

    /// <summary>
    /// Evaluate a simple condition against the result count.
    /// Supports: "count > N", "count >= N", "count < N", "count <= N", "count == N", "count != N"
    /// </summary>
    internal static bool EvaluateCondition(string condition, int threshold, int resultCount)
    {
        var normalized = condition.Trim().ToLowerInvariant();

        if (normalized.Contains(">="))
            return resultCount >= threshold;
        if (normalized.Contains("<="))
            return resultCount <= threshold;
        if (normalized.Contains("!="))
            return resultCount != threshold;
        if (normalized.Contains("=="))
            return resultCount == threshold;
        if (normalized.Contains('>'))
            return resultCount > threshold;
        if (normalized.Contains('<'))
            return resultCount < threshold;

        // Default: trigger if count > 0
        return resultCount > 0;
    }

    /// <summary>
    /// Whether a search is due: its cron expression matches a minute since the last check, and it
    /// hasn't already run in that minute. Looking at every minute since the last check means a
    /// slow check, or a change of leader, doesn't make a search miss its time.
    /// </summary>
    internal static bool ShouldRun(ScheduledSearch search, DateTime now, DateTime? lastCheckedMinute = null)
    {
        var minute = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc);
        var alreadyRan = search.LastRunAt is { } lastRun && lastRun.Ticks / TimeSpan.TicksPerMinute >= minute.Ticks / TimeSpan.TicksPerMinute;
        if (alreadyRan)
        {
            return false;
        }

        // Catch up on at most an hour, so a long outage doesn't look like a due search forever
        var earliest = lastCheckedMinute is { } checkedAt && minute - checkedAt < TimeSpan.FromHours(1) ? checkedAt.AddMinutes(1) : minute;
        if (search.LastRunAt is { } previousRun && previousRun > earliest)
        {
            earliest = new DateTime(previousRun.Ticks - (previousRun.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc).AddMinutes(1);
        }

        for (var candidate = earliest; candidate <= minute; candidate = candidate.AddMinutes(1))
        {
            if (MatchesCron(search.CronExpression, candidate))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Why a cron expression isn't valid, or null if it is. Every value has to be in range for its
    /// field, so one that could never match is an error when it's saved rather than silence later.
    /// </summary>
    internal static string? ValidateCron(string? cronExpression)
    {
        var parts = cronExpression?.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: 5 })
        {
            return "A cron expression has 5 fields: minute hour day-of-month month day-of-week";
        }

        (string Name, int Min, int Max)[] fields = [("minute", 0, 59), ("hour", 0, 23), ("day-of-month", 1, 31), ("month", 1, 12), ("day-of-week", 0, 6)];
        for (var i = 0; i < fields.Length; i++)
        {
            foreach (var segment in parts[i].Split(','))
            {
                if (ParseCronSegment(segment, fields[i].Min, fields[i].Max) == null)
                {
                    return $"'{segment}' isn't valid for {fields[i].Name}, which runs from {fields[i].Min} to {fields[i].Max}";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Match a 5-field cron expression against a DateTime.
    /// Fields: minute hour day-of-month month day-of-week.
    /// Follows POSIX cron semantics: when both day-of-month and day-of-week are
    /// restricted (non-*), the match uses OR (either day matches). When only one
    /// is restricted, it uses AND (all fields must match).
    /// </summary>
    internal static bool MatchesCron(string cronExpression, DateTime now)
    {
        var parts = cronExpression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5)
        {
            return false;
        }

        // Minute, hour, and month must always match
        if (!MatchesCronField(parts[0], now.Minute, 0, 59) ||
            !MatchesCronField(parts[1], now.Hour, 0, 23) ||
            !MatchesCronField(parts[3], now.Month, 1, 12))
        {
            return false;
        }

        var domRestricted = parts[2] != "*";
        var dowRestricted = parts[4] != "*";

        var domMatches = MatchesCronField(parts[2], now.Day, 1, 31);
        var dowMatches = MatchesCronField(parts[4], (int)now.DayOfWeek, 0, 6);

        // POSIX: if both dom and dow are restricted, use OR semantics
        if (domRestricted && dowRestricted)
        {
            return domMatches || dowMatches;
        }

        // Otherwise both must match (unrestricted fields always return true)
        return domMatches && dowMatches;
    }

    /// <summary>
    /// Match a single cron field: a comma-separated list of *, N, N-M, each with an optional /S.
    /// </summary>
    private static bool MatchesCronField(string field, int value, int min, int max)
    {
        foreach (var segment in field.Split(','))
        {
            if (ParseCronSegment(segment, min, max) is var (start, end, step) && value >= start && value <= end && (value - start) % step == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads *, N, N-M or any of them with /S. Null if it's malformed or out of range.</summary>
    private static (int Start, int End, int Step)? ParseCronSegment(string segment, int min, int max)
    {
        var step = 1;
        var slash = segment.IndexOf('/');
        var range = slash < 0 ? segment : segment[..slash];
        if (slash >= 0 && (!int.TryParse(segment[(slash + 1)..], out step) || step < 1))
        {
            return null;
        }

        int start, end;
        if (range == "*")
        {
            (start, end) = (min, max);
        }
        else if (range.Split('-') is [var lower, var upper])
        {
            if (!int.TryParse(lower, out start) || !int.TryParse(upper, out end))
            {
                return null;
            }
        }
        else if (int.TryParse(range, out start))
        {
            // N/S means from N to the end of the field
            end = slash >= 0 ? max : start;
        }
        else
        {
            return null;
        }

        return start >= min && end <= max && start <= end ? (start, end, step) : null;
    }

    /// <summary>
    /// Parse a relative time range string (e.g., "-15m", "-1h", "-24h") into a TimeSpan.
    /// </summary>
    private static TimeSpan? ParseTimeRange(string timeRange)
    {
        if (string.IsNullOrWhiteSpace(timeRange))
        {
            return null;
        }

        var cleaned = timeRange.TrimStart('-');
        if (cleaned.Length < 2)
        {
            return null;
        }

        var unit = cleaned[^1];
        if (!int.TryParse(cleaned[..^1], out var amount))
        {
            return null;
        }

        return unit switch
        {
            'm' => TimeSpan.FromMinutes(-amount),
            'h' => TimeSpan.FromHours(-amount),
            'd' => TimeSpan.FromDays(-amount),
            's' => TimeSpan.FromSeconds(-amount),
            _ => null
        };
    }
}
