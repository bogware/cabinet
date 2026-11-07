using System.Collections.Concurrent;

namespace Cabinet.Core.Interfaces;

/// <summary>
/// State that has to agree across API instances: how many are running, and what detection rules
/// have counted and fired. With one instance it lives in memory. With several it lives in the
/// shared database, so a threshold counts events whichever instance received them, and a rule
/// fires once rather than once per instance.
/// </summary>
public interface IClusterCoordinator
{
    /// <summary>Records that this instance is running, and returns how many are.</summary>
    Task<int> HeartbeatAsync(CancellationToken cancellationToken = default);

    /// <summary>Instances running, as of the last heartbeat. At least 1.</summary>
    int LiveInstances { get; }

    /// <summary>
    /// Records a rule match and returns the matches for that rule and group from
    /// <paramref name="windowStart"/> on. The same event recorded twice counts once.
    /// </summary>
    Task<long> RecordMatchAsync(string ruleId, string group, string eventId, DateTime eventTime, DateTime windowStart, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims the right to fire for a rule and group, unless it fired within the cooldown. Exactly
    /// one caller succeeds at a time. A success clears the matches counted so far.
    /// </summary>
    Task<bool> TryFireAsync(string ruleId, string group, TimeSpan cooldown, CancellationToken cancellationToken = default);
}

/// <summary>For a single instance.</summary>
public sealed class InMemoryClusterCoordinator : IClusterCoordinator
{
    private const int MaxGroups = 200_000;

    private readonly ConcurrentDictionary<(string Rule, string Group), MatchState> _matches = new();
    private readonly ConcurrentDictionary<(string Rule, string Group), DateTime> _fired = new();

    /// <inheritdoc />
    public int LiveInstances => 1;

    /// <inheritdoc />
    public Task<int> HeartbeatAsync(CancellationToken cancellationToken = default) => Task.FromResult(1);

    /// <inheritdoc />
    public Task<long> RecordMatchAsync(string ruleId, string group, string eventId, DateTime eventTime, DateTime windowStart, CancellationToken cancellationToken = default)
    {
        if (_matches.Count >= MaxGroups)
        {
            _matches.Clear();
        }

        var state = _matches.GetOrAdd((ruleId, group), _ => new MatchState());
        lock (state)
        {
            state.Events[eventId] = eventTime;
            foreach (var old in state.Events.Where(pair => pair.Value < windowStart).Select(pair => pair.Key).ToList())
            {
                state.Events.Remove(old);
            }

            return Task.FromResult((long)state.Events.Count);
        }
    }

    /// <inheritdoc />
    public Task<bool> TryFireAsync(string ruleId, string group, TimeSpan cooldown, CancellationToken cancellationToken = default)
    {
        var key = (ruleId, group);
        var now = DateTime.UtcNow;
        while (true)
        {
            if (_fired.TryGetValue(key, out var last))
            {
                if (now - last < cooldown || !_fired.TryUpdate(key, now, last))
                {
                    return Task.FromResult(false);
                }
            }
            else if (!_fired.TryAdd(key, now))
            {
                continue;
            }

            if (_matches.TryGetValue(key, out var state))
            {
                lock (state)
                {
                    state.Events.Clear();
                }
            }

            return Task.FromResult(true);
        }
    }

    private sealed class MatchState
    {
        public Dictionary<string, DateTime> Events { get; } = new();
    }
}
