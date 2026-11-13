using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Storage;

namespace Cabinet.API.Services.Archive;

/// <summary>A search of the archive that would hold more events than it may.</summary>
public sealed class ArchiveTooLargeException(int maxEvents)
    : Exception(string.Create(CultureInfo.InvariantCulture, $"More than {maxEvents:N0} archived events are in that range. Search a shorter time, or rehydrate it into an index."))
{
}

/// <summary>One archived file: an index's events from part of one day.</summary>
public sealed record ArchiveChunk(string Key, string Index, DateTime Day, long SizeBytes);

/// <summary>
/// Cold storage for events retention removes: gzipped JSON lines, one file per index, day and run,
/// at <c>{index}/{yyyy-MM-dd}/{run}.ndjson.gz</c>.
/// </summary>
public interface IEventArchive
{
    /// <summary>Stores a file under a key.</summary>
    Task PutAsync(string key, string localFile, CancellationToken cancellationToken = default);

    /// <summary>Every chunk, or one index's.</summary>
    Task<List<ArchiveChunk>> ListAsync(string? index = null, CancellationToken cancellationToken = default);

    /// <summary>Opens a chunk for reading.</summary>
    Task<Stream> OpenAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>An archive in a directory: a local disk, a NAS share or a mounted bucket.</summary>
public sealed class DirectoryEventArchive : IEventArchive
{
    private readonly string _root;

    public DirectoryEventArchive(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public Task PutAsync(string key, string localFile, CancellationToken cancellationToken = default)
    {
        var target = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(localFile, target, overwrite: true);
        return Task.CompletedTask;
    }

    public Task<List<ArchiveChunk>> ListAsync(string? index = null, CancellationToken cancellationToken = default)
    {
        var start = index == null ? _root : PathFor(EventArchiver.SafeIndex(index));
        if (!Directory.Exists(start))
        {
            return Task.FromResult(new List<ArchiveChunk>());
        }

        var chunks = Directory.EnumerateFiles(start, "*.ndjson.gz", SearchOption.AllDirectories)
            .Select(file => EventArchiver.Parse(Path.GetRelativePath(_root, file).Replace('\\', '/'), new FileInfo(file).Length))
            .OfType<ArchiveChunk>()
            .ToList();
        return Task.FromResult(chunks);
    }

    public Task<Stream> OpenAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(File.OpenRead(PathFor(key)));

    private string PathFor(string key)
    {
        var full = Path.GetFullPath(Path.Combine(_root, key));
        if (!full.StartsWith(_root, StringComparison.Ordinal))
        {
            throw new ArgumentException("An archive key can't leave the archive directory", nameof(key));
        }

        return full;
    }
}

/// <summary>An archive in an S3 bucket.</summary>
public sealed class S3EventArchive : IEventArchive
{
    private readonly Cabinet.AWS.Services.S3ArchiveService _s3;
    private readonly string _prefix;

    public S3EventArchive(Cabinet.AWS.Services.S3ArchiveService s3, string prefix)
    {
        _s3 = s3;
        _prefix = prefix.TrimEnd('/');
    }

    public async Task PutAsync(string key, string localFile, CancellationToken cancellationToken = default)
    {
        // The service names the object after the file, so give the file the key's name
        var folder = Path.GetDirectoryName(key)!.Replace('\\', '/');
        var named = Path.Combine(Path.GetDirectoryName(localFile)!, Path.GetFileName(key));
        File.Move(localFile, named, overwrite: true);
        try
        {
            var result = await _s3.UploadAsync(named, keyPrefix: $"{_prefix}/{folder}", cancellationToken: cancellationToken);
            if (!result.Success)
            {
                throw new IOException($"Uploading {key} to S3 failed: {result.Error}");
            }
        }
        finally
        {
            File.Move(named, localFile, overwrite: true);
        }
    }

    public async Task<List<ArchiveChunk>> ListAsync(string? index = null, CancellationToken cancellationToken = default)
    {
        var prefix = index == null ? $"{_prefix}/" : $"{_prefix}/{EventArchiver.SafeIndex(index)}/";
        return (await _s3.ListArchivesAsync(prefix: prefix, cancellationToken: cancellationToken))
            .Select(info => EventArchiver.Parse(info.Key[(_prefix.Length + 1)..], info.SizeBytes))
            .OfType<ArchiveChunk>()
            .ToList();
    }

    public async Task<Stream> OpenAsync(string key, CancellationToken cancellationToken = default)
    {
        var folder = Directory.CreateTempSubdirectory("cabinet-rehydrate-").FullName;
        var file = await _s3.DownloadAsync($"{_prefix}/{key}", folder, cancellationToken: cancellationToken);
        return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.DeleteOnClose);
    }
}

/// <summary>Moves events into the archive before retention removes them, and brings them back.</summary>
public sealed class EventArchiver
{
    private const int PageSize = 5000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IEventArchive _archive;
    private readonly ILogger<EventArchiver> _logger;

    public EventArchiver(IEventArchive archive, ILogger<EventArchiver> logger)
    {
        _archive = archive;
        _logger = logger;
    }

    /// <summary>An index name as a path segment.</summary>
    public static string SafeIndex(string index) =>
        new(index.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());

    /// <summary>A key's index and day, or null for a file that isn't a chunk.</summary>
    public static ArchiveChunk? Parse(string key, long size)
    {
        var parts = key.Split('/');
        return parts.Length == 3 && parts[2].EndsWith(".ndjson.gz", StringComparison.Ordinal)
            && DateTime.TryParseExact(parts[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day)
            ? new ArchiveChunk(key, parts[0], day, size)
            : null;
    }

    /// <summary>
    /// Archives an index's events older than <paramref name="before"/>, a day at a time. Returns
    /// how many it archived; a failure throws, so the caller doesn't delete what wasn't saved.
    /// </summary>
    public async Task<long> ArchiveAsync(ILogStorage storage, string index, DateTime before, CancellationToken cancellationToken)
    {
        var oldest = await storage.QueryAsync(new QueryContext { Index = index, EndTime = before.AddTicks(-1), SortDescending = false, Limit = 1 }, cancellationToken)
            .FirstOrDefaultAsync(cancellationToken);
        if (oldest == null)
        {
            return 0;
        }

        long total = 0;
        var run = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
        for (var day = oldest.Timestamp.Date; day < before; day = day.AddDays(1))
        {
            var end = day.AddDays(1) < before ? day.AddDays(1) : before;
            var file = Path.GetTempFileName();
            try
            {
                var written = await WriteDayAsync(storage, index, day, end, file, cancellationToken);
                if (written > 0)
                {
                    await _archive.PutAsync($"{SafeIndex(index)}/{day:yyyy-MM-dd}/{run}.ndjson.gz", file, cancellationToken);
                    total += written;
                }
            }
            finally
            {
                File.Delete(file);
            }
        }

        _logger.LogInformation("Archived {Count} events from index {Index} older than {Before}", total, index, before);
        return total;
    }

    private static async Task<long> WriteDayAsync(ILogStorage storage, string index, DateTime start, DateTime end, string file, CancellationToken cancellationToken)
    {
        long written = 0;
        await using var output = new GZipStream(File.Create(file), CompressionLevel.Optimal);
        await using var writer = new StreamWriter(output);
        var page = new QueryContext { Index = index, StartTime = start, EndTime = end.AddTicks(-1), Limit = PageSize };
        while (true)
        {
            var read = 0;
            LogEvent? last = null;
            await foreach (var logEvent in storage.QueryAsync(page, cancellationToken))
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(logEvent, Json));
                read++;
                last = logEvent;
            }

            written += read;
            if (read < PageSize || last == null)
            {
                return written;
            }

            page = page.Clone();
            page.After = new EventCursor(last.Timestamp, last.Id);
        }
    }

    /// <summary>Restores an index's archived events between two times into <paramref name="target"/>.</summary>
    public async Task<long> RehydrateAsync(ILogStorage storage, string index, DateTime from, DateTime to, string target, CancellationToken cancellationToken)
    {
        long restored = 0;
        var batch = new List<LogEvent>(1000);
        foreach (var chunk in (await _archive.ListAsync(index, cancellationToken)).Where(c => c.Day <= to && c.Day.AddDays(1) > from).OrderBy(c => c.Day))
        {
            await foreach (var logEvent in ReadAsync(chunk.Key, cancellationToken))
            {
                if (logEvent.Timestamp < from || logEvent.Timestamp > to)
                {
                    continue;
                }

                logEvent.Index = target;
                batch.Add(logEvent);
                if (batch.Count == 1000)
                {
                    restored += await storage.StoreBatchAsync(batch, cancellationToken);
                    batch.Clear();
                }
            }
        }

        if (batch.Count > 0)
        {
            restored += await storage.StoreBatchAsync(batch, cancellationToken);
        }

        _logger.LogInformation("Rehydrated {Count} events of {Index} into {Target}", restored, index, target);
        return restored;
    }

    /// <summary>
    /// An index's archived events between two times, in memory to search where they are, reading
    /// only the days the range covers. More than <paramref name="maxEvents"/> throws, so a search
    /// can't fill the server's memory; a narrower range gets under it.
    /// </summary>
    public async Task<InMemoryStorage> LoadAsync(string index, DateTime from, DateTime to, int maxEvents, CancellationToken cancellationToken)
    {
        var storage = new InMemoryStorage(maxEvents: maxEvents + 1);
        var batch = new List<LogEvent>(1000);
        long loaded = 0;
        foreach (var chunk in (await _archive.ListAsync(index, cancellationToken)).Where(c => c.Day <= to && c.Day.AddDays(1) > from).OrderBy(c => c.Day))
        {
            await foreach (var logEvent in ReadAsync(chunk.Key, cancellationToken))
            {
                if (logEvent.Timestamp < from || logEvent.Timestamp > to)
                {
                    continue;
                }

                if (++loaded > maxEvents)
                {
                    throw new ArchiveTooLargeException(maxEvents);
                }

                batch.Add(logEvent);
                if (batch.Count == 1000)
                {
                    await storage.StoreBatchAsync(batch, cancellationToken);
                    batch.Clear();
                }
            }
        }

        await storage.StoreBatchAsync(batch, cancellationToken);
        return storage;
    }

    private async IAsyncEnumerable<LogEvent> ReadAsync(string key, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await _archive.OpenAsync(key, cancellationToken);
        await using var input = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(input);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0 || JsonSerializer.Deserialize<LogEvent>(line, Json) is not { } logEvent)
            {
                continue;
            }

            // Fields come back as JSON, in a dictionary that has lost its case-insensitivity
            logEvent.Fields = new System.Collections.Concurrent.ConcurrentDictionary<string, object?>(
                logEvent.Fields.Select(field => KeyValuePair.Create(field.Key, field.Value is JsonElement element ? JsonFieldValue.Unwrap(element) : field.Value)),
                StringComparer.OrdinalIgnoreCase);

            yield return logEvent;
        }
    }
}
