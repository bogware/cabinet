using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Cabinet.Agent.Configuration;

namespace Cabinet.Agent.Forwarding;

/// <summary>
/// Batches log events and forwards them to the Cabinet API server.
/// <para>
/// A batch is durable once the server accepts it or it's written to the spool, and only then does
/// <see cref="BatchDurable"/> fire to move the file checkpoints. Spooled batches are replayed
/// periodically. When the spool is full the forwarder keeps retrying, which fills the queue and
/// pauses file reading, so data waits in the source files instead of being dropped.
/// </para>
/// </summary>
public sealed class LogForwarder : IAsyncDisposable
{
    private const string CollectorPath = "/services/collector/event";
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);

    private enum SendResult
    {
        Accepted,
        Rejected,
        Unavailable,
    }

    private sealed record QueuedEvent(LogEventDto Event, string Json);

    private readonly OutputConfiguration _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<LogForwarder> _logger;
    private readonly Channel<LogEventDto> _channel;
    private readonly CancellationTokenSource _stopSending = new();
    private readonly CancellationTokenSource _stopReplay = new();
    private readonly Task _processingTask;
    private readonly Task _replayTask;
    private readonly string _spoolDirectory;
    private readonly string _deadLetterDirectory;
    private readonly TimeSpan _retryDelay;
    private readonly object _flushLock = new();
    private Task? _flushTask;
    private QueuedEvent? _carriedOver;
    private bool _serverDown;
    private long _eventsSent;
    private long _eventsDropped;
    private long _eventsSpooled;
    private long _eventsDeadLettered;
    private long _batchesSent;
    private long _batchesFailed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Raised when events are safe: accepted by the server, or written to the spool or dead-letter
    /// directory. Their file checkpoints can move past them.
    /// </summary>
    public event Action<IReadOnlyList<LogEventDto>>? BatchDurable;

    public LogForwarder(OutputConfiguration config, HttpClient httpClient, ILogger<LogForwarder> logger)
    {
        _config = config;
        _httpClient = httpClient;
        _logger = logger;
        _spoolDirectory = config.SpillDirectory;
        _deadLetterDirectory = Path.Combine(config.SpillDirectory, "dead-letter");
        _retryDelay = TimeSpan.FromMilliseconds(config.RetryDelayMilliseconds);

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

        _processingTask = Task.Run(ProcessBatchesAsync);
        _replayTask = Task.Run(ReplaySpoolAsync);
    }

    /// <summary>
    /// Enqueue a log event for batched forwarding, waiting if the queue is full. That wait is what
    /// slows file reading when the server can't keep up.
    /// </summary>
    public async ValueTask EnqueueAsync(LogEventDto evt, CancellationToken cancellationToken = default)
    {
        try
        {
            await _channel.Writer.WriteAsync(evt, cancellationToken);
        }
        catch (ChannelClosedException)
        {
            // Shutting down. The checkpoint hasn't moved, so this is re-read at the next start.
            Interlocked.Increment(ref _eventsDropped);
        }
    }

    /// <summary>
    /// Get forwarding statistics.
    /// </summary>
    public ForwarderStats GetStats() => new()
    {
        EventsSent = Interlocked.Read(ref _eventsSent),
        EventsDropped = Interlocked.Read(ref _eventsDropped),
        EventsSpooled = Interlocked.Read(ref _eventsSpooled),
        EventsDeadLettered = Interlocked.Read(ref _eventsDeadLettered),
        BatchesSent = Interlocked.Read(ref _batchesSent),
        BatchesFailed = Interlocked.Read(ref _batchesFailed),
        QueueDepth = _channel.Reader.Count
    };

    /// <summary>
    /// Stops accepting events and delivers what's queued. Whatever can't be sent before the
    /// shutdown deadline goes to the spool.
    /// </summary>
    public Task FlushAsync()
    {
        lock (_flushLock)
        {
            return _flushTask ??= FlushCoreAsync();
        }
    }

    private async Task FlushCoreAsync()
    {
        _channel.Writer.TryComplete();

        try
        {
            await _processingTask.WaitAsync(TimeSpan.FromSeconds(_config.ShutdownFlushSeconds));
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Delivery didn't finish within {Seconds}s, spooling the rest", _config.ShutdownFlushSeconds);
            await _stopSending.CancelAsync();
            await _processingTask;
        }

        await _stopReplay.CancelAsync();
        await _replayTask;

        _logger.LogInformation("Log forwarder stopped. Sent: {Sent}, spooled: {Spooled}, dead-lettered: {DeadLettered}",
            Interlocked.Read(ref _eventsSent), Interlocked.Read(ref _eventsSpooled), Interlocked.Read(ref _eventsDeadLettered));
    }

    private async Task ProcessBatchesAsync()
    {
        _logger.LogInformation("Log forwarder started. Server: {Server}, BatchSize: {BatchSize}, FlushInterval: {Interval}s",
            _config.ServerUrl, _config.BatchSize, _config.FlushIntervalSeconds);

        var token = _stopSending.Token;
        List<QueuedEvent>? inFlight = null;
        try
        {
            while (_carriedOver != null || await _channel.Reader.WaitToReadAsync(token))
            {
                inFlight = await CollectBatchAsync(token);
                if (inFlight.Count > 0)
                {
                    await DeliverAsync(inFlight, token);
                }

                inFlight = null;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger.LogDebug("Sending stopped at the shutdown deadline");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in batch processor");
        }

        await SpoolRemainingAsync(inFlight);
    }

    /// <summary>
    /// Takes events until the batch reaches its event or byte limit, or the flush interval passes.
    /// The byte limit keeps batches under the server's request size limit.
    /// </summary>
    private async Task<List<QueuedEvent>> CollectBatchAsync(CancellationToken token)
    {
        var batch = new List<QueuedEvent>(_config.BatchSize);
        var bytes = 0L;

        using var flush = CancellationTokenSource.CreateLinkedTokenSource(token);
        flush.CancelAfter(TimeSpan.FromSeconds(_config.FlushIntervalSeconds));

        try
        {
            while (batch.Count < _config.BatchSize)
            {
                var next = _carriedOver;
                _carriedOver = null;

                if (next == null)
                {
                    if (!_channel.Reader.TryRead(out var evt))
                    {
                        if (!await _channel.Reader.WaitToReadAsync(flush.Token))
                        {
                            break;
                        }

                        continue;
                    }

                    next = new QueuedEvent(evt, JsonSerializer.Serialize(evt, JsonOptions));
                }

                if (batch.Count > 0 && bytes + next.Json.Length + 1 > _config.MaxBatchBytes)
                {
                    _carriedOver = next;
                    break;
                }

                batch.Add(next);
                bytes += next.Json.Length + 1;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            _logger.LogTrace("Flush interval elapsed with {Count} events", batch.Count);
        }

        return batch;
    }

    private async Task DeliverAsync(List<QueuedEvent> batch, CancellationToken token)
    {
        var payload = BuildPayload(batch);
        while (true)
        {
            var result = await SendWithRetriesAsync(payload, batch.Count, token);
            if (result == SendResult.Accepted)
            {
                Interlocked.Add(ref _eventsSent, batch.Count);
                Interlocked.Increment(ref _batchesSent);
                MarkDurable(batch);
                return;
            }

            if (result == SendResult.Rejected)
            {
                await HandleRejectedAsync(batch, payload, token);
                return;
            }

            Interlocked.Increment(ref _batchesFailed);
            if (await TryWriteToSpoolAsync(payload))
            {
                Interlocked.Add(ref _eventsSpooled, batch.Count);
                MarkDurable(batch);
                return;
            }

            // Spool full or unwritable. Keep the batch: nothing is lost, and the full queue
            // pauses file reading until the server returns.
            _logger.LogWarning("Can't deliver or spool {Count} events, will keep retrying", batch.Count);
            await Task.Delay(MaxRetryDelay, token);
        }
    }

    /// <summary>
    /// The server refused this request, so retrying as-is won't help. Splitting finds out whether
    /// the size or one bad event caused it, so good events in the batch still get through.
    /// </summary>
    private async Task HandleRejectedAsync(List<QueuedEvent> batch, string payload, CancellationToken token)
    {
        if (batch.Count > 1)
        {
            var middle = batch.Count / 2;
            await DeliverAsync(batch[..middle], token);
            await DeliverAsync(batch[middle..], token);
            return;
        }

        Interlocked.Increment(ref _batchesFailed);
        if (await TryWriteAsync(_deadLetterDirectory, payload))
        {
            Interlocked.Add(ref _eventsDeadLettered, batch.Count);
            _logger.LogError("Server rejected an event, saved it to {Directory}", _deadLetterDirectory);
            MarkDurable(batch);
            return;
        }

        _logger.LogError("Server rejected an event and it couldn't be saved. It will be re-read at the next start");
    }

    private async Task<SendResult> SendWithRetriesAsync(string payload, int count, CancellationToken token)
    {
        // A batch has already used up its retries, so this one tries once and goes to the spool.
        // The spool replay notices when the server returns.
        var retries = _serverDown ? 0 : _config.MaxRetries;

        var delay = _retryDelay;
        for (var attempt = 0; attempt <= retries; attempt++)
        {
            var (result, retryAfter) = await PostAsync(payload, count, attempt, token);
            if (result != SendResult.Unavailable)
            {
                _serverDown = false;
                return result;
            }

            if (attempt < retries)
            {
                var wait = retryAfter ?? delay;
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * wait.TotalMilliseconds * 0.25);
                await Task.Delay(wait + jitter, token);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, MaxRetryDelay.TotalMilliseconds));
            }
        }

        _serverDown = true;
        return SendResult.Unavailable;
    }

    private async Task<(SendResult Result, TimeSpan? RetryAfter)> PostAsync(string payload, int count, int attempt, CancellationToken token)
    {
        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(CollectorPath, content, token);

            if (response.IsSuccessStatusCode)
            {
                return (SendResult.Accepted, null);
            }

            if (response.StatusCode is HttpStatusCode.BadRequest
                or HttpStatusCode.RequestEntityTooLarge
                or HttpStatusCode.UnprocessableEntity)
            {
                _logger.LogWarning("Server rejected {Count} events with {Status}", count, response.StatusCode);
                return (SendResult.Rejected, null);
            }

            // Includes 401 and 403: a bad API key is fixable, and shouldn't cost the data.
            _logger.LogWarning("Server returned {Status} for {Count} events (attempt {Attempt}/{Max})",
                response.StatusCode, count, attempt + 1, _config.MaxRetries + 1);
            return (SendResult.Unavailable, ReadRetryAfter(response));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP error sending {Count} events (attempt {Attempt}/{Max})", count, attempt + 1, _config.MaxRetries + 1);
            return (SendResult.Unavailable, null);
        }
        catch (TaskCanceledException) when (!token.IsCancellationRequested)
        {
            // The server may have stored these already. The retry is safe: event IDs don't change.
            _logger.LogWarning("Timeout sending {Count} events (attempt {Attempt}/{Max})", count, attempt + 1, _config.MaxRetries + 1);
            return (SendResult.Unavailable, null);
        }
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var wait = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (wait is not { } value || value <= TimeSpan.Zero)
        {
            return null;
        }

        return value < MaxRetryAfter ? value : MaxRetryAfter;
    }

    private async Task SpoolRemainingAsync(List<QueuedEvent>? inFlight)
    {
        _channel.Writer.TryComplete();

        // Part of an in-flight batch may have arrived already. Event IDs make that harmless.
        var remaining = new List<QueuedEvent>(inFlight ?? []);
        if (_carriedOver != null)
        {
            remaining.Add(_carriedOver);
            _carriedOver = null;
        }

        while (_channel.Reader.TryRead(out var evt))
        {
            remaining.Add(new QueuedEvent(evt, JsonSerializer.Serialize(evt, JsonOptions)));
        }

        foreach (var batch in remaining.Chunk(_config.BatchSize))
        {
            var list = batch.ToList();
            if (await TryWriteToSpoolAsync(BuildPayload(list)))
            {
                Interlocked.Add(ref _eventsSpooled, list.Count);
                MarkDurable(list);
            }
        }
    }

    private async Task<bool> TryWriteToSpoolAsync(string payload)
    {
        if (SpoolSizeBytes() + Encoding.UTF8.GetByteCount(payload) > _config.MaxSpoolBytes)
        {
            _logger.LogWarning("Spool is at its {Limit} byte limit", _config.MaxSpoolBytes);
            return false;
        }

        return await TryWriteAsync(_spoolDirectory, payload);
    }

    /// <summary>Writes a batch so it either exists completely or not at all.</summary>
    private async Task<bool> TryWriteAsync(string directory, string payload)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var name = $"batch_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}";
            var temp = Path.Combine(directory, name + ".tmp");

            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(payload));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, Path.Combine(directory, name + ".json"));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to write batch to {Directory}", directory);
            return false;
        }
    }

    private long SpoolSizeBytes()
    {
        if (!Directory.Exists(_spoolDirectory))
        {
            return 0;
        }

        return new DirectoryInfo(_spoolDirectory).EnumerateFiles("batch_*.json").Sum(file => file.Length);
    }

    private async Task ReplaySpoolAsync()
    {
        var token = _stopReplay.Token;
        try
        {
            while (true)
            {
                await ReplaySpooledBatchesAsync(token);
                await Task.Delay(TimeSpan.FromSeconds(_config.SpoolRetryIntervalSeconds), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger.LogDebug("Spool replay stopped");
        }
    }

    private async Task ReplaySpooledBatchesAsync(CancellationToken token)
    {
        if (!Directory.Exists(_spoolDirectory))
        {
            return;
        }

        var files = Directory.GetFiles(_spoolDirectory, "batch_*.json").OrderBy(file => file, StringComparer.Ordinal).ToList();
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();

            string payload;
            try
            {
                payload = await File.ReadAllTextAsync(file, token);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Can't read spooled batch {File}", file);
                continue;
            }

            var (result, _) = await PostAsync(payload, payload.Count(c => c == '\n') + 1, 0, token);
            if (result == SendResult.Unavailable)
            {
                // Still down. Stop here and try again next cycle.
                return;
            }

            if (result == SendResult.Accepted)
            {
                File.Delete(file);
                _logger.LogInformation("Delivered spooled batch {File}", Path.GetFileName(file));
                continue;
            }

            // A rejected batch must not block the ones behind it.
            Directory.CreateDirectory(_deadLetterDirectory);
            File.Move(file, Path.Combine(_deadLetterDirectory, Path.GetFileName(file)));
            _logger.LogError("Server rejected spooled batch, moved it to {Directory}", _deadLetterDirectory);
        }
    }

    private void MarkDurable(List<QueuedEvent> batch)
    {
        try
        {
            BatchDurable?.Invoke(batch.Select(item => item.Event).ToList());
        }
        catch (Exception ex)
        {
            // At worst the checkpoint lags, and re-read events are ignored by ID.
            _logger.LogError(ex, "Failed to record delivered events");
        }
    }

    /// <summary>
    /// Build a Splunk HEC-compatible payload (newline-delimited JSON).
    /// </summary>
    private static string BuildPayload(List<QueuedEvent> events) =>
        string.Join('\n', events.Select(item => item.Json));

    public async ValueTask DisposeAsync()
    {
        await FlushAsync();
        _stopSending.Dispose();
        _stopReplay.Dispose();
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
    public long EventsSpooled { get; init; }
    public long EventsDeadLettered { get; init; }
    public long BatchesSent { get; init; }
    public long BatchesFailed { get; init; }
    public int QueueDepth { get; init; }
}
