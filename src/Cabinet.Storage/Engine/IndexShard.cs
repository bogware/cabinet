using System.Text;
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
}

/// <summary>
/// One index: a write-ahead log and in-memory events, and the immutable segments they're written
/// to. Writes and flushes take turns; reads take a snapshot and never wait.
/// </summary>
internal sealed class IndexShard : IDisposable
{
    private const string WalPrefix = "wal-";
    private readonly string _directory;
    private readonly EngineOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly List<(SegmentReader Segment, DateTime RetiredAt)> _retired = [];

    private List<LogEvent> _hot = [];
    private HashSet<string> _hotIds = new(StringComparer.Ordinal);
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

    private void OpenWal()
    {
        var path = Path.Combine(_directory, $"{WalPrefix}{_walGeneration:D12}.log");
        _wal = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 1 << 16);
    }

    /// <summary>
    /// The in-memory events and the segments, taken together, so a flush between reading one and
    /// the other can't show an event twice or not at all.
    /// </summary>
    public (LogEvent[] Hot, SegmentReader[] Segments) Snapshot()
    {
        _writeLock.Wait();
        try
        {
            return (_hot.ToArray(), _segments);
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
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var kept = Deduplicate(events);
            if (kept.Count == 0)
            {
                return 0;
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

            var bytes = payload.GetBuffer().AsSpan(0, (int)payload.Length);
            var header = new byte[8];
            BitConverter.TryWriteBytes(header.AsSpan(0, 4), bytes.Length);
            BitConverter.TryWriteBytes(header.AsSpan(4, 4), Crc32.Compute(bytes));
            await _wal!.WriteAsync(header, cancellationToken);
            await _wal.WriteAsync(payload.GetBuffer().AsMemory(0, (int)payload.Length), cancellationToken);
            _wal.Flush(flushToDisk: _options.SyncWrites);

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
                FlushLocked();
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
            if (!batch.Add(logEvent.Id) || _hotIds.Contains(logEvent.Id) || InSegments(segments, logEvent.Id, decoded))
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

    /// <summary>Writes in-memory events older than the flush interval, or all with <paramref name="force"/>.</summary>
    public async Task FlushAsync(bool force, CancellationToken cancellationToken)
    {
        if (!force && (Volatile.Read(ref _hotCountField) == 0 || DateTime.UtcNow - _oldestHot < TimeSpan.FromSeconds(_options.FlushSeconds)))
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (!_disposed)
            {
                FlushLocked();
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void FlushLocked()
    {
        DisposeRetired(TimeSpan.FromMinutes(10));
        if (_hot.Count == 0)
        {
            return;
        }

        WriteSegment(_hot);

        // The events are in a segment, so the log that held them can go
        var oldWal = _wal!.Name;
        _wal.Dispose();
        File.Delete(oldWal);
        _walGeneration++;
        OpenWal();

        _hot = [];
        _hotIds = new HashSet<string>(StringComparer.Ordinal);
        _oldestHot = DateTime.MaxValue;
        Volatile.Write(ref _hotCountField, 0);
    }

    private SegmentReader WriteSegment(IReadOnlyList<LogEvent> events, IReadOnlyList<string>? replaces = null)
    {
        var first = events.Min(e => e.Timestamp.Ticks);
        var path = Path.Combine(_directory, $"seg-{first:D19}-{Guid.NewGuid():N}.seg");
        SegmentWriter.Write(path, Index, events, replaces);
        var reader = SegmentReader.Open(path);
        _segments = _segments.Append(reader).OrderBy(s => s.MinTicks).ToArray();
        return reader;
    }

    /// <summary>
    /// Deletes events older than <paramref name="before"/>. Whole segments go at once; one that
    /// straddles the time is rewritten without them.
    /// </summary>
    public async Task<int> PurgeAsync(DateTime before, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            FlushLocked();
            var deleted = 0;
            foreach (var segment in _segments.Where(s => s.MinTicks < before.Ticks).ToList())
            {
                if (segment.MaxTicks < before.Ticks)
                {
                    deleted += segment.Rows;
                    Retire(segment);
                    continue;
                }

                var kept = new List<LogEvent>();
                foreach (var block in segment.Blocks)
                {
                    var rows = new BlockRows(segment, block);
                    for (var i = 0; i < rows.Count; i++)
                    {
                        if (rows.Ticks[i] >= before.Ticks)
                        {
                            kept.Add(rows.Materialize(i));
                        }
                    }
                }

                deleted += segment.Rows - kept.Count;
                if (kept.Count > 0)
                {
                    WriteSegment(kept, [segment.Name]);
                }

                Retire(segment);
            }

            return deleted;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Removes a segment from reads and deletes its file; queries that have it open finish first.</summary>
    private void Retire(SegmentReader segment)
    {
        _segments = _segments.Where(s => s != segment).ToArray();
        _retired.Add((segment, DateTime.UtcNow));
        File.Delete(segment.Path);
    }

    private void DisposeRetired(TimeSpan after)
    {
        foreach (var retired in _retired.Where(r => DateTime.UtcNow - r.RetiredAt > after).ToList())
        {
            retired.Segment.Dispose();
            _retired.Remove(retired);
        }
    }

    /// <summary>Deletes the whole index. Returns how many events it held.</summary>
    public async Task<int> DeleteAllAsync(CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
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
            segment.Dispose();
        }

        DisposeRetired(TimeSpan.Zero);
        _segments = [];
    }

    public void Dispose()
    {
        _writeLock.Wait();
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
