using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Cabinet.Core.Configuration;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cabinet.Core.Ingestion;

/// <summary>
/// High-performance ingestion engine with streaming and parallel processing
/// </summary>
public class IngestionEngine : IIngestionEngine
{
    private readonly ILogStorage _storage;
    private readonly IParserRegistry _parserRegistry;
    private readonly ICheckpointManager _checkpointManager;
    private readonly IngestionConfiguration _config;
    private readonly ILogger<IngestionEngine> _logger;
    private readonly object _resultLock = new();

    public IngestionEngine(
        ILogStorage storage,
        IParserRegistry parserRegistry,
        ICheckpointManager checkpointManager,
        IOptions<CabinetConfiguration> config,
        ILogger<IngestionEngine> logger)
    {
        _storage = storage;
        _parserRegistry = parserRegistry;
        _checkpointManager = checkpointManager;
        _config = config.Value.Ingestion;
        _logger = logger;
    }

    public async Task<IngestionResult> IngestDirectoryAsync(
        string path,
        IngestionOptions options,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new IngestionResult();
        var startTime = DateTime.UtcNow;

        try
        {
            // Find all matching files
            var files = FindFiles(path, options);
            var totalFiles = files.Count;

            if (totalFiles == 0)
            {
                _logger.LogWarning("No files found matching patterns in {Path}", path);
                return result;
            }

            _logger.LogInformation("Found {Count} files to process", totalFiles);

            // Create processing pipeline
            var channel = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(_config.ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait
            });

            // Start consumer task
            var consumerTask = ConsumeEventsAsync(channel.Reader, result, cancellationToken);

            // Process files in parallel
            var semaphore = new SemaphoreSlim(_config.ParallelParsers);
            var tasks = new List<Task>();

            foreach (var file in files)
            {
                await semaphore.WaitAsync(cancellationToken);

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await ProcessFileAsync(file, options, channel.Writer, result, progress, totalFiles, cancellationToken);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, cancellationToken));
            }

            await Task.WhenAll(tasks);
            channel.Writer.Complete();

            await consumerTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ingestion failed for directory {Path}", path);
            lock (_resultLock)
            {
                result.Errors.Add(new IngestionError
                {
                    File = path,
                    Error = ex.Message,
                    Details = ex.ToString()
                });
            }
        }
        finally
        {
            result.Duration = DateTime.UtcNow - startTime;
        }

        return result;
    }

    public async Task<IngestionResult> IngestFileAsync(
        string filePath,
        IngestionOptions options,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new IngestionResult();
        var startTime = DateTime.UtcNow;

        try
        {
            var channel = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(_config.ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait
            });

            var consumerTask = ConsumeEventsAsync(channel.Reader, result, cancellationToken);

            await ProcessFileAsync(filePath, options, channel.Writer, result, progress, 1, cancellationToken);

            channel.Writer.Complete();
            await consumerTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ingestion failed for file {Path}", filePath);
            result.Errors.Add(new IngestionError
            {
                File = filePath,
                Error = ex.Message,
                Details = ex.ToString()
            });
        }
        finally
        {
            result.Duration = DateTime.UtcNow - startTime;
        }

        return result;
    }

    public async IAsyncEnumerable<LogEvent> StreamFileAsync(
        string filePath,
        StreamOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}");

        var parser = SelectParser(filePath, options.Filter);

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8, true, 4096);

        if (options.FromEnd)
        {
            fs.Seek(0, SeekOrigin.End);
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync();

            if (line != null)
            {
                var logEvent = await parser.ParseAsync(line, filePath, 0, cancellationToken);
                if (logEvent != null && MatchesFilter(logEvent, options.Filter))
                {
                    yield return logEvent;
                }
            }
            else if (options.Follow)
            {
                await Task.Delay(options.PollIntervalMs, cancellationToken);
            }
            else
            {
                break;
            }
        }
    }

    private async Task ProcessFileAsync(
        string filePath,
        IngestionOptions options,
        ChannelWriter<LogEvent> writer,
        IngestionResult result,
        IProgress<IngestionProgress>? progress,
        int totalFiles,
        CancellationToken cancellationToken)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);

            // Check file size limit
            if (_config.MaxFileSizeMB > 0 && fileInfo.Length > _config.MaxFileSizeMB * 1024 * 1024)
            {
                _logger.LogWarning("Skipping file {Path} - exceeds size limit", filePath);
                return;
            }

            // Check checkpoint for incremental processing
            long startPosition = 0;
            if (options.Incremental && !options.Force)
            {
                var checkpoint = await _checkpointManager.GetCheckpointAsync(filePath, cancellationToken);
                if (checkpoint != null)
                {
                    if (checkpoint.FileSize == fileInfo.Length &&
                        checkpoint.LastModified == fileInfo.LastWriteTimeUtc)
                    {
                        _logger.LogDebug("File {Path} unchanged, skipping", filePath);
                        return;
                    }
                    startPosition = checkpoint.LastPosition;
                }
            }

            var parser = SelectParser(filePath, options.Profile);
            var linesProcessed = 0L;
            var eventsGenerated = 0L;

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(startPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8, true, 4096);

            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                linesProcessed++;

                var logEvent = await parser.ParseAsync(line, filePath, linesProcessed, cancellationToken);
                if (logEvent != null)
                {
                    if (!string.IsNullOrEmpty(options.Host))
                    {
                        logEvent.Host = options.Host;
                    }

                    await writer.WriteAsync(logEvent, cancellationToken);
                    eventsGenerated++;
                }

                // Report progress periodically
                if (linesProcessed % 1000 == 0 && progress != null)
                {
                    progress.Report(new IngestionProgress
                    {
                        CurrentFile = Path.GetFileName(filePath),
                        FilesCompleted = result.FilesProcessed,
                        TotalFiles = totalFiles,
                        LinesProcessed = result.LinesProcessed + linesProcessed,
                        EventsStored = result.EventsStored,
                        PercentComplete = ((double)result.FilesProcessed / totalFiles) * 100,
                        Status = $"Processing {Path.GetFileName(filePath)}"
                    });
                }
            }

            // Save checkpoint
            if (options.Incremental)
            {
                await _checkpointManager.SaveCheckpointAsync(new FileCheckpoint
                {
                    FilePath = filePath,
                    FileSize = fileInfo.Length,
                    LastModified = fileInfo.LastWriteTimeUtc,
                    LastPosition = fs.Position,
                    LinesProcessed = linesProcessed,
                    CheckpointTime = DateTime.UtcNow
                }, cancellationToken);
            }

            lock (_resultLock)
            {
                result.FilesProcessed++;
                result.LinesProcessed += linesProcessed;
                result.BytesProcessed += fileInfo.Length;
            }

            _logger.LogInformation("Processed file {Path}: {Lines} lines, {Events} events",
                filePath, linesProcessed, eventsGenerated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing file {Path}", filePath);
            lock (_resultLock)
            {
                result.Errors.Add(new IngestionError
                {
                    File = filePath,
                    Error = ex.Message
                });
            }
        }
    }

    private async Task ConsumeEventsAsync(
        ChannelReader<LogEvent> reader,
        IngestionResult result,
        CancellationToken cancellationToken)
    {
        var batch = new List<LogEvent>(_config.BatchSize);

        await foreach (var logEvent in reader.ReadAllAsync(cancellationToken))
        {
            batch.Add(logEvent);

            if (batch.Count >= _config.BatchSize)
            {
                var stored = await _storage.StoreBatchAsync(batch, cancellationToken);
                lock (_resultLock)
                {
                    result.EventsStored += stored;
                }
                batch.Clear();
            }
        }

        // Store remaining events
        if (batch.Count > 0)
        {
            var stored = await _storage.StoreBatchAsync(batch, cancellationToken);
            lock (_resultLock)
            {
                result.EventsStored += stored;
            }
        }
    }

    private List<string> FindFiles(string path, IngestionOptions options)
    {
        var files = new List<string>();
        var searchOption = options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        foreach (var pattern in options.IncludePatterns)
        {
            var foundFiles = Directory.GetFiles(path, pattern, searchOption);
            files.AddRange(foundFiles);
        }

        // Apply exclusions
        if (options.ExcludePatterns.Any())
        {
            files = files.Where(f => !options.ExcludePatterns.Any(p =>
                FileSystemName.MatchesSimpleExpression(p, Path.GetFileName(f)))).ToList();
        }

        return files.Distinct().OrderBy(f => f).ToList();
    }

    private ILogParser SelectParser(string filePath, string? profile)
    {
        ILogParser? parser = null;

        if (!string.IsNullOrEmpty(profile))
        {
            parser = _parserRegistry.GetParser(profile);
        }

        if (parser == null)
        {
            // Try to detect parser from file content
            using var reader = new StreamReader(filePath, Encoding.UTF8, true, 4096);
            var firstLine = reader.ReadLine();
            if (!string.IsNullOrEmpty(firstLine))
            {
                parser = _parserRegistry.FindParser(firstLine);
            }
        }

        return parser ?? _parserRegistry.GetParserForSourceType(SourceTypes.Generic)!;
    }

    private bool MatchesFilter(LogEvent logEvent, string? filter)
    {
        if (string.IsNullOrEmpty(filter))
            return true;

        // Simple filter implementation - can be enhanced with proper query parsing
        var lower = filter.ToLowerInvariant();
        var rawLower = logEvent.RawLine.ToLowerInvariant();

        return rawLower.Contains(lower);
    }
}