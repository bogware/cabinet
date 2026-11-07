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
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

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

        foreach (var search in _searches.Values)
        {
            if (!search.Enabled)
            {
                continue;
            }

            if (ShouldRun(search, now))
            {
                await RunSearchAsync(search, now, cancellationToken);
            }
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
                Limit = 10000
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
            var alertService = serviceProvider.GetRequiredService<IAlertService>();
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

            await alertService.SendAlertAsync(alert, cancellationToken);
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
    /// Determine if a scheduled search should run based on its cron expression.
    /// Simple 5-field cron matcher: minute hour dom month dow.
    /// Supports: numbers, *, ranges (1-5), and steps (star/N).
    /// </summary>
    internal static bool ShouldRun(ScheduledSearch search, DateTime now)
    {
        // Don't run if it ran within the last 30 seconds
        if (search.LastRunAt.HasValue &&
            now - search.LastRunAt.Value < CheckInterval)
        {
            return false;
        }

        return MatchesCron(search.CronExpression, now);
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
    /// Match a single cron field. Supports: *, N, N-M, star/N, N-M/S
    /// </summary>
    private static bool MatchesCronField(string field, int value, int min, int max)
    {
        if (field == "*")
        {
            return true;
        }

        // Handle step: */N or N-M/S
        var stepParts = field.Split('/');
        if (stepParts.Length == 2 && int.TryParse(stepParts[1], out var step) && step > 0)
        {
            var rangePart = stepParts[0];
            int rangeStart = min;
            int rangeEnd = max;

            if (rangePart != "*")
            {
                var rangeBounds = rangePart.Split('-');
                if (rangeBounds.Length == 2 &&
                    int.TryParse(rangeBounds[0], out var rs) &&
                    int.TryParse(rangeBounds[1], out var re))
                {
                    rangeStart = rs;
                    rangeEnd = re;
                }
                else if (int.TryParse(rangePart, out var singleStart))
                {
                    rangeStart = singleStart;
                }
            }

            for (var i = rangeStart; i <= rangeEnd; i += step)
            {
                if (i == value) return true;
            }
            return false;
        }

        // Handle comma-separated values
        var segments = field.Split(',');
        foreach (var segment in segments)
        {
            // Range: N-M
            if (segment.Contains('-'))
            {
                var bounds = segment.Split('-');
                if (bounds.Length == 2 &&
                    int.TryParse(bounds[0], out var lower) &&
                    int.TryParse(bounds[1], out var upper))
                {
                    if (value >= lower && value <= upper)
                    {
                        return true;
                    }
                }
            }
            else if (int.TryParse(segment, out var exact))
            {
                if (exact == value)
                {
                    return true;
                }
            }
        }

        return false;
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
