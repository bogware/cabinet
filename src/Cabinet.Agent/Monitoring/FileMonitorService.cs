using System.Text.RegularExpressions;
using System.Threading.Channels;
using Cabinet.Agent.Configuration;
using Cabinet.Agent.Forwarding;

namespace Cabinet.Agent.Monitoring;

/// <summary>
/// Background service that monitors log files and forwards new lines to the output pipeline.
/// Handles file discovery (glob patterns), tailing, rotation detection, and multi-line merging.
/// </summary>
public sealed class FileMonitorService : BackgroundService
{
    private readonly AgentConfiguration _config;
    private readonly FileStateRegistry _registry;
    private readonly LogForwarder _forwarder;
    private readonly ILogger<FileMonitorService> _logger;
    private readonly string _hostname;
    private readonly Dictionary<string, CancellationTokenSource> _harvesters = new(StringComparer.OrdinalIgnoreCase);

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
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        finally
        {
            // Cancel all harvesters
            foreach (var cts in _harvesters.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }

            _harvesters.Clear();
            _registry.Save();
            _logger.LogInformation("File monitor stopped");
        }
    }

    private void DiscoverFiles()
    {
        foreach (var input in _config.Inputs)
        {
            foreach (var pattern in input.Paths)
            {
                try
                {
                    var files = ExpandGlob(pattern, input.Recursive);

                    foreach (var file in files)
                    {
                        if (IsExcluded(file, input.ExcludePatterns))
                            continue;

                        if (_harvesters.ContainsKey(file))
                            continue;

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
        var trackedPaths = _registry.GetTrackedPaths();
        foreach (var path in trackedPaths)
        {
            if (!File.Exists(path) && _harvesters.TryGetValue(path, out var cts))
            {
                _logger.LogInformation("File deleted, stopping harvester: {Path}", path);
                cts.Cancel();
                cts.Dispose();
                _harvesters.Remove(path);
                _registry.Remove(path);
            }
        }
    }

    private void StartHarvester(string filePath, InputConfiguration input)
    {
        var cts = new CancellationTokenSource();
        _harvesters[filePath] = cts;

        _logger.LogInformation("Starting harvester for {Path} (sourcetype={SourceType}, index={Index})",
            filePath, input.SourceType, input.Index);

        // Register file with header hash for rotation detection
        var headerHash = FileStateRegistry.ComputeHeaderHash(filePath);
        var fileInfo = new FileInfo(filePath);
        _registry.RegisterFile(filePath, fileInfo.Length, headerHash);

        _ = Task.Run(() => HarvestFileAsync(filePath, input, cts.Token), cts.Token);
    }

    private async Task HarvestFileAsync(string filePath, InputConfiguration input, CancellationToken cancellationToken)
    {
        Regex? multiLinePattern = null;
        if (input.MultiLine != null && !string.IsNullOrEmpty(input.MultiLine.Pattern))
        {
            try
            {
                multiLinePattern = new Regex(
                    input.MultiLine.Pattern,
                    RegexOptions.Compiled,
                    TimeSpan.FromSeconds(1));
            }
            catch (RegexParseException ex)
            {
                _logger.LogError(ex, "Invalid multi-line pattern for {Path}: {Pattern}",
                    filePath, input.MultiLine.Pattern);
            }
        }

        try
        {
            // Determine starting offset
            var state = _registry.GetState(filePath);
            var offset = state?.Offset ?? 0;
            var linesRead = state?.LinesRead ?? 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!File.Exists(filePath))
                {
                    _logger.LogDebug("File not found, waiting: {Path}", filePath);
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    continue;
                }

                var fileInfo = new FileInfo(filePath);

                // Detect file truncation or rotation
                if (fileInfo.Length < offset)
                {
                    _logger.LogInformation("File truncated/rotated, resetting offset: {Path}", filePath);
                    offset = 0;
                    linesRead = 0;
                    _registry.ResetOffset(filePath);
                }

                // Detect rotation via header hash change
                if (state?.HeaderHash != null && fileInfo.Length > 0)
                {
                    var currentHash = FileStateRegistry.ComputeHeaderHash(filePath);
                    if (currentHash != null && currentHash != state.HeaderHash)
                    {
                        _logger.LogInformation("File rotated (header hash changed), resetting: {Path}", filePath);
                        offset = 0;
                        linesRead = 0;
                        _registry.ResetOffset(filePath);
                        _registry.RegisterFile(filePath, fileInfo.Length, currentHash);
                    }
                }

                // Read new lines
                if (fileInfo.Length > offset)
                {
                    try
                    {
                        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        stream.Seek(offset, SeekOrigin.Begin);
                        using var reader = new StreamReader(stream);

                        var pendingMultiLine = new List<string>();

                        while (!cancellationToken.IsCancellationRequested)
                        {
                            var line = await reader.ReadLineAsync(cancellationToken);
                            if (line == null) break;

                            linesRead++;

                            if (multiLinePattern != null && input.MultiLine != null)
                            {
                                // Negate=true (default): pattern matches start of new event
                                // Negate=false: pattern matches continuation lines
                                var matchesPattern = multiLinePattern.IsMatch(line);
                                var isNewEvent = input.MultiLine.Negate ? matchesPattern : !matchesPattern;

                                if (!isNewEvent && pendingMultiLine.Count > 0 &&
                                    pendingMultiLine.Count < input.MultiLine.MaxLines)
                                {
                                    // Continuation line
                                    pendingMultiLine.Add(line);
                                }
                                else
                                {
                                    // Flush previous multi-line event
                                    if (pendingMultiLine.Count > 0)
                                    {
                                        var merged = string.Join('\n', pendingMultiLine);
                                        await EmitEvent(merged, filePath, input, linesRead - pendingMultiLine.Count);
                                    }

                                    pendingMultiLine.Clear();
                                    pendingMultiLine.Add(line);
                                }
                            }
                            else
                            {
                                await EmitEvent(line, filePath, input, linesRead);
                            }
                        }

                        // Flush any remaining multi-line buffer
                        if (pendingMultiLine.Count > 0)
                        {
                            var merged = string.Join('\n', pendingMultiLine);
                            await EmitEvent(merged, filePath, input, linesRead - pendingMultiLine.Count + 1);
                        }

                        offset = stream.Position;
                        _registry.UpdateState(filePath, offset, fileInfo.Length, linesRead);
                    }
                    catch (IOException ex) when (ex is not FileNotFoundException)
                    {
                        _logger.LogDebug(ex, "IO error reading {Path}, will retry", filePath);
                    }
                    catch (FileNotFoundException)
                    {
                        _logger.LogInformation("File disappeared: {Path}", filePath);
                        break;
                    }
                }

                // Wait before polling again
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Harvester failed for {Path}", filePath);
        }
        finally
        {
            _logger.LogDebug("Harvester stopped for {Path}", filePath);
        }
    }

    private async Task EmitEvent(string rawLine, string filePath, InputConfiguration input, long lineNumber)
    {
        var evt = new LogEventDto
        {
            Timestamp = DateTime.UtcNow,
            Host = _hostname,
            Source = filePath,
            SourceType = input.SourceType,
            Index = input.Index,
            Event = rawLine,
            LineNumber = lineNumber
        };

        await _forwarder.EnqueueAsync(evt);
    }

    /// <summary>
    /// Expand a file path or glob pattern to matching files.
    /// Supports *, **, and ? wildcards.
    /// </summary>
    public static IReadOnlyList<string> ExpandGlob(string pattern, bool recursive)
    {
        // If the pattern is a direct file path (no wildcards)
        if (!pattern.Contains('*') && !pattern.Contains('?'))
        {
            return File.Exists(pattern) ? [Path.GetFullPath(pattern)] : [];
        }

        // Split into directory and file pattern
        var directory = Path.GetDirectoryName(pattern);
        var filePattern = Path.GetFileName(pattern);

        if (string.IsNullOrEmpty(directory))
        {
            directory = ".";
        }

        if (!Directory.Exists(directory))
        {
            return [];
        }

        var searchOption = recursive || pattern.Contains("**")
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        try
        {
            return Directory.GetFiles(directory, filePattern, searchOption)
                .Select(Path.GetFullPath)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsExcluded(string filePath, List<string> excludePatterns)
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
