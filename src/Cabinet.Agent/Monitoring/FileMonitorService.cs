using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Cabinet.Agent.Configuration;
using Cabinet.Agent.Forwarding;

namespace Cabinet.Agent.Monitoring;

/// <summary>
/// Monitors log files and forwards new lines to the output pipeline, handling file discovery,
/// tailing, rotation and multi-line merging.
/// <para>
/// Delivery is at-least-once: a file's checkpoint moves only when its events have been accepted by
/// the server or written to the spool, so a crash re-reads rather than loses them. Re-read events
/// get the same ID, and the server ignores IDs it already has.
/// </para>
/// </summary>
public sealed class FileMonitorService : BackgroundService
{
    private sealed record Harvester(CancellationTokenSource Cancellation, Task Task);

    private readonly AgentConfiguration _config;
    private readonly FileStateRegistry _registry;
    private readonly LogForwarder _forwarder;
    private readonly ILogger<FileMonitorService> _logger;
    private readonly string _hostname;
    private readonly string _timeZone;
    private readonly ConcurrentDictionary<string, Harvester> _harvesters = new(FileStateRegistry.PathComparer);

    // Files that couldn't be deleted, so the warning isn't repeated every scan
    private readonly ConcurrentDictionary<string, byte> _undeletable = new(FileStateRegistry.PathComparer);

    // Files closed for inactivity, and how they looked then. They're reopened when that changes.
    private readonly ConcurrentDictionary<string, (long Length, DateTime Written)> _closed = new(FileStateRegistry.PathComparer);
    private readonly TimeSpan _pollInterval;

    // How long a file can refuse to open before it's worth a warning
    private static readonly TimeSpan UnopenableWarningAfter = TimeSpan.FromMinutes(1);

    public FileMonitorService(
        AgentConfiguration config,
        FileStateRegistry registry,
        LogForwarder forwarder,
        ILogger<FileMonitorService> logger)
    {
        _config = config;
        _registry = registry;
        _forwarder = forwarder;
        _logger = logger;
        _hostname = string.IsNullOrEmpty(config.Hostname)
            ? Environment.MachineName
            : config.Hostname;
        _pollInterval = TimeSpan.FromMilliseconds(config.PollIntervalMilliseconds);
        _timeZone = string.IsNullOrEmpty(config.TimeZone) ? LocalTimeZoneName() : config.TimeZone;
        _forwarder.BatchDurable += OnBatchDurable;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("File monitor starting. Hostname: {Hostname}, Inputs: {Count}",
            _hostname, _config.Inputs.Count);

        _registry.Initialize();

        // Initial file discovery
        DiscoverFiles();

        // Periodic scan for new files
        using var scanTimer = new PeriodicTimer(TimeSpan.FromSeconds(_config.ScanIntervalSeconds));

        try
        {
            while (await scanTimer.WaitForNextTickAsync(stoppingToken))
            {
                DiscoverFiles();
                CleanupDeletedFiles();
                DeleteDeliveredFiles();
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("File monitor stopping");
        }
        finally
        {
            await StopAsync();
        }
    }

    /// <summary>
    /// Stops reading, then delivers what was read, then saves the checkpoints that delivery moved.
    /// The order matters: saving first would lose those checkpoints and cause a re-read.
    /// </summary>
    private async Task StopAsync()
    {
        var harvesters = _harvesters.Values.ToList();
        foreach (var harvester in harvesters)
        {
            await harvester.Cancellation.CancelAsync();
        }

        await Task.WhenAll(harvesters.Select(h => h.Task));
        await _forwarder.FlushAsync();

        _registry.Save();
        _logger.LogInformation("File monitor stopped");
    }

    private void DiscoverFiles()
    {
        var tracked = new HashSet<string>(_registry.GetTrackedPaths(), FileStateRegistry.PathComparer);
        foreach (var input in _config.Inputs.Where(input => input.IsFileInput))
        {
            var ignoreBefore = input.IgnoreOlderThanHours > 0
                ? DateTime.UtcNow.AddHours(-input.IgnoreOlderThanHours)
                : DateTime.MinValue;

            foreach (var pattern in input.Paths)
            {
                try
                {
                    foreach (var file in ExpandGlob(pattern, input.Recursive))
                    {
                        if (IsExcluded(file, input.ExcludePatterns) || _harvesters.ContainsKey(file))
                        {
                            continue;
                        }

                        var info = new FileInfo(file);
                        if (!info.Exists)
                        {
                            continue;
                        }

                        // Unchanged since it was closed for inactivity
                        if (_closed.TryGetValue(file, out var closed) && closed.Length == info.Length && closed.Written == info.LastWriteTimeUtc)
                        {
                            continue;
                        }

                        // Old history is skipped, but a file already part read is always finished
                        if (info.LastWriteTimeUtc < ignoreBefore && !tracked.Contains(file))
                        {
                            continue;
                        }

                        _closed.TryRemove(file, out _);
                        StartHarvester(file, input);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to expand path pattern: {Pattern}", pattern);
                }
            }
        }
    }

    private void CleanupDeletedFiles()
    {
        foreach (var path in _registry.GetTrackedPaths())
        {
            if (!File.Exists(path) && !_harvesters.ContainsKey(path))
            {
                _logger.LogInformation("File deleted, no longer tracking: {Path}", path);
                _registry.Remove(path);
                _closed.TryRemove(path, out _);
            }
        }
    }

    /// <summary>
    /// Deletes files the server has stored completely and that have been quiet for the input's
    /// <see cref="InputConfiguration.DeleteDeliveredAfterDays"/>. The checkpoint only moves once
    /// the server has accepted events, so a file at its end has nothing left to send.
    /// </summary>
    private void DeleteDeliveredFiles()
    {
        foreach (var input in _config.Inputs.Where(input => input.IsFileInput && input.DeleteDeliveredAfterDays > 0))
        {
            var quietSince = DateTime.UtcNow.AddDays(-input.DeleteDeliveredAfterDays);
            foreach (var pattern in input.Paths)
            {
                IReadOnlyList<string> files;
                try
                {
                    files = ExpandGlob(pattern, input.Recursive);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Couldn't list {Pattern} for deletion", pattern);
                    continue;
                }

                foreach (var file in files)
                {
                    if (_harvesters.ContainsKey(file) || _undeletable.ContainsKey(file) || IsExcluded(file, input.ExcludePatterns))
                    {
                        continue;
                    }

                    TryDeleteDelivered(file, quietSince);
                }
            }
        }
    }

    private void TryDeleteDelivered(string file, DateTime quietSince)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.LastWriteTimeUtc > quietSince)
        {
            return;
        }

        // Only files this agent has read, still holding the contents it read
        var state = _registry.GetState(file);
        if (state == null || state.Displaced || FileStateRegistry.ComputeFingerprint(file) != state.Fingerprint)
        {
            return;
        }

        var remaining = info.Length - state.Offset;
        if (remaining > 0 && !IsUnfinishedLine(file, state.Offset, remaining))
        {
            return;
        }

        try
        {
            File.Delete(file);
            _registry.Remove(file);
            _closed.TryRemove(file, out _);
            if (remaining > 0)
            {
                _logger.LogInformation("Deleted {Path}, stored in full but for {Bytes} bytes of a final line that was never finished", file, remaining);
            }
            else
            {
                _logger.LogInformation("Deleted {Path}, stored in full", file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _undeletable[file] = 0;
            _logger.LogWarning("Can't delete {Path}, which is stored in full: {Message}. The agent's service needs write access to its folder.", file, ex.Message);
        }
    }

    /// <summary>
    /// Whether what's left after the checkpoint is a single line that never got its line ending,
    /// such as one cut off when the writer stopped. It can never be sent, and the file is quiet.
    /// </summary>
    private bool IsUnfinishedLine(string file, long offset, long remaining)
    {
        if (remaining > _config.MaxLineBytes)
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Position = offset;
            var buffer = new byte[remaining];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return Array.IndexOf(buffer, (byte)'\n', 0, read) < 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Couldn't check the end of {Path}", file);
            return false;
        }
    }

    private void StartHarvester(string filePath, InputConfiguration input)
    {
        _logger.LogInformation("Starting harvester for {Path} (sourcetype={SourceType}, index={Index})",
            filePath, input.SourceType, input.Index);

        var cancellation = new CancellationTokenSource();

        // Start only once registered, so the harvester's own cleanup can't run first.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Task.Run(async () =>
        {
            await start.Task;
            await HarvestFileAsync(filePath, input, cancellation.Token);
        });

        _harvesters[filePath] = new Harvester(cancellation, task);
        start.SetResult();
    }

    private async Task HarvestFileAsync(string filePath, InputConfiguration input, CancellationToken cancellationToken)
    {
        try
        {
            var merger = new MultiLineMerger(input.MultiLine, _logger);

            while (!cancellationToken.IsCancellationRequested)
            {
                var drained = await TailContentsAsync(filePath, input, merger, cancellationToken);
                if (!drained)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Harvester cancelled for {Path}", filePath);
        }
        catch (Exception ex)
        {
            // The next scan starts a new harvester, which resumes from the checkpoint.
            _logger.LogError(ex, "Harvester failed for {Path}, restarting at the next scan", filePath);
        }
        finally
        {
            if (_harvesters.TryRemove(filePath, out var harvester))
            {
                harvester.Cancellation.Dispose();
            }

            _logger.LogDebug("Harvester stopped for {Path}", filePath);
        }
    }

    /// <summary>
    /// Follows one file's contents through an open handle until the path no longer holds them.
    /// The handle keeps working after a rename, so lines written just before a rotation are still
    /// read. Returns true if the path now holds new contents, or false if it's gone.
    /// </summary>
    private async Task<bool> TailContentsAsync(
        string filePath,
        InputConfiguration input,
        MultiLineMerger merger,
        CancellationToken cancellationToken)
    {
        await using var stream = await OpenWhenReadableAsync(filePath, cancellationToken);
        if (stream == null)
        {
            return false;
        }

        var fingerprint = await WaitForFingerprintAsync(filePath, cancellationToken);
        if (fingerprint == null)
        {
            return false;
        }

        var state = _registry.Resolve(filePath, fingerprint, stream.Length);
        _registry.SaveNewKeys();
        var reader = new LineReader(stream, state.Offset, _config.MaxLineBytes);
        var lineNumber = state.LinesRead;
        var lastActivity = DateTime.UtcNow;
        var lastLength = stream.Length;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Copy-and-truncate rotation: the same handle now has less data than we've read.
            if (stream.Length < reader.Position)
            {
                _logger.LogInformation("File truncated, reading from the start: {Path}", filePath);
                await FlushMergedAsync(merger, filePath, input, state, cancellationToken);

                // Start over as for any new file, which waits for a complete first line. Recording
                // an identity now could save it empty, and a restart wouldn't recognize the file.
                _registry.Remove(filePath);
                return true;
            }

            await foreach (var line in reader.ReadLinesAsync(cancellationToken))
            {
                lineNumber++;
                foreach (var merged in merger.Add(line, lineNumber))
                {
                    await EmitAsync(merged, filePath, input, state, cancellationToken);
                }
            }

            if (merger.TakeIfExpired() is { } expired)
            {
                await EmitAsync(expired, filePath, input, state, cancellationToken);
            }

            // Everything readable through this handle has been read. Has the path moved on?
            var current = new FileInfo(filePath);
            if (!current.Exists)
            {
                _logger.LogInformation("File removed or renamed, stopping: {Path}", filePath);
                await FlushMergedAsync(merger, filePath, input, state, cancellationToken);
                return false;
            }

            var currentFingerprint = FileStateRegistry.ComputeFingerprint(filePath);
            if (currentFingerprint != null && currentFingerprint != state.Fingerprint)
            {
                _logger.LogInformation("File rotated, switching to the new file: {Path}", filePath);
                await FlushMergedAsync(merger, filePath, input, state, cancellationToken);
                return true;
            }

            if (current.Length < reader.Position && stream.Length >= reader.Position)
            {
                _logger.LogInformation("File replaced by a smaller one, switching to it: {Path}", filePath);
                await FlushMergedAsync(merger, filePath, input, state, cancellationToken);
                return true;
            }

            if (stream.Length != lastLength)
            {
                lastLength = stream.Length;
                lastActivity = DateTime.UtcNow;
            }
            else if (input.CloseInactiveSeconds > 0
                && current.Length == stream.Length
                && DateTime.UtcNow - lastActivity >= TimeSpan.FromSeconds(input.CloseInactiveSeconds))
            {
                // Read to the end and quiet: release it until it changes. What's been read stays
                // queued for delivery, and the checkpoint follows delivery as always.
                await FlushMergedAsync(merger, filePath, input, state, cancellationToken);
                _closed[filePath] = (current.Length, current.LastWriteTimeUtc);
                _logger.LogInformation("No changes for {Seconds} s, closing until it changes: {Path}", input.CloseInactiveSeconds, filePath);
                return false;
            }

            await Task.Delay(_pollInterval, cancellationToken);
        }
    }

    private async Task<FileStream?> OpenWhenReadableAsync(string filePath, CancellationToken cancellationToken)
    {
        var since = DateTime.UtcNow;
        var warned = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (warned)
                {
                    _logger.LogInformation("Opened {Path} after {Seconds:N0} s", filePath, (DateTime.UtcNow - since).TotalSeconds);
                }

                return stream;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (IOException ex)
            {
                // Retrying silently hid a file that stayed unreadable all day
                if (!warned && DateTime.UtcNow - since >= UnopenableWarningAfter)
                {
                    _logger.LogWarning("Can't open {Path} for {Seconds:N0} s, still trying: {Message}", filePath, (DateTime.UtcNow - since).TotalSeconds, ex.Message);
                    warned = true;
                }
                else
                {
                    _logger.LogDebug(ex, "IO error opening {Path}, will retry", filePath);
                }

                await Task.Delay(_pollInterval, cancellationToken);
            }
        }
    }

    /// <summary>Waits for a complete first line. Returns null if the file disappears first.</summary>
    private async Task<string?> WaitForFingerprintAsync(string filePath, CancellationToken cancellationToken)
    {
        while (File.Exists(filePath))
        {
            if (FileStateRegistry.ComputeFingerprint(filePath) is { } fingerprint)
            {
                return fingerprint;
            }

            await Task.Delay(_pollInterval, cancellationToken);
        }

        return null;
    }

    private async Task FlushMergedAsync(
        MultiLineMerger merger,
        string filePath,
        InputConfiguration input,
        FileState state,
        CancellationToken cancellationToken)
    {
        if (merger.Take() is { } merged)
        {
            await EmitAsync(merged, filePath, input, state, cancellationToken);
        }
    }

    private async Task EmitAsync(
        MergedLine line,
        string filePath,
        InputConfiguration input,
        FileState state,
        CancellationToken cancellationToken)
    {
        // Blank lines carry nothing, but their bytes are still covered by later checkpoints.
        if (string.IsNullOrWhiteSpace(line.Text))
        {
            return;
        }

        var evt = new LogEventDto
        {
            Id = CreateEventId(state.FileKey, line.StartOffset),
            FileKey = state.FileKey,
            StartOffset = line.StartOffset,
            EndOffset = line.EndOffset,
            Timestamp = DateTime.UtcNow,
            Host = _hostname,
            Source = filePath,
            TimeZone = _timeZone,
            SourceType = input.SourceType,
            Index = input.Index,
            Event = line.Text,
            LineNumber = line.LineNumber
        };

        if (input.IsKubernetes)
        {
            // The runtime wraps each line; send what the container wrote, when it wrote it
            var entry = KubernetesLogLine.Parse(line.Text);
            evt.Event = entry.Message;
            evt.Timestamp = entry.Time ?? evt.Timestamp;
            evt.Fields = KubernetesLogLine.PodFields(filePath);
            evt.Fields["k8s.stream"] = entry.Stream;
            if (string.IsNullOrWhiteSpace(evt.Event))
            {
                return;
            }
        }

        await _forwarder.EnqueueAsync(evt, cancellationToken);
    }

    /// <summary>
    /// The same bytes of the same file always get the same ID, however many times they're sent.
    /// </summary>
    /// <summary>
    /// This machine's zone by IANA name where possible, which every platform understands.
    /// </summary>
    public static string LocalTimeZoneName()
    {
        var local = TimeZoneInfo.Local;
        if (local.HasIanaId)
        {
            return local.Id;
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana) ? iana : local.Id;
    }

    public static string CreateEventId(string fileKey, long startOffset)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{fileKey}:{startOffset}"));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    private void OnBatchDurable(IReadOnlyList<LogEventDto> batch)
    {
        foreach (var file in batch.Where(e => e.FileKey != null).GroupBy(e => e.FileKey!))
        {
            var last = file.MaxBy(e => e.EndOffset)!;
            _registry.Commit(file.Key, last.EndOffset, last.LineNumber, last.StartOffset, FileStateRegistry.HashText(last.Event));
        }
    }

    /// <summary>
    /// Expand a file path or glob pattern to matching files. Supports <c>*</c>, <c>?</c> and
    /// <c>**</c> in any path segment. With <paramref name="recursive"/>, the last segment also
    /// matches in subdirectories.
    /// </summary>
    public static IReadOnlyList<string> ExpandGlob(string pattern, bool recursive)
    {
        // If the pattern is a direct file path (no wildcards)
        if (!pattern.Contains('*') && !pattern.Contains('?'))
        {
            return File.Exists(pattern) ? [Path.GetFullPath(pattern)] : [];
        }

        var normalized = pattern.Replace('\\', '/');
        var segments = normalized.Split('/');
        var firstWildcard = Array.FindIndex(segments, s => s.Contains('*') || s.Contains('?'));

        var root = string.Join('/', segments[..firstWildcard]);
        if (root.Length == 0)
        {
            root = normalized.StartsWith('/') ? "/" : ".";
        }

        if (!Directory.Exists(root))
        {
            return [];
        }

        var wildcardSegments = segments[firstWildcard..];
        var searchesDeep = recursive || wildcardSegments.Length > 1;
        var relativePattern = string.Join('/', wildcardSegments);
        if (recursive && !relativePattern.StartsWith("**/", StringComparison.Ordinal))
        {
            relativePattern = "**/" + relativePattern;
        }

        var matcher = GlobToRegex(relativePattern);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = searchesDeep,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
        };

        return Directory.EnumerateFiles(root, "*", options)
            .Where(file => matcher.IsMatch(Path.GetRelativePath(root, file).Replace('\\', '/')))
            .Select(Path.GetFullPath)
            .OrderBy(file => file, FileStateRegistry.PathComparer)
            .ToList();
    }

    private static Regex GlobToRegex(string glob)
    {
        var regex = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/')
                {
                    i++;
                    regex.Append("(?:.*/)?");
                }
                else
                {
                    regex.Append(".*");
                }
            }
            else if (c == '*')
            {
                regex.Append("[^/]*");
            }
            else if (c == '?')
            {
                regex.Append("[^/]");
            }
            else
            {
                regex.Append(Regex.Escape(c.ToString()));
            }
        }

        regex.Append('$');
        var options = OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None;
        return new Regex(regex.ToString(), options, TimeSpan.FromSeconds(1));
    }

    internal static bool IsExcluded(string filePath, IReadOnlyList<string> excludePatterns)
    {
        if (excludePatterns.Count == 0) return false;

        var fileName = Path.GetFileName(filePath);
        foreach (var pattern in excludePatterns)
        {
            // Simple wildcard matching for exclude patterns
            if (pattern.StartsWith('*') && fileName.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase))
                return true;

            if (fileName.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
