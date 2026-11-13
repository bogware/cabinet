using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cabinet.Storage.Engine;

/// <summary>
/// Cabinet's own storage engine: one folder per index, each a write-ahead log, events in memory,
/// and immutable column segments. Built for one machine and no other services: searches skip
/// blocks by time, severity, host, source type and a filter of their words, and read only the
/// columns they need.
/// </summary>
public sealed partial class EngineLogStorage : ILogStorage, IDisposable
{
    private readonly string _root;
    private readonly EngineOptions _options;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, IndexShard> _shards = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _shardLock = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private volatile bool _initialized;
    private Timer? _flushTimer;
    private int _compacting;

    public EngineLogStorage(string dataPath, EngineOptions? options = null, ILogger<EngineLogStorage>? logger = null)
    {
        _root = Path.GetFullPath(dataPath);
        _options = options ?? new EngineOptions();
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Where the data is.</summary>
    public string DataPath => _root;

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(_root);
            foreach (var directory in Directory.EnumerateDirectories(_root))
            {
                if (IndexShard.IndexNameIn(directory) is not { } index)
                {
                    continue;
                }

                if (_shards.ContainsKey(index))
                {
                    _logger.LogError("Folder {Folder} holds index {Index}, which another folder holds too; it was left unopened", directory, index);
                    continue;
                }

                _shards[index] = IndexShard.Open(directory, index, _options, _logger);
            }

            var period = TimeSpan.FromSeconds(Math.Max(1, _options.FlushSeconds) / 2.0);
            _flushTimer = new Timer(_ => _ = FlushDueAsync(), null, period, period);
            _initialized = true;
            _logger.LogInformation("Cabinet engine opened {Count} indexes in {Path}", _shards.Count, _root);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task FlushDueAsync()
    {
        foreach (var shard in _shards.Values)
        {
            try
            {
                await shard.FlushAsync(force: false, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Writing index {Index} to a segment failed; its events are still in the log", shard.Index);
            }
        }

        // One merge pass at a time, in the background
        if (Interlocked.CompareExchange(ref _compacting, 1, 0) == 0)
        {
            try
            {
                await CompactAsync(_stopping.Token);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                _logger.LogDebug("Merging segments stopped for shutdown");
            }
            finally
            {
                Volatile.Write(ref _compacting, 0);
            }
        }
    }

    /// <summary>Merges segments until none are due, in every index. Returns how many merges ran.</summary>
    public async Task<int> CompactAsync(CancellationToken cancellationToken = default)
    {
        var merges = 0;
        foreach (var shard in _shards.Values)
        {
            try
            {
                while (await shard.CompactAsync(cancellationToken))
                {
                    merges++;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Merging segments of index {Index} failed; its segments are unchanged", shard.Index);
            }
        }

        return merges;
    }

    /// <summary>Writes every index's in-memory events to segments now.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        foreach (var shard in _shards.Values)
        {
            await shard.FlushAsync(force: true, cancellationToken);
        }
    }

    /// <summary>Longest folder name given to an index; well inside every file system's limit.</summary>
    internal const int MaxFolderName = 96;

    /// <summary>
    /// The folder name an index is first offered: its name, with anything but ASCII letters,
    /// digits, '-' and '_' escaped. Unchanged from earlier versions for names that fit, so their
    /// folders keep their names. A name that escapes to more than <see cref="MaxFolderName"/>
    /// characters becomes a prefix of it and a hash of the whole name.
    /// </summary>
    internal static string FolderName(string index)
    {
        var safe = new StringBuilder();
        foreach (var c in index)
        {
            safe.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : $"~{(int)c:x4}");
        }

        if (safe.Length <= MaxFolderName)
        {
            return safe.ToString();
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(index)))[..16];
        return $"{safe.ToString(0, MaxFolderName - hash.Length - 1)}~{hash}";
    }

    /// <summary>
    /// A folder for a new index: its <see cref="FolderName"/>, or that with a number after it when
    /// the folder already holds another index. Names that differ only in case ("Security" and
    /// "security") get the same folder name, which is one folder where the file system ignores
    /// case, as on Windows and macOS. Any folder will do, since opening reads each folder's
    /// <c>index.name</c>. Called under the shard lock.
    /// </summary>
    private string FolderFor(string index)
    {
        var name = FolderName(index);
        for (var n = 1; ; n++)
        {
            var folder = Path.Combine(_root, n == 1 ? name : $"{name}~{n}");
            if (!Directory.Exists(folder) || IndexShard.IndexNameIn(folder) is not { } existing || existing == index)
            {
                return folder;
            }
        }
    }

    private async Task<IndexShard> ShardAsync(string index, CancellationToken cancellationToken)
    {
        if (_shards.TryGetValue(index, out var shard))
        {
            return shard;
        }

        await _shardLock.WaitAsync(cancellationToken);
        try
        {
            return _shards.GetOrAdd(index, name => IndexShard.Open(FolderFor(name), name, _options, _logger));
        }
        finally
        {
            _shardLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> StoreAsync(LogEvent logEvent, CancellationToken cancellationToken = default) =>
        await StoreBatchAsync([logEvent], cancellationToken) == 1;

    /// <inheritdoc />
    public async Task<int> StoreBatchAsync(IEnumerable<LogEvent> events, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var stored = 0;
        foreach (var group in events.GroupBy(e => string.IsNullOrEmpty(e.Index) ? "main" : e.Index, StringComparer.Ordinal))
        {
            var shard = await ShardAsync(group.Key, cancellationToken);
            stored += await shard.AddAsync(group.ToList(), cancellationToken);
        }

        return stored;
    }

    private IEnumerable<IndexShard> ShardsFor(QueryContext context) => _shards.Values.Where(shard =>
        (string.IsNullOrEmpty(context.Index) || shard.Index == context.Index)
        && (context.AllowedIndexes is not { } allowed || allowed.Any(pattern => IndexPermission.Matches(pattern, shard.Index))));

    /// <inheritdoc />
    public async IAsyncEnumerable<LogEvent> QueryAsync(QueryContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var filter = new RowFilter(context);
        var sources = new List<RowSource>();
        var snapshots = new List<ShardSnapshot>();
        try
        {
            foreach (var shard in ShardsFor(context))
            {
                var snapshot = shard.Snapshot();
                snapshots.Add(snapshot);
                sources.Add(RowSource.FromHot(snapshot.Hot, filter, context.SortDescending));
                sources.AddRange(snapshot.Segments.Where(filter.MayMatch).Select(segment => RowSource.FromSegment(segment, filter, context.SortDescending)));
            }

            foreach (var logEvent in Read(context, sources, cancellationToken))
            {
                yield return logEvent;
            }
        }
        finally
        {
            snapshots.ForEach(snapshot => snapshot.Dispose());
        }
    }

    /// <summary>The matches of a query's sources, in its order, after its skip and up to its limit.</summary>
    private static IEnumerable<LogEvent> Read(QueryContext context, List<RowSource> sources, CancellationToken cancellationToken)
    {
        if (!context.SortField.Equals("Timestamp", StringComparison.OrdinalIgnoreCase))
        {
            // Other sorts need every match first; only severity is asked for
            var all = sources.SelectMany(s => s.All(cancellationToken)).Select(r => r.Materialize()).ToList();
            var ordered = context.SortField.Equals("severity", StringComparison.OrdinalIgnoreCase)
                ? context.SortDescending
                    ? all.OrderByDescending(e => e.Severity).ThenByDescending(e => e.Id, StringComparer.Ordinal)
                    : all.OrderBy(e => e.Severity).ThenBy(e => e.Id, StringComparer.Ordinal)
                : context.SortDescending
                    ? all.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Id, StringComparer.Ordinal)
                    : all.OrderBy(e => e.Timestamp).ThenBy(e => e.Id, StringComparer.Ordinal);
            foreach (var logEvent in ordered.Skip(context.Skip).Take(context.Limit))
            {
                yield return logEvent;
            }

            yield break;
        }

        var skip = context.Skip;
        var remaining = context.Limit;
        foreach (var row in RowSource.Merge(sources, context.SortDescending, cancellationToken))
        {
            if (remaining <= 0)
            {
                yield break;
            }

            if (skip > 0)
            {
                skip--;
                continue;
            }

            remaining--;
            yield return row.Materialize();
        }
    }

    /// <inheritdoc />
    public async Task<LogEvent?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        foreach (var shard in _shards.Values)
        {
            using var snapshot = shard.Snapshot();
            var (hot, segments) = snapshot;
            if (hot.FirstOrDefault(e => e.Id == id) is { } found)
            {
                return found;
            }

            foreach (var segment in segments)
            {
                foreach (var block in segment.Blocks.Where(b => b.Ids.MayContain(id)))
                {
                    var rows = new BlockRows(segment, block);
                    var at = Array.IndexOf(rows.Ids, id);
                    if (at >= 0)
                    {
                        return rows.Materialize(at);
                    }
                }
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<long> GetCountAsync(QueryContext? context = null, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        context ??= new QueryContext();
        var filter = new RowFilter(context);
        long count = 0;
        foreach (var shard in ShardsFor(context))
        {
            using var snapshot = shard.Snapshot();
            var (hot, segments) = snapshot;
            count +=hot.Count(filter.Matches);
            var blocks = segments.Where(filter.MayMatch).SelectMany(segment => segment.Blocks.Where(filter.MayMatch).Select(block => (segment, block))).ToList();
            long counted = 0;
            Parallel.ForEach(blocks, new ParallelOptions { CancellationToken = cancellationToken }, work =>
                Interlocked.Add(ref counted, filter.WholeBlock(work.block) ? work.block.Rows : filter.CountIn(new BlockRows(work.segment, work.block))));
            count += counted;
        }

        return count;
    }

    /// <inheritdoc />
    public async Task<int> PurgeOldEventsAsync(DateTime before, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var deleted = 0;
        foreach (var shard in _shards.Values)
        {
            deleted += await shard.PurgeAsync(before, cancellationToken);
        }

        return deleted;
    }

    /// <inheritdoc />
    public async Task<int> PurgeIndexEventsAsync(string indexName, DateTime before, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return _shards.TryGetValue(indexName, out var shard) ? await shard.PurgeAsync(before, cancellationToken) : 0;
    }

    /// <inheritdoc />
    public async Task<int> DeleteByIndexAsync(string indexName, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _shardLock.WaitAsync(cancellationToken);
        try
        {
            return _shards.TryRemove(indexName, out var shard) ? await shard.DeleteAllAsync(cancellationToken) : 0;
        }
        finally
        {
            _shardLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<StorageStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var stats = new StorageStats();
        long? oldest = null;
        long? newest = null;
        foreach (var shard in _shards.Values)
        {
            long total = 0;
            using var snapshot = shard.Snapshot();
            var (hot, segments) = snapshot;
            foreach (var logEvent in hot)
            {
                Add(stats.EventsBySourceType, logEvent.SourceType ?? string.Empty, 1);
                Add(stats.EventsBySeverity, logEvent.Severity, 1);
                oldest = Math.Min(oldest ?? long.MaxValue, logEvent.Timestamp.Ticks);
                newest = Math.Max(newest ?? long.MinValue, logEvent.Timestamp.Ticks);
            }

            total += hot.Length;
            stats.StorageSizeBytes += shard.WalBytes;
            foreach (var segment in segments)
            {
                total += segment.Rows;
                stats.StorageSizeBytes += segment.SizeBytes;
                if (segment.Rows > 0)
                {
                    oldest = Math.Min(oldest ?? long.MaxValue, segment.MinTicks);
                    newest = Math.Max(newest ?? long.MinValue, segment.MaxTicks);
                }

                foreach (var block in segment.Blocks)
                {
                    foreach (var (sourceType, count) in block.SourceTypeCounts)
                    {
                        Add(stats.EventsBySourceType, sourceType, count);
                    }

                    foreach (var (severity, count) in block.SeverityCounts)
                    {
                        Add(stats.EventsBySeverity, (LogSeverity)severity, count);
                    }
                }
            }

            if (total > 0)
            {
                stats.EventsByIndex[shard.Index] = total;
            }

            stats.TotalEvents += total;
        }

        stats.OldestEvent = oldest is { } o ? new DateTime(o, DateTimeKind.Utc) : null;
        stats.NewestEvent = newest is { } n ? new DateTime(n, DateTimeKind.Utc) : null;
        return stats;
    }

    private static void Add<TKey>(Dictionary<TKey, long> counts, TKey key, long count)
        where TKey : notnull =>
        counts[key] = counts.GetValueOrDefault(key) + count;

    public void Dispose()
    {
        _flushTimer?.Dispose();
        _stopping.Cancel();
        foreach (var shard in _shards.Values)
        {
            shard.Dispose();
        }

        _shards.Clear();
    }
}

/// <summary>A query's filters, applied to whole segments and blocks first, then rows.</summary>
internal sealed class RowFilter
{
    private readonly QueryContext _context;
    private readonly IReadOnlyList<string> _terms;
    private readonly long _start;
    private readonly long _end;
    private readonly string? _host;
    private readonly string? _sourceType;

    public RowFilter(QueryContext context)
    {
        _context = context;
        _terms = SearchTerms.Parse(context.SearchText);
        _start = context.StartTime?.Ticks ?? long.MinValue;
        _end = context.EndTime?.Ticks ?? long.MaxValue;
        _host = string.IsNullOrWhiteSpace(context.Host) ? null : context.Host.ToLowerInvariant();
        _sourceType = string.IsNullOrEmpty(context.SourceType) ? null : context.SourceType.ToLowerInvariant();
    }

    public EventCursor? After => _context.After;

    /// <summary>
    /// How many blocks the next scan reads ahead. Shared by a query's segments, so once a search
    /// has shown it reads far, later segments start at full width rather than one block.
    /// </summary>
    public int ScanWindow { get; set; } = 1;

    /// <summary>Whether matching rows costs more than reading times: text, fields or tags to check.</summary>
    public bool Expensive => _terms.Count > 0 || _context.FieldFilters is { Count: > 0 } || _context.Tags is { Count: > 0 };

    public bool MayMatch(SegmentReader segment) => segment.Rows > 0 && segment.MaxTicks >= _start && segment.MinTicks <= _end;

    /// <summary>Whether a block could hold a match, from its summary alone.</summary>
    public bool MayMatch(BlockInfo block)
    {
        if (block.MaxTicks < _start || block.MinTicks > _end)
        {
            return false;
        }

        if (_context.MinSeverity is { } severity && block.MaxSeverity < (int)severity)
        {
            return false;
        }

        if (_host != null && block.Hosts != null && !block.Hosts.Contains(_host))
        {
            return false;
        }

        if (_sourceType != null && block.SourceTypes != null && !block.SourceTypes.Contains(_sourceType))
        {
            return false;
        }

        return _terms.All(block.Words.MayContain);
    }

    /// <summary>Whether every row of a block matches, so it can be counted without reading it.</summary>
    public bool WholeBlock(BlockInfo block) =>
        block.MinTicks >= _start && block.MaxTicks <= _end
        && _terms.Count == 0 && _host == null && _context.Tags is not { Count: > 0 } && _context.FieldFilters is not { Count: > 0 }
        && (_sourceType == null || (block.SourceTypes is { Count: 1 } types && types.Contains(_sourceType)))
        && (_context.MinSeverity is not { } severity || block.MinSeverity >= (int)severity);

    public int CountIn(BlockRows rows)
    {
        var count = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            if (Matches(rows, i))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Whether a stored row matches, reading only the columns the filters need.</summary>
    public bool Matches(BlockRows rows, int i)
    {
        var ticks = rows.Ticks[i];
        if (ticks < _start || ticks > _end)
        {
            return false;
        }

        if (_context.MinSeverity is { } severity && rows.Severities[i] < (int)severity)
        {
            return false;
        }

        if (_host != null && !string.Equals(rows.Hosts[i], _context.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_sourceType != null && !string.Equals(rows.SourceTypes[i], _context.SourceType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_terms.Count > 0 && !rows.TermMatches(_terms)[i])
        {
            return false;
        }

        if (_context.FieldFilters is { Count: > 0 } filters)
        {
            foreach (var (name, expected) in filters)
            {
                if (!rows.HasField(name, i) || !FieldMatches(rows.Field(name)![i], expected))
                {
                    return false;
                }
            }
        }

        return _context.Tags is not { Count: > 0 } tags || tags.All(rows.Materialize(i).Tags.Contains);
    }

    /// <summary>Whether an in-memory event matches.</summary>
    public bool Matches(LogEvent e)
    {
        var ticks = e.Timestamp.Ticks;
        if (ticks < _start || ticks > _end)
        {
            return false;
        }

        if (_context.MinSeverity is { } severity && e.Severity < severity)
        {
            return false;
        }

        if (_host != null && !string.Equals(e.Host, _context.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_sourceType != null && !string.Equals(e.SourceType, _context.SourceType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_terms.Count > 0 && !SearchTerms.Matches(e.RawLine, _terms))
        {
            return false;
        }

        if (_context.FieldFilters is { Count: > 0 } filters)
        {
            foreach (var (name, expected) in filters)
            {
                if (!e.Fields.TryGetValue(name, out var value) || !FieldMatches(value, expected))
                {
                    return false;
                }
            }
        }

        return _context.Tags is not { Count: > 0 } tags || tags.All(e.Tags.Contains);
    }

    private static bool FieldMatches(object? value, object? expected) => expected is object?[] any
        ? any.Any(option => string.Equals(value?.ToString(), option?.ToString(), StringComparison.OrdinalIgnoreCase))
        : string.Equals(value?.ToString(), expected?.ToString(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether (ticks, id) comes after the cursor in the query's order.</summary>
    public bool PastCursor(long ticks, string id, bool descending)
    {
        if (_context.After is not { } after)
        {
            return true;
        }

        var cursor = after.Timestamp.Ticks;
        return descending
            ? ticks < cursor || (ticks == cursor && string.CompareOrdinal(id, after.Id) < 0)
            : ticks > cursor || (ticks == cursor && string.CompareOrdinal(id, after.Id) > 0);
    }
}

/// <summary>One match: from memory, or a row of a block.</summary>
internal readonly record struct Row(long Ticks, string Id, LogEvent? Hot, BlockRows? Block, int Index)
{
    public LogEvent Materialize() => Hot ?? Block!.Materialize(Index);
}

/// <summary>A sorted stream of matches from one place, and the merge of several.</summary>
internal sealed class RowSource
{
    private readonly Func<CancellationToken, IEnumerable<Row>> _rows;

    private RowSource(long first, long last, Func<CancellationToken, IEnumerable<Row>> rows)
    {
        First = first;
        Last = last;
        _rows = rows;
    }

    /// <summary>The earliest and latest times this source can hold.</summary>
    public long First { get; }
    public long Last { get; }

    public IEnumerable<Row> All(CancellationToken cancellationToken) => _rows(cancellationToken);

    public static RowSource FromHot(LogEvent[] events, RowFilter filter, bool descending)
    {
        var matches = events.Where(filter.Matches).Where(e => filter.PastCursor(e.Timestamp.Ticks, e.Id, descending));
        var ordered = (descending
            ? matches.OrderByDescending(e => e.Timestamp.Ticks).ThenByDescending(e => e.Id, StringComparer.Ordinal)
            : matches.OrderBy(e => e.Timestamp.Ticks).ThenBy(e => e.Id, StringComparer.Ordinal)).ToList();
        return new RowSource(
            ordered.Count == 0 ? long.MaxValue : ordered.Min(e => e.Timestamp.Ticks),
            ordered.Count == 0 ? long.MinValue : ordered.Max(e => e.Timestamp.Ticks),
            _ => ordered.Select(e => new Row(e.Timestamp.Ticks, e.Id, e, null, -1)));
    }

    public static RowSource FromSegment(SegmentReader segment, RowFilter filter, bool descending) =>
        new(segment.MinTicks, segment.MaxTicks, token => SegmentRows(segment, filter, descending, token));

    /// <summary>
    /// A segment's matches in order. Where matching is costly, blocks are scanned ahead in parallel,
    /// one at first and then twice as many each time up to the core count, so a query that stops
    /// after a few matches reads little more than it needs, and one that reads far gets every core.
    /// </summary>
    private static IEnumerable<Row> SegmentRows(SegmentReader segment, RowFilter filter, bool descending, CancellationToken cancellationToken)
    {
        var cursor = filter.After?.Timestamp.Ticks;
        var blocks = (descending ? Enumerable.Reverse(segment.Blocks) : segment.Blocks)
            .Where(block => filter.MayMatch(block) && !(cursor is { } c && (descending ? block.MinTicks > c : block.MaxTicks < c)));
        using var pending = blocks.GetEnumerator();
        var most = filter.Expensive ? Environment.ProcessorCount * 2 : 1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = Math.Min(filter.ScanWindow, most);
            var batch = new List<BlockInfo>(window);
            while (batch.Count < window && pending.MoveNext())
            {
                batch.Add(pending.Current);
            }

            if (batch.Count == 0)
            {
                yield break;
            }

            var scanned = new (BlockRows Rows, List<int> Matches)[batch.Count];
            if (batch.Count == 1)
            {
                scanned[0] = Scan(segment, batch[0], filter, descending);
            }
            else
            {
                Parallel.For(0, batch.Count, new ParallelOptions { CancellationToken = cancellationToken }, b => scanned[b] = Scan(segment, batch[b], filter, descending));
            }

            foreach (var (rows, matches) in scanned)
            {
                foreach (var i in matches)
                {
                    yield return new Row(rows.Ticks[i], rows.Ids[i], null, rows, i);
                }
            }

            filter.ScanWindow = Math.Min(window * 4, most);
        }
    }

    /// <summary>A block's matching rows, in the query's order.</summary>
    private static (BlockRows Rows, List<int> Matches) Scan(SegmentReader segment, BlockInfo block, RowFilter filter, bool descending)
    {
        var rows = new BlockRows(segment, block);
        var matches = new List<int>();
        for (var n = 0; n < rows.Count; n++)
        {
            var i = descending ? rows.Count - 1 - n : n;
            if (filter.Matches(rows, i) && filter.PastCursor(rows.Ticks[i], rows.Ids[i], descending))
            {
                matches.Add(i);
            }
        }

        rows.Matched = matches.Count;
        return (rows, matches);
    }

    /// <summary>
    /// Merges sorted sources into one sorted stream. A source joins only once the merge reaches
    /// its time range, so a query that stops early never opens the segments it didn't need.
    /// </summary>
    public static IEnumerable<Row> Merge(List<RowSource> sources, bool descending, CancellationToken cancellationToken)
    {
        // Sources in the order the merge reaches them
        var pending = new Queue<RowSource>(descending ? sources.OrderByDescending(s => s.Last) : sources.OrderBy(s => s.First));
        var heap = new PriorityQueue<IEnumerator<Row>, (long Ticks, string Id)>(Comparer<(long Ticks, string Id)>.Create((a, b) =>
        {
            var byTime = a.Ticks.CompareTo(b.Ticks);
            var order = byTime != 0 ? byTime : string.CompareOrdinal(a.Id, b.Id);
            return descending ? -order : order;
        }));

        try
        {
            while (true)
            {
                // Bring in every source that could hold the next row
                while (pending.Count > 0 && (heap.Count == 0 || Reaches(pending.Peek(), heap, descending)))
                {
                    var source = pending.Dequeue();
                    var rows = source.All(cancellationToken).GetEnumerator();
                    if (rows.MoveNext())
                    {
                        heap.Enqueue(rows, (rows.Current.Ticks, rows.Current.Id));
                    }
                    else
                    {
                        rows.Dispose();
                    }
                }

                if (heap.Count == 0)
                {
                    yield break;
                }

                var next = heap.Dequeue();
                yield return next.Current;
                if (next.MoveNext())
                {
                    heap.Enqueue(next, (next.Current.Ticks, next.Current.Id));
                }
                else
                {
                    next.Dispose();
                }
            }
        }
        finally
        {
            while (heap.TryDequeue(out var rows, out _))
            {
                rows.Dispose();
            }
        }
    }

    private static bool Reaches(RowSource source, PriorityQueue<IEnumerator<Row>, (long Ticks, string Id)> heap, bool descending)
    {
        heap.TryPeek(out _, out var top);
        return descending ? source.Last >= top.Ticks : source.First <= top.Ticks;
    }
}

