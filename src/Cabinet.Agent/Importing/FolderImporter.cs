using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cabinet.Agent.Configuration;
using Cabinet.Agent.Forwarding;
using Cabinet.Agent.Monitoring;

namespace Cabinet.Agent.Importing;

/// <summary>
/// What to import: files matching a path or glob, sent as one source type to one index.
/// </summary>
public sealed class ImportSource
{
    /// <summary>A folder, file or glob. A folder means every file in it.</summary>
    public required string Path { get; init; }

    /// <summary>Source type the server parses the lines as, such as L4D2.</summary>
    public string SourceType { get; init; } = "generic";

    /// <summary>Index the events go to.</summary>
    public string Index { get; init; } = "main";

    /// <summary>Whether a folder includes its subfolders.</summary>
    public bool Recursive { get; init; } = true;

    /// <summary>File names to leave out, such as <c>*.gz</c>.</summary>
    public IReadOnlyList<string> ExcludePatterns { get; init; } = [];
}

/// <summary>
/// Settings for one import.
/// </summary>
public sealed class ImportOptions
{
    /// <summary>What to read.</summary>
    public required IReadOnlyList<ImportSource> Sources { get; init; }

    /// <summary>Host name the events carry.</summary>
    public required string Hostname { get; init; }

    /// <summary>IANA time zone the files' times are in.</summary>
    public required string TimeZone { get; init; }

    /// <summary>Files to leave out, because the running agent reads them.</summary>
    public IReadOnlySet<string> SkipPaths { get; init; } = new HashSet<string>();

    /// <summary>Most events sent per second, to spare the machine and the server. 0 is no limit.</summary>
    public int MaxEventsPerSecond { get; init; }

    /// <summary>Longest line kept, in bytes.</summary>
    public int MaxLineBytes { get; init; } = 1024 * 1024;
}

/// <summary>
/// Where an import has got to.
/// </summary>
public sealed record ImportProgress(int FilesDone, int FilesTotal, long EventsRead, long BytesRead, long BytesTotal, string? CurrentFile);

/// <summary>
/// How an import went.
/// </summary>
public sealed record ImportResult(int FilesImported, int FilesSkipped, long EventsRead, long EventsSent, long EventsNotDelivered, TimeSpan Took);

/// <summary>
/// Reads whole files once and sends every line, for history and for folders of logs from
/// anywhere. Unlike tailing, it keeps no checkpoints: each event's ID comes from the host, the
/// file's name and first line, and the line's position, so importing the same files again stores
/// nothing twice. Files are read one at a time, so memory stays small whatever the folder holds.
/// </summary>
public sealed class FolderImporter
{
    private const int IdentityBytes = 1024;

    private readonly LogForwarder _forwarder;
    private readonly ILogger _logger;

    public FolderImporter(LogForwarder forwarder, ILogger logger)
    {
        _forwarder = forwarder;
        _logger = logger;
    }

    /// <summary>
    /// The files an import would read, oldest first, with those it leaves out counted.
    /// </summary>
    public static (List<(string Path, ImportSource Source, long Length)> Files, int Skipped) Plan(ImportOptions options)
    {
        var files = new List<(string Path, ImportSource Source, long Length, DateTime Written)>();
        var seen = new HashSet<string>(FileStateRegistry.PathComparer);
        var skipped = 0;

        foreach (var source in options.Sources)
        {
            foreach (var path in Expand(source))
            {
                if (!seen.Add(path) || FileMonitorService.IsExcluded(path, source.ExcludePatterns))
                {
                    continue;
                }

                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    continue;
                }

                if (options.SkipPaths.Contains(path))
                {
                    skipped++;
                    continue;
                }

                files.Add((path, source, info.Length, info.LastWriteTimeUtc));
            }
        }

        return ([.. files.OrderBy(f => f.Written).ThenBy(f => f.Path, StringComparer.Ordinal).Select(f => (f.Path, f.Source, f.Length))], skipped);
    }

    /// <summary>
    /// Reads every planned file and sends its lines, then waits for delivery.
    /// </summary>
    public async Task<ImportResult> ImportAsync(
        ImportOptions options,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        var (files, skipped) = Plan(options);
        var bytesTotal = files.Sum(f => f.Length);
        var before = _forwarder.GetStats();

        long events = 0;
        long bytes = 0;
        var done = 0;
        var throttle = new Throttle(options.MaxEventsPerSecond);

        foreach (var (path, source, _) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ImportProgress(done, files.Count, events, bytes, bytesTotal, path));

            try
            {
                var (read, length) = await ImportFileAsync(path, source, options, throttle, cancellationToken);
                events += read;
                bytes += length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Couldn't read {Path}, left out: {Message}", path, ex.Message);
                skipped++;
            }

            done++;
        }

        progress?.Report(new ImportProgress(done, files.Count, events, bytes, bytesTotal, null));
        await _forwarder.FlushAsync();

        var after = _forwarder.GetStats();
        var sent = after.EventsSent - before.EventsSent;
        var notDelivered = (after.EventsSpooled - before.EventsSpooled)
            + (after.EventsDeadLettered - before.EventsDeadLettered)
            + (after.EventsDropped - before.EventsDropped);

        return new ImportResult(done, skipped, events, sent, notDelivered, started.Elapsed);
    }

    /// <summary>
    /// Identifies a file for event IDs by host, name and first line, which don't change as it
    /// grows or moves to another folder. A file with no complete first line uses what it has.
    /// </summary>
    public static string FileKey(string hostname, string path, ReadOnlySpan<byte> start)
    {
        var newline = start.IndexOf((byte)'\n');
        var first = newline >= 0 ? start[..(newline + 1)] : start;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"import\n{hostname}\n{System.IO.Path.GetFileName(path)}\n"));
        hash.AppendData(first);
        return "import-" + Convert.ToHexStringLower(hash.GetHashAndReset().AsSpan(0, 16));
    }

    private async Task<(long Events, long Bytes)> ImportFileAsync(
        string path,
        ImportSource source,
        ImportOptions options,
        Throttle throttle,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        var start = new byte[IdentityBytes];
        var count = await stream.ReadAtLeastAsync(start, IdentityBytes, throwOnEndOfStream: false, cancellationToken);
        var fileKey = FileKey(options.Hostname, path, start.AsSpan(0, count));

        var reader = new LineReader(stream, 0, options.MaxLineBytes);
        long events = 0;
        long lineNumber = 0;

        await foreach (var line in reader.ReadLinesAsync(cancellationToken))
        {
            lineNumber++;
            if (await SendAsync(line.Text, line.StartOffset, line.EndOffset, lineNumber, fileKey, path, source, options, throttle, cancellationToken))
            {
                events++;
            }
        }

        // A final line with no newline: the file is history, so nothing will finish it
        var length = stream.Length;
        if (reader.Position < length)
        {
            var rest = LineReader.ReadRange(stream, reader.Position, length, options.MaxLineBytes);
            if (await SendAsync(rest, reader.Position, length, lineNumber + 1, fileKey, path, source, options, throttle, cancellationToken))
            {
                events++;
            }
        }

        return (events, length);
    }

    private async Task<bool> SendAsync(
        string text,
        long startOffset,
        long endOffset,
        long lineNumber,
        string fileKey,
        string path,
        ImportSource source,
        ImportOptions options,
        Throttle throttle,
        CancellationToken cancellationToken)
    {
        text = text.TrimEnd('\r');
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        await throttle.WaitAsync(cancellationToken);
        await _forwarder.EnqueueAsync(new LogEventDto
        {
            Id = FileMonitorService.CreateEventId(fileKey, startOffset),
            StartOffset = startOffset,
            EndOffset = endOffset,
            Timestamp = DateTime.UtcNow,
            Host = options.Hostname,
            Source = path,
            TimeZone = options.TimeZone,
            SourceType = source.SourceType,
            Index = source.Index,
            Event = text,
            LineNumber = lineNumber,
        }, cancellationToken);
        return true;
    }

    private static IEnumerable<string> Expand(ImportSource source)
    {
        if (Directory.Exists(source.Path))
        {
            return Directory.EnumerateFiles(source.Path, "*", source.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
        }

        return FileMonitorService.ExpandGlob(source.Path, source.Recursive);
    }

    /// <summary>
    /// Files the running agent is responsible for, so an import leaves them alone: those it has
    /// read (in its registry) and those its inputs match that are recent enough for it to read.
    /// </summary>
    public static HashSet<string> AgentFiles(AgentConfiguration config, string registryPath, DateTime now)
    {
        var paths = TrackedPaths(registryPath);
        foreach (var input in config.Inputs)
        {
            var ignoreBefore = input.IgnoreOlderThanHours > 0 ? now.AddHours(-input.IgnoreOlderThanHours) : DateTime.MinValue;
            foreach (var pattern in input.Paths)
            {
                foreach (var file in FileMonitorService.ExpandGlob(pattern, input.Recursive))
                {
                    if (!IsExcludedBy(input, file) && File.GetLastWriteTimeUtc(file) >= ignoreBefore)
                    {
                        paths.Add(file);
                    }
                }
            }
        }

        return paths;
    }

    private static bool IsExcludedBy(InputConfiguration input, string file) => FileMonitorService.IsExcluded(file, input.ExcludePatterns);

    /// <summary>
    /// Paths the running agent tracks, read from its registry file without changing it.
    /// </summary>
    public static HashSet<string> TrackedPaths(string registryPath)
    {
        var paths = new HashSet<string>(FileStateRegistry.PathComparer);
        if (!File.Exists(registryPath))
        {
            return paths;
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(registryPath));
        var entries = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray()
            : default;
        foreach (var entry in entries)
        {
            if (entry.TryGetProperty("path", out var path) && path.GetString() is { } value)
            {
                paths.Add(value);
            }
        }

        return paths;
    }

    /// <summary>Spaces events out to a rate, checking once per second's worth.</summary>
    private sealed class Throttle(int perSecond)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _sent;

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            if (perSecond <= 0 || ++_sent % perSecond != 0)
            {
                return;
            }

            var due = TimeSpan.FromSeconds((double)_sent / perSecond);
            var wait = due - _clock.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken);
            }
        }
    }
}
