using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query.Bql;

namespace Cabinet.API.Services;

/// <summary>What a rule would have done over stored events.</summary>
public sealed class BacktestResult
{
    public long EventsScanned { get; set; }
    public long Matches { get; set; }
    public int WouldFire { get; set; }

    /// <summary>True when the scan stopped at its limit before the end of the range.</summary>
    public bool Truncated { get; set; }

    /// <summary>The groups that would have fired most, most first.</summary>
    public List<BacktestGroup> TopGroups { get; set; } = new();

    /// <summary>The first alerts it would have raised.</summary>
    public List<BacktestFiring> Samples { get; set; } = new();
}

/// <summary>A group and how often it would have fired.</summary>
public sealed record BacktestGroup(string Group, int Fires, long Matches);

/// <summary>One alert the rule would have raised.</summary>
public sealed record BacktestFiring(DateTime Time, string Group, int Count, string EventId, string Line);

/// <summary>
/// Runs a rule over stored events, oldest first, counting and cooling down by event time as the
/// live engine would. Nothing is alerted. It's for tuning a rule against real traffic before
/// turning it on.
/// </summary>
public static class RuleBacktester
{
    private const int PageSize = 5000;
    private const int MaxSamples = 20;
    private const int MaxTopGroups = 20;

    public static async Task<BacktestResult> RunAsync(
        DetectionRule rule,
        ILogStorage storage,
        DateTime start,
        DateTime end,
        long maxEvents,
        CancellationToken cancellationToken = default)
    {
        var matcher = new BqlEventMatcher(rule.Pattern, rule.Condition, rule.Extract);
        var window = TimeSpan.FromSeconds(rule.WindowSeconds);
        var cooldown = TimeSpan.FromSeconds(rule.CooldownSeconds);
        var groups = new Dictionary<string, GroupState>(StringComparer.Ordinal);
        var result = new BacktestResult();
        EventCursor? after = null;

        while (true)
        {
            var page = new List<LogEvent>(PageSize);
            var context = new QueryContext { StartTime = start, EndTime = end, SortDescending = false, Limit = PageSize, After = after };
            await foreach (var logEvent in storage.QueryAsync(context, cancellationToken))
            {
                page.Add(logEvent);
            }

            foreach (var logEvent in page)
            {
                result.EventsScanned++;
                if (!matcher.TryMatch(logEvent, out var fields))
                {
                    continue;
                }

                var group = string.Empty;
                if (!string.IsNullOrEmpty(rule.GroupBy))
                {
                    if (!fields.TryGetValue(rule.GroupBy, out var value) || value?.ToString() is not { Length: > 0 } text)
                    {
                        continue;
                    }

                    group = text;
                }

                result.Matches++;
                if (!groups.TryGetValue(group, out var state))
                {
                    state = new GroupState();
                    groups[group] = state;
                }

                state.Matches++;
                state.Recent.Enqueue(logEvent.Timestamp);
                while (state.Recent.TryPeek(out var oldest) && logEvent.Timestamp - oldest > window)
                {
                    state.Recent.Dequeue();
                }

                if (state.Recent.Count < Math.Max(1, rule.Threshold) || (state.LastFired is { } last && logEvent.Timestamp - last < cooldown))
                {
                    continue;
                }

                result.WouldFire++;
                state.Fires++;
                state.LastFired = logEvent.Timestamp;
                if (result.Samples.Count < MaxSamples)
                {
                    result.Samples.Add(new BacktestFiring(logEvent.Timestamp, group, state.Recent.Count, logEvent.Id, logEvent.RawLine[..Math.Min(300, logEvent.RawLine.Length)]));
                }

                state.Recent.Clear();
            }

            if (result.EventsScanned >= maxEvents)
            {
                result.Truncated = page.Count == PageSize;
                break;
            }

            if (page.Count < PageSize)
            {
                break;
            }

            after = new EventCursor(page[^1].Timestamp, page[^1].Id);
        }

        result.TopGroups = groups
            .Where(pair => pair.Value.Fires > 0)
            .OrderByDescending(pair => pair.Value.Fires)
            .ThenByDescending(pair => pair.Value.Matches)
            .Take(MaxTopGroups)
            .Select(pair => new BacktestGroup(pair.Key, pair.Value.Fires, pair.Value.Matches))
            .ToList();
        return result;
    }

    private sealed class GroupState
    {
        public Queue<DateTime> Recent { get; } = new();
        public DateTime? LastFired { get; set; }
        public int Fires { get; set; }
        public long Matches { get; set; }
    }
}
