using System.Text;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cabinet.Storage.Engine;

/// <summary>How the engine trades memory, durability and file count.</summary>
public sealed class EngineOptions
{
    /// <summary>Events an index holds in memory before writing them as a segment.</summary>
    public int FlushEvents { get; set; } = 100_000;

    /// <summary>Most seconds events stay in memory before being written as a segment.</summary>
    public int FlushSeconds { get; set; } = 10;

    /// <summary>
    /// Whether each write waits for the log to reach the disk. Off, a power cut can lose the last
    /// writes the server accepted; a crash of the process alone can't.
    /// </summary>
    public bool SyncWrites { get; set; } = true;

    /// <summary>
    /// Segments are merged until they hold about this many events. Larger segments mean fewer
    /// files and duplicate checks, but more to rewrite when a purge cuts through one.
    /// </summary>
    public int MaxSegmentEvents { get; set; } = 5_000_000;
}

/// <summary>
/// One index: a write-ahead log and in-memory events, and the immutable segments they're written
/// to. A flush freezes the in-memory events and starts a new log, then writes the segment in the
/// background while writes carry on; reads see the frozen events until the segment replaces them.
/// </summary>
internal sealed class IndexShard : IDisposable
{
    private const string WalPrefix = "wal-";
    private readonly string _directory;
    private readonly EngineOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // Held by whatever replaces segments (merges, purges, deleting the index), so they take turns.
    // Taken before the write lock, never after it.
    private readonly SemaphoreSlim _maintenanceLock = new(1, 1);

    /// <summary>Segments merged at once, and how many of a size there must be before they are.</summary>
    private const int MergeWidth = 10;

    private List<LogEvent> _hot = [];
    private HashSet<string> _hotIds = new(StringComparer.Ordinal);

    // Events being written to a segment, and the logs that hold them and anything older in memory
    private List<LogEvent> _frozen = [];
    private HashSet<string> _frozenIds = new(StringComparer.Ordinal);
    private readonly List<string> _unflushedLogs = [];
    private Task<bool> _flushing = Task.FromResult(true);
    private volatile SegmentReader[] _segments = [];
    private FileStream? _wal;
    private long _walGeneration;
    private DateTime _oldestHot = DateTime.MaxValue;
    private bool _disposed;

    private IndexShard(string directory, string index, EngineOptions options, ILogger logger)
    {
        _directory = directory;
        Index = index;
        _options = options;
        _logger = logger;
    }

    public string Index { get; }

    /// <summary>Opens or creates an index's folder, recovering what the log holds.</summary>
    public static IndexShard Open(string directory, string index, EngineOptions options, ILogger logger)
    {
        Directory.CreateDirectory(directory);
        var nameFile = Path.Combine(directory, "index.name");
        if (!File.Exists(nameFile))
        {
            File.WriteAllText(nameFile, index, Encoding.UTF8);
        }

        var shard = new IndexShard(directory, index, options, logger);
        shard.LoadSegments();
        shard.Recover();
        return shard;
    }

    /// <summary>The index name stored in a folder, for opening existing indexes.</summary>
    public static string? IndexNameIn(string directory)
    {
        var nameFile = Path.Combine(directory, "index.name");
        return File.Exists(nameFile) ? File.ReadAllText(nameFile, Encoding.UTF8) : null;
    }

    private void LoadSegments()
    {
        foreach (var temporary in Directory.EnumerateFiles(_directory, "*.tmp"))
        {
            File.Delete(temporary);
        }

        var segments = new List<SegmentReader>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.seg"))
        {
            try
            {
                segments.Add(SegmentReader.Open(file));
            }
            catch (InvalidDataException ex)
            {
                _logger.LogError(ex, "Segment {File} is damaged and was set aside", file);
                File.Move(file, file + ".damaged", overwrite: true);
            }
        }

        // A merge that finished writing but not deleting what it replaced: those go now
        var replaced = segments.SelectMany(s => s.Replaces).ToHashSet(StringComparer.Ordinal);
        foreach (var segment in segments.Where(s => replaced.Contains(s.Name)).ToList())
        {
            segment.Dispose();
            File.Delete(segment.Path);
            segments.Remove(segment);
        }

        _segments = segments.OrderBy(s => s.MinTicks).ToArray();
    }

    /// <summary>
    /// Replays the logs left by a stop or crash into a segment. Replay skips IDs a segment already
    /// has, so a crash between writing a segment and deleting its log loses and repeats nothing.
    /// </summary>
    private void Recover()
    {
        var logs = Directory.EnumerateFiles(_directory, WalPrefix + "*.log")
            .Select(path => (Path: path, Generation: long.TryParse(Path.GetFileNameWithoutExtension(path)[WalPrefix.Length..], out var g) ? g : 0))
            .OrderBy(l => l.Generation)
            .ToList();
        _walGeneration = logs.Count == 0 ? 0 : logs[^1].Generation + 1;

        var recovered = new List<LogEvent>();
        foreach (var (path, _) in logs)
        {
            recovered.AddRange(ReadLog(path));
        }

        if (recovered.Count > 0)
        {
            var kept = Deduplicate(recovered);
            if (kept.Count > 0)
            {
                WriteSegment(kept);
            }

            _logger.LogInformation("Recovered {Count} events of index {Index} from its log", kept.Count, Index);
        }

        foreach (var (path, _) in logs)
        {
            File.Delete(path);
        }

        OpenWal();
    }

    private List<LogEvent> ReadLog(string path)
    {
        var events = new List<LogEvent>();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(file, Encoding.UTF8);
        while (file.Position + 8 <= file.Length)
        {
            var length = reader.ReadInt32();
            var crc = reader.ReadUInt32();
            if (length <= 0 || file.Position + length > file.Length)
            {
                // A write the crash cut short, which was never acknowledged
                break;
            }

            var payload = reader.ReadBytes(length);
            if (Crc32.Compute(payload) != crc)
            {
                _logger.LogWarning("The log for index {Index} ends in a damaged record, which is dropped", Index);
                break;
            }

            using var record = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
            var count = record.ReadInt32();
            for (var i = 0; i < count; i++)
            {
                events.Add(EventCodec.Read(record));
            }
        }

        return events;
    }

    /// <summary>
    /// Appends one record to the log, all of it or none: never cancelled partway, and on a failed
    /// write, such as a full disk, the log is cut back to where it was. Otherwise a half-written
    /// record would sit in front of later ones, and recovery, which stops at a damaged record,
    /// would lose every batch after it.
    /// </summary>
    private async Task AppendToWalAsync(byte[] record)
    {
        var before = _wal!.Length;
        try
        {
            await _wal.WriteAsync(record, CancellationToken.None);
            _wal.Flush(flushToDisk: _options.SyncWrites);
        }
        catch (IOException)
        {
            var path = _wal.Name;
            try
            {
                _wal.Dispose();
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Closing the log for index {Index} after a failed write failed too", Index);
            }

            using (var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                file.SetLength(before);
            }

            OpenWal();
            throw;
        }
    }

    private void OpenWal()
    {
        var path = Path.Combine(_directory, $"{WalPrefix}{_walGeneration:D12}.log");
        _wal = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 1 << 16);
    }

    /// <summary>
    /// The in-memory events and the segments, taken together, so a flush between reading one and
    /// the other can't show an event twice or not at all.
    /// </summary>
    /// <remarks>
    /// The lock is held only to copy references; nothing slow (a segment write or rewrite) runs
    /// under it, so waiting for it synchronously is brief. Dispose the snapshot when done reading:
    /// it holds the segments open, so one retired meanwhile closes once the last read lets go.
    /// </remarks>
    public ShardSnapshot Snapshot()
    {
        _writeLock.Wait();
        try
        {
            var segments = _segments.Where(s => s.TryAcquire()).ToArray();
            return new ShardSnapshot(_frozen.Count == 0 ? _hot.ToArray() : [.. _frozen, .. _hot], segments);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public long HotCount => Volatile.Read(ref _hotCountField);
    private long _hotCountField;

    public long WalBytes => _wal?.Length ?? 0;

    /// <summary>Stores events, skipping IDs this index already has. Returns how many were new.</summary>
    public async Task<int> AddAsync(IReadOnlyList<LogEvent> events, CancellationToken cancellationToken)
    {
        // Writes outrunning the segment writer wait for it, so memory stays bounded
        if (Volatile.Read(ref _hotCountField) >= 2L * _options.FlushEvents)
        {
            await _flushing.WaitAsync(cancellationToken);
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var kept = Deduplicate(events);
            if (kept.Count == 0)
            {
                return 0;
            }

            foreach (var logEvent in kept)
            {
                // An event stored with no index is in this one, in memory as in a segment
                logEvent.Index = Index;
            }

            // One record for the batch: it's in the log whole, or not at all
            using var payload = new MemoryStream();
            using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(kept.Count);
                foreach (var logEvent in kept)
                {
                    EventCodec.Write(writer, logEvent);
                }
            }

            var length = (int)payload.Length;
            var record = new byte[8 + length];
            payload.GetBuffer().AsSpan(0, length).CopyTo(record.AsSpan(8));
            BitConverter.TryWriteBytes(record.AsSpan(0, 4), length);
            BitConverter.TryWriteBytes(record.AsSpan(4, 4), Crc32.Compute(record.AsSpan(8)));
            await AppendToWalAsync(record);

            foreach (var logEvent in kept)
            {
                _hot.Add(logEvent);
                _hotIds.Add(logEvent.Id);
            }

            if (_oldestHot == DateTime.MaxValue)
            {
                _oldestHot = DateTime.UtcNow;
            }

            Volatile.Write(ref _hotCountField, _hot.Count);
            if (_hot.Count >= _options.FlushEvents)
            {
                StartFlushLocked();
            }

            return kept.Count;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>The events whose IDs aren't in this batch already, in memory, or in a segment.</summary>
    private List<LogEvent> Deduplicate(IReadOnlyList<LogEvent> events)
    {
        var kept = new List<LogEvent>(events.Count);
        var batch = new HashSet<string>(StringComparer.Ordinal);
        var segments = _segments;
        var decoded = new Dictionary<BlockInfo, HashSet<string>>(ReferenceEqualityComparer.Instance);
        foreach (var logEvent in events)
        {
            if (!batch.Add(logEvent.Id) || _hotIds.Contains(logEvent.Id) || _frozenIds.Contains(logEvent.Id) || InSegments(segments, logEvent.Id, decoded))
            {
                continue;
            }

            kept.Add(logEvent);
        }

        return kept;
    }

    /// <summary>
    /// Whether a segment holds this ID. A re-sent event can carry a different time (a collector
    /// stamps it on arrival), so every segment is checked: its ID filter rules most out in one
    /// probe, then each block's, and only then are a block's IDs read, once per batch.
    /// </summary>
    private static bool InSegments(SegmentReader[] segments, string id, Dictionary<BlockInfo, HashSet<string>> decoded)
    {
        foreach (var segment in segments)
        {
            if (!segment.Ids.MayContain(id))
            {
                continue;
            }

            foreach (var block in segment.Blocks)
            {
                if (!block.Ids.MayContain(id))
                {
                    continue;
                }

                if (!decoded.TryGetValue(block, out var ids))
                {
                    ids = new HashSet<string>(new BlockRows(segment, block).Ids, StringComparer.Ordinal);
                    decoded[block] = ids;
                }

                if (ids.Contains(id))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Starts writing in-memory events older than the flush interval to a segment. With
    /// <paramref name="force"/>, writes all that are in memory when it starts, and waits until
    /// they're in a segment; events written meanwhile may still be in memory when it returns.
    /// </summary>
    public async Task FlushAsync(bool force, CancellationToken cancellationToken)
    {
        if (!force && (Volatile.Read(ref _hotCountField) == 0 || DateTime.UtcNow - _oldestHot < TimeSpan.FromSeconds(_options.FlushSeconds)))
        {
            return;
        }

        if (!force)
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                if (!_disposed)
                {
                    StartFlushLocked();
                }
            }
            finally
            {
                _writeLock.Release();
            }

            return;
        }

        // Everything in memory now, once. Waiting until memory is empty would never end under
        // steady writes, and a purge waiting on this holds off merges all that time.
        await LockWithoutFlushAsync(cancellationToken);
        Task<bool> flushing;
        try
        {
            if (_disposed || _hot.Count == 0)
            {
                return;
            }

            StartFlushLocked();
            flushing = _flushing;
        }
        finally
        {
            _writeLock.Release();
        }

        if (!await flushing.WaitAsync(cancellationToken))
        {
            throw new IOException($"Writing index {Index} to a segment failed; its events are still in the log");
        }
    }

    /// <summary>
    /// Freezes the in-memory events and starts a new log for what follows, then writes the frozen
    /// events to a segment in the background. Does nothing while a flush is already running; the
    /// next write or timer tick tries again.
    /// </summary>
    private void StartFlushLocked()
    {
        if (_hot.Count == 0 || !_flushing.IsCompleted)
        {
            return;
        }

        _unflushedLogs.Add(_wal!.Name);
        _wal.Dispose();
        _walGeneration++;
        OpenWal();

        _frozen = _hot;
        _frozenIds = _hotIds;
        _hot = [];
        _hotIds = new HashSet<string>(StringComparer.Ordinal);
        _oldestHot = DateTime.MaxValue;
        Volatile.Write(ref _hotCountField, 0);

        var frozen = _frozen;
        var logs = _unflushedLogs.ToList();
        _flushing = Task.Run(() => WriteFrozenAsync(frozen, logs));
    }

    /// <summary>Writes frozen events to a segment. Returns false if that failed and they went back in memory.</summary>
    private async Task<bool> WriteFrozenAsync(List<LogEvent> frozen, List<string> logs)
    {
        SegmentReader? written = null;
        try
        {
            var first = frozen.Min(e => e.Timestamp.Ticks);
            var path = Path.Combine(_directory, $"seg-{first:D19}-{Guid.NewGuid():N}.seg");
            SegmentWriter.Write(path, Index, frozen);
            written = SegmentReader.Open(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.LogError(ex, "Writing index {Index} to a segment failed; its events stay in memory and in the log, to try again", Index);
        }

        await _writeLock.WaitAsync();
        try
        {
            if (written == null || _disposed)
            {
                if (written != null)
                {
                    written.Dispose();
                    written = null;
                }

                // Back in with the newest, so the next flush takes them. They're still in their logs.
                _hot.InsertRange(0, frozen);
                _hotIds.UnionWith(frozen.Select(e => e.Id));
                Volatile.Write(ref _hotCountField, _hot.Count);
                if (_oldestHot == DateTime.MaxValue)
                {
                    _oldestHot = DateTime.UtcNow;
                }
            }
            else
            {
                AddSegment(written);

                // The events are in a segment, so the logs that held them can go
                foreach (var log in logs)
                {
                    File.Delete(log);
                    _unflushedLogs.Remove(log);
                }
            }

            _frozen = [];
            _frozenIds = new HashSet<string>(StringComparer.Ordinal);
            return written != null;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Takes the write lock with no flush running, for work that replaces segments.</summary>
    private async Task LockWithoutFlushAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _flushing.WaitAsync(cancellationToken);
            await _writeLock.WaitAsync(cancellationToken);
            if (_flushing.IsCompleted)
            {
                return;
            }

            _writeLock.Release();
        }
    }

    private SegmentReader WriteSegment(IReadOnlyList<LogEvent> events, IReadOnlyList<string>? replaces = null)
    {
        var first = events.Min(e => e.Timestamp.Ticks);
        var path = Path.Combine(_directory, $"seg-{first:D19}-{Guid.NewGuid():N}.seg");
        SegmentWriter.Write(path, Index, events, replaces);
        var reader = SegmentReader.Open(path);
        AddSegment(reader);
        return reader;
    }

    /// <summary>
    /// Deletes events older than <paramref name="before"/>. Whole segments go at once; one that
    /// straddles the time is rewritten without them.
    /// </summary>
    public async Task<int> PurgeAsync(DateTime before, CancellationToken cancellationToken)
    {
        await _maintenanceLock.WaitAsync(cancellationToken);
        try
        {
            // What's in memory as the purge starts goes to segments, so the purge can see it
            await FlushAsync(force: true, cancellationToken);

            // Segments are only replaced under the maintenance lock, so these stay as they are
            // while straddling ones are rewritten, outside the write lock: reads and writes carry
            // on, and only swapping the results in waits for them, as with a merge.
            var deleted = 0;
            var retiring = new List<SegmentReader>();
            var rewritten = new List<SegmentReader>();
            try
            {
                foreach (var segment in _segments.Where(s => s.MinTicks < before.Ticks).ToList())
                {
                    if (segment.MaxTicks < before.Ticks)
                    {
                        deleted += segment.Rows;
                        retiring.Add(segment);
                        continue;
                    }

                    var kept = segment.Blocks.Where(b => b.MaxTicks >= before.Ticks)
                        .Sum(b => new BlockRows(segment, b).Ticks.Count(t => t >= before.Ticks));
                    deleted += segment.Rows - kept;
                    if (kept > 0)
                    {
                        rewritten.Add(await Task.Run(
                            () =>
                            {
                                var rows = RowSource.Merge([RowSource.FromSegment(segment, new RowFilter(new QueryContext { StartTime = before }), descending: false)], descending: false, cancellationToken);
                                return WriteSorted(rows.Select(r => r.Materialize()), kept, [segment.Name], cancellationToken);
                            },
                            cancellationToken));
                    }

                    retiring.Add(segment);
                }

                await _writeLock.WaitAsync(cancellationToken);
            }
            catch
            {
                DiscardRewritten(rewritten);
                throw;
            }

            try
            {
                if (_disposed)
                {
                    DiscardRewritten(rewritten);
                    return 0;
                }

                foreach (var segment in rewritten)
                {
                    AddSegment(segment);
                }

                foreach (var segment in retiring)
                {
                    Retire(segment);
                }

                return deleted;
            }
            finally
            {
                _writeLock.Release();
            }
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }

    /// <summary>
    /// Merges one set of segments, if any are due: once <see cref="MergeWidth"/> segments are of
    /// the same size (by powers of ten), the oldest of them become one. Each event is rewritten
    /// about once per size, and an index settles at a few dozen segments plus full-sized ones.
    /// Writes carry on meanwhile; only swapping the new segment in waits for them.
    /// </summary>
    /// <returns>Whether it merged anything, so the caller can see if more are due.</returns>
    public async Task<bool> CompactAsync(CancellationToken cancellationToken)
    {
        await _maintenanceLock.WaitAsync(cancellationToken);
        try
        {
            if (_disposed || PickMerge(_segments) is not { } chosen)
            {
                return false;
            }

            var sources = chosen.Select(segment => RowSource.FromSegment(segment, new RowFilter(new QueryContext()), descending: false)).ToList();
            var merged = await Task.Run(
                () => WriteSorted(RowSource.Merge(sources, descending: false, cancellationToken).Select(r => r.Materialize()), chosen.Sum(s => s.Rows), chosen.Select(s => s.Name).ToList(), cancellationToken),
                cancellationToken);

            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                if (_disposed)
                {
                    // The originals are still there, so the merged copy just goes
                    merged.Dispose();
                    File.Delete(merged.Path);
                    return false;
                }

                AddSegment(merged);
                foreach (var segment in chosen)
                {
                    Retire(segment);
                }
            }
            finally
            {
                _writeLock.Release();
            }

            _logger.LogDebug("Merged {Count} segments of index {Index} into one of {Rows} events", chosen.Count, Index, merged.Rows);
            return true;
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }

    /// <summary>The segments to merge next, oldest first, or null if none are due.</summary>
    private List<SegmentReader>? PickMerge(SegmentReader[] segments)
    {
        foreach (var tier in segments.Where(s => s.Rows < _options.MaxSegmentEvents)
                     .GroupBy(s => (int)Math.Log10(Math.Max(1, s.Rows)))
                     .OrderBy(t => t.Key))
        {
            if (tier.Count() < MergeWidth)
            {
                continue;
            }

            var chosen = new List<SegmentReader>();
            long rows = 0;
            foreach (var segment in tier.OrderBy(s => s.MinTicks).Take(MergeWidth))
            {
                if (chosen.Count > 0 && rows + segment.Rows > _options.MaxSegmentEvents)
                {
                    break;
                }

                chosen.Add(segment);
                rows += segment.Rows;
            }

            if (chosen.Count > 1)
            {
                return chosen;
            }
        }

        return null;
    }

    /// <summary>Writes sorted events to a new segment file and opens it, without adding it to reads.</summary>
    private SegmentReader WriteSorted(IEnumerable<LogEvent> sorted, int atMost, IReadOnlyList<string> replaces, CancellationToken cancellationToken)
    {
        var first = sorted.First().Timestamp.Ticks;
        var path = Path.Combine(_directory, $"seg-{first:D19}-{Guid.NewGuid():N}.seg");
        SegmentWriter.WriteSorted(path, Index, sorted, atMost, replaces, cancellationToken);
        return SegmentReader.Open(path);
    }

    private void AddSegment(SegmentReader reader) =>
        _segments = _segments.Append(reader).OrderBy(s => s.MinTicks).ToArray();

    /// <summary>
    /// Removes a segment from reads and deletes its file. Reads that have it in a snapshot keep
    /// reading it; it closes when the last of them is done.
    /// </summary>
    private void Retire(SegmentReader segment)
    {
        _segments = _segments.Where(s => s != segment).ToArray();
        File.Delete(segment.Path);
        segment.Release();
    }

    /// <summary>Closes and deletes segments written for a replacement that won't happen.</summary>
    private void DiscardRewritten(List<SegmentReader> rewritten)
    {
        foreach (var segment in rewritten)
        {
            segment.Dispose();
            try
            {
                File.Delete(segment.Path);
            }
            catch (IOException ex)
            {
                // Left behind, it replaces what it was rewritten from at the next start: the purge, applied then
                _logger.LogWarning(ex, "Deleting {File} after a purge stopped failed", segment.Path);
            }
        }

        rewritten.Clear();
    }

    /// <summary>Deletes the whole index. Returns how many events it held.</summary>
    public async Task<int> DeleteAllAsync(CancellationToken cancellationToken)
    {
        await _maintenanceLock.WaitAsync(cancellationToken);
        try
        {
            await LockWithoutFlushAsync(cancellationToken);
            try
            {
                var count = _segments.Sum(s => s.Rows) + _hot.Count;
                CloseLocked();
                Directory.Delete(_directory, recursive: true);
                return count;
            }
            finally
            {
                _writeLock.Release();
            }
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }

    private void CloseLocked()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _wal?.Dispose();
        foreach (var segment in _segments)
        {
            segment.Release();
        }

        _segments = [];
    }

    /// <summary>Closes the index. Cancel any merge first, or this waits for it to finish.</summary>
    public void Dispose()
    {
        _maintenanceLock.Wait();
        _maintenanceLock.Release();
        LockWithoutFlushAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            // What's in memory is in the log, so stopping needn't write a segment
            CloseLocked();
        }
        finally
        {
            _writeLock.Release();
        }
    }
}

/// <summary>
/// An index's in-memory events and segments at one moment. Holds the segments open until disposed,
/// so a merge or purge that retires one meanwhile can't close it under the read.
/// </summary>
internal sealed class ShardSnapshot(LogEvent[] hot, SegmentReader[] segments) : IDisposable
{
    private int _disposed;

    public LogEvent[] Hot { get; } = hot;

    public SegmentReader[] Segments { get; } = segments;

    public void Deconstruct(out LogEvent[] hot, out SegmentReader[] segments)
    {
        hot = Hot;
        segments = Segments;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            foreach (var segment in Segments)
            {
                segment.Release();
            }
        }
    }
}
