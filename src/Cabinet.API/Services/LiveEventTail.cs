using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;

namespace Cabinet.API.Services;

/// <summary>
/// Finds events that have arrived since the last poll, for a live stream. An event's timestamp is
/// when it happened, not when it arrived, so a batch from an agent lands behind the newest time
/// already seen. So each poll looks back over a short window and remembers what it has sent.
/// </summary>
public sealed class LiveEventTail
{
    /// <summary>How late an event can arrive and still appear.</summary>
    public static readonly TimeSpan LateArrivalWindow = TimeSpan.FromSeconds(30);

    /// <summary>The most events read in a poll. On a busier stream, the newest are shown.</summary>
    public const int MaxEventsPerPoll = 5000;

    private readonly ILogStorage _storage;
    private readonly string _index;
    private readonly string? _filter;
    private readonly DateTime _startedAt;
    private readonly Dictionary<string, DateTime> _sent = new();

    public LiveEventTail(ILogStorage storage, string index, string? filter, DateTime startedAt)
    {
        _storage = storage;
        _index = index;
        _filter = filter;
        _startedAt = startedAt;
    }

    /// <summary>Returns events not returned before, oldest first.</summary>
    public async Task<List<LogEvent>> PollAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        var windowStart = now - LateArrivalWindow;
        if (windowStart < _startedAt)
        {
            windowStart = _startedAt;
        }

        var context = new QueryContext
        {
            Index = _index,
            StartTime = windowStart,
            SearchText = _filter,
            SortField = "Timestamp",
            SortDescending = true,
            Limit = MaxEventsPerPoll,
        };

        var fresh = new List<LogEvent>();
        await foreach (var logEvent in _storage.QueryAsync(context, cancellationToken))
        {
            if (_sent.TryAdd(logEvent.Id, logEvent.Timestamp))
            {
                fresh.Add(logEvent);
            }
        }

        // Older than the window can't come back, so it needn't be remembered
        foreach (var (id, timestamp) in _sent.Where(sent => sent.Value < windowStart).ToList())
        {
            _sent.Remove(id);
        }

        fresh.Reverse();
        return fresh;
    }
}
