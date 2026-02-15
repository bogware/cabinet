using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Cabinet.Agent.Configuration;

namespace Cabinet.Agent.Forwarding;

/// <summary>
/// Batches log events and forwards them to the Cabinet API server.
/// Provides reliable delivery with retry, exponential backoff, and disk-based spill buffer.
/// </summary>
public sealed class LogForwarder : IAsyncDisposable
{
    private readonly OutputConfiguration _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<LogForwarder> _logger;
    private readonly Channel<LogEventDto> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processingTask;
    private readonly string _spillDirectory;
    private long _eventsSent;
    private long _eventsDropped;
    private long _batchesSent;
    private long _batchesFailed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public LogForwarder(OutputConfiguration config, HttpClient httpClient, ILogger<LogForwarder> logger)
    {
        _config = config;
        _httpClient = httpClient;
        _logger = logger;
        _spillDirectory = config.SpillDirectory;

        _channel = Channel.CreateBounded<LogEventDto>(new BoundedChannelOptions(config.MaxBufferSize)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        // Configure HTTP client
        var serverUri = new Uri(config.ServerUrl.TrimEnd('/'));
        _httpClient.BaseAddress = serverUri;
        _httpClient.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);

        // Security: warn if sending API key over plaintext HTTP to non-localhost
        if (serverUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
            !serverUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
            !serverUri.Host.Equals("127.0.0.1", StringComparison.Ordinal) &&
            !serverUri.Host.Equals("::1", StringComparison.Ordinal))
        {
            _logger.LogWarning("API key is being sent over unencrypted HTTP to {Host}. " +
                "Use HTTPS in production to prevent credential interception", serverUri.Host);
        }

        _httpClient.DefaultRequestHeaders.Add("X-API-Key", config.ApiKey);
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        _processingTask = Task.Run(() => ProcessBatchesAsync(_cts.Token));
    }

    /// <summary>
    /// Enqueue a log event for batched forwarding.
    /// </summary>
    public async ValueTask EnqueueAsync(LogEventDto evt)
    {
        if (!_channel.Writer.TryWrite(evt))
        {
            // Channel is full, wait briefly then drop if still full
            if (!await _channel.Writer.WaitToWriteAsync(_cts.Token))
            {
                Interlocked.Increment(ref _eventsDropped);
                return;
            }

            _channel.Writer.TryWrite(evt);
        }
    }

    /// <summary>
    /// Get forwarding statistics.
    /// </summary>
    public ForwarderStats GetStats() => new()
    {
        EventsSent = Interlocked.Read(ref _eventsSent),
        EventsDropped = Interlocked.Read(ref _eventsDropped),
        BatchesSent = Interlocked.Read(ref _batchesSent),
        BatchesFailed = Interlocked.Read(ref _batchesFailed),
        QueueDepth = _channel.Reader.Count
    };

    private async Task ProcessBatchesAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Log forwarder started. Server: {Server}, BatchSize: {BatchSize}, FlushInterval: {Interval}s",
            _config.ServerUrl, _config.BatchSize, _config.FlushIntervalSeconds);

        // First, retry any spilled batches from previous runs
        await RetrySpilledBatchesAsync(cancellationToken);

        var batch = new List<LogEventDto>(_config.BatchSize);
        var flushInterval = TimeSpan.FromSeconds(_config.FlushIntervalSeconds);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Wait for first event or cancellation
                using var flushCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                flushCts.CancelAfter(flushInterval);

                try
                {
                    while (batch.Count < _config.BatchSize)
                    {
                        var evt = await _channel.Reader.ReadAsync(flushCts.Token);
                        batch.Add(evt);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Flush interval elapsed — send what we have
                }

                if (batch.Count > 0)
                {
                    await SendBatchWithRetryAsync(batch, cancellationToken);
                    batch.Clear();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ChannelClosedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in batch processor");
                batch.Clear();
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        // Drain remaining events on shutdown
        while (_channel.Reader.TryRead(out var remaining))
        {
            batch.Add(remaining);
            if (batch.Count >= _config.BatchSize)
            {
                await SendBatchWithRetryAsync(batch, CancellationToken.None);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await SendBatchWithRetryAsync(batch, CancellationToken.None);
        }

        _logger.LogInformation("Log forwarder stopped. Total sent: {Sent}, dropped: {Dropped}",
            Interlocked.Read(ref _eventsSent), Interlocked.Read(ref _eventsDropped));
    }

    private async Task SendBatchWithRetryAsync(List<LogEventDto> batch, CancellationToken cancellationToken)
    {
        var payload = BuildHecPayload(batch);
        var retryDelay = TimeSpan.FromSeconds(1);

        for (var attempt = 0; attempt <= _config.MaxRetries; attempt++)
        {
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync("/services/collector/event", content, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    Interlocked.Add(ref _eventsSent, batch.Count);
                    Interlocked.Increment(ref _batchesSent);

                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("Sent batch of {Count} events (attempt {Attempt})",
                            batch.Count, attempt + 1);
                    }

                    return;
                }

                // Non-retryable status codes
                if (response.StatusCode is HttpStatusCode.BadRequest
                    or HttpStatusCode.Unauthorized
                    or HttpStatusCode.Forbidden)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError("Server rejected batch ({Status}): {Body}. Dropping {Count} events",
                        response.StatusCode, body, batch.Count);
                    Interlocked.Add(ref _eventsDropped, batch.Count);
                    Interlocked.Increment(ref _batchesFailed);
                    return;
                }

                _logger.LogWarning("Server returned {Status} for batch of {Count} events (attempt {Attempt}/{Max})",
                    response.StatusCode, batch.Count, attempt + 1, _config.MaxRetries + 1);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "HTTP error sending batch (attempt {Attempt}/{Max})",
                    attempt + 1, _config.MaxRetries + 1);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Timeout sending batch (attempt {Attempt}/{Max})",
                    attempt + 1, _config.MaxRetries + 1);
            }

            if (attempt < _config.MaxRetries)
            {
                await Task.Delay(retryDelay, cancellationToken);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 60));
            }
        }

        // All retries exhausted — spill to disk
        _logger.LogError("All retries exhausted for batch of {Count} events, spilling to disk", batch.Count);
        Interlocked.Increment(ref _batchesFailed);
        await SpillBatchToDiskAsync(payload);
    }

    private async Task SpillBatchToDiskAsync(string payload)
    {
        try
        {
            Directory.CreateDirectory(_spillDirectory);
            var fileName = $"batch_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.json";
            var filePath = Path.Combine(_spillDirectory, fileName);
            await File.WriteAllTextAsync(filePath, payload);
            _logger.LogInformation("Spilled failed batch to {Path}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to spill batch to disk, events will be lost");
            // Events are lost at this point
        }
    }

    private async Task RetrySpilledBatchesAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_spillDirectory)) return;

        var spillFiles = Directory.GetFiles(_spillDirectory, "batch_*.json")
            .OrderBy(f => f)
            .ToList();

        if (spillFiles.Count == 0) return;

        _logger.LogInformation("Found {Count} spilled batch files to retry", spillFiles.Count);

        foreach (var file in spillFiles)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                var payload = await File.ReadAllTextAsync(file, cancellationToken);
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync("/services/collector/event", content, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    File.Delete(file);
                    _logger.LogInformation("Successfully retried spilled batch: {File}", Path.GetFileName(file));
                }
                else
                {
                    _logger.LogWarning("Failed to retry spilled batch {File}: {Status}",
                        Path.GetFileName(file), response.StatusCode);
                    break; // Stop retrying if server is rejecting — try again next cycle
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error retrying spilled batch {File}", Path.GetFileName(file));
                break;
            }
        }
    }

    /// <summary>
    /// Build a Splunk HEC-compatible payload (newline-delimited JSON).
    /// </summary>
    private static string BuildHecPayload(List<LogEventDto> events)
    {
        var sb = new StringBuilder(events.Count * 256);
        foreach (var evt in events)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(JsonSerializer.Serialize(evt, JsonOptions));
        }

        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _cts.CancelAsync();

        try
        {
            await _processingTask.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Forwarder shutdown timed out after 30 seconds");
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }

        _cts.Dispose();
        _httpClient.Dispose();
    }
}

/// <summary>
/// Forwarding statistics for monitoring/diagnostics.
/// </summary>
public sealed class ForwarderStats
{
    public long EventsSent { get; init; }
    public long EventsDropped { get; init; }
    public long BatchesSent { get; init; }
    public long BatchesFailed { get; init; }
    public int QueueDepth { get; init; }
}
