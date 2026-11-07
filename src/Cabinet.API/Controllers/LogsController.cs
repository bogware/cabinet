using Cabinet.API.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Cabinet.Core.Models;
using Cabinet.Core.Interfaces;
using Cabinet.API.Services;
using Cabinet.API.Middleware;
using Cabinet.Parsers;
using Cabinet.Storage; // For extension methods
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cabinet.API.Controllers;

/// <summary>
/// Logs management API
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class LogsController : ControllerBase
{
    private readonly ILogStorage _storage;
    private readonly LogLineParser _lineParser;
    private readonly IndexAccessScope? _indexScope;
    private readonly IAuditLogger _auditLogger;
    private readonly MultiLineLogMerger _multiLineMerger;
    private readonly QueryCacheService? _queryCache;
    private readonly ILogger<LogsController> _logger;

    public LogsController(
        ILogStorage storage,
        LogLineParser lineParser,
        IAuditLogger auditLogger,
        MultiLineLogMerger multiLineMerger,
        ILogger<LogsController> logger,
        QueryCacheService? queryCache = null,
        IndexAccessScope? indexScope = null)
    {
        _indexScope = indexScope;
        _storage = indexScope?.Restrict(storage) ?? storage;
        _lineParser = lineParser;
        _auditLogger = auditLogger;
        _multiLineMerger = multiLineMerger;
        _queryCache = queryCache;
        _logger = logger;
    }

    /// <summary>
    /// Ingest log events
    /// </summary>
    [HttpPost("ingest")]
    [RequireCapability(Capability.Ingest)]
    [ProducesResponseType(typeof(IngestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> IngestLogs([FromBody] IngestRequest request)
    {
        if (request.Logs == null || !request.Logs.Any())
        {
            return Problem(detail: "No logs provided", statusCode: 400);
        }

        if (request.Logs.Count > 10_000)
        {
            return Problem(detail: "Batch too large. Maximum 10,000 log entries per request.", statusCode: 400);
        }

        var events = new List<LogEvent>();
        var errors = new List<string>();

        const string targetIndex = "main";

        if (_indexScope?.Principal != null && !_indexScope.Can(Capability.Ingest, targetIndex))
        {
            return Problem(detail: $"Not allowed to ingest into index '{targetIndex}'", statusCode: 403);
        }

        // Each item in the Logs array is already a distinct event — no multi-line merging.
        // Multi-line merging is for raw text streams (HEC raw, syslog, file tailing) where
        // event boundaries aren't explicit. REST API callers define boundaries by array entries.
        foreach (var log in request.Logs)
        {
            try
            {
                var evt = await _lineParser.ParseAsync(log);
                evt.Index = targetIndex;
                events.Add(evt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse log entry");
                errors.Add("Failed to parse log entry");
            }
        }

        var stored = 0;
        if (events.Any())
        {
            using (var span = Cabinet.Core.Utilities.CabinetDiagnostics.Source.StartActivity("ingest.store"))
            {
                span?.SetTag("cabinet.events", events.Count);
                stored = await _storage.StoreBatchAsync(events);
            }

            // Invalidate cached query results for this index so dashboards get fresh data
            var index = events.FirstOrDefault()?.Index ?? "main";
            _queryCache?.InvalidateIndex(index);
        }

        // Audit log the ingestion
        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Ingest,
            ResourceType = "LogEvent",
            Description = $"Ingested {stored} events into index '{targetIndex}'",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return Ok(new IngestResponse
        {
            TotalReceived = request.Logs.Count,
            TotalIngested = stored,
            Errors = errors
        });
    }

    /// <summary>
    /// Stream logs in real-time
    /// </summary>
    [HttpGet("stream")]
    [RequireCapability(Capability.Search)]
    [LimitConcurrency]
    [ProducesResponseType(typeof(LogEvent), StatusCodes.Status200OK)]
    public async IAsyncEnumerable<LogEvent> StreamLogs(
        [FromQuery] DateTime? since = null,
        [FromQuery] string? source = null,
        [FromQuery] string? severity = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Validate parameters to prevent DoS via long strings
        if (source?.Length > 256 || severity?.Length > 64)
        {
            yield break;
        }

        // Enforce a 5-minute timeout on streaming to prevent resource exhaustion.
        // Explicitly link HttpContext.RequestAborted so client disconnection cancels the
        // operation even if the framework doesn't propagate it through the enumerator token.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, HttpContext.RequestAborted);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        var filter = BuildFilter(since, source, severity);

        var events = await _storage.GetEventsAsync(filter, null, 0, 1000, timeoutCts.Token);
        foreach (var evt in events)
        {
            yield return evt;

            if (timeoutCts.Token.IsCancellationRequested)
                break;
        }
    }

    /// <summary>
    /// Live log stream via Server-Sent Events (SSE).
    /// Keeps the connection open and pushes new log events as they arrive via polling.
    /// Sends heartbeat comments every 15 seconds to keep the connection alive.
    /// Maximum stream duration is 60 minutes.
    /// </summary>
    /// <param name="index">Logical index to stream from (default "main")</param>
    /// <param name="filter">Optional search text to filter events</param>
    /// <param name="pollIntervalMs">Polling interval in milliseconds (default 1000, min 500, max 5000)</param>
    [HttpGet("stream/live")]
    [RequireCapability(Capability.Search)]
    [LimitConcurrency(ConcurrencyPool.Stream)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task StreamLive(
        [FromQuery] string index = "main",
        [FromQuery] string? filter = null,
        [FromQuery] int pollIntervalMs = 1000)
    {
        // Validate parameters before writing any response headers
        if (index.Length > 256)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(new { detail = "Index name too long (max 256 characters)" });
            return;
        }

        if (filter?.Length > 2048)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(new { detail = "Filter too long (max 2048 characters)" });
            return;
        }

        pollIntervalMs = Math.Clamp(pollIntervalMs, 500, 5000);

        // Set SSE response headers
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Append("X-Accel-Buffering", "no"); // Disable proxy buffering (nginx)

        // Max 60-minute duration, linked to client disconnect
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        cts.CancelAfter(TimeSpan.FromMinutes(60));
        var token = cts.Token;

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        var tail = new LiveEventTail(_storage, index, filter, DateTime.UtcNow);
        var lastHeartbeat = DateTime.UtcNow;
        var writer = Response.Body;

        // Send initial comment to confirm the stream is established
        await WriteSseCommentAsync(writer, "connected", token);
        await writer.FlushAsync(token);

        try
        {
            while (!token.IsCancellationRequested)
            {
                var events = await tail.PollAsync(DateTime.UtcNow, token);
                foreach (var evt in events)
                {
                    await WriteSseDataAsync(writer, JsonSerializer.Serialize(evt, jsonOptions), token);
                }

                var emittedCount = events.Count;
                if (emittedCount > 0)
                {
                    await writer.FlushAsync(token);
                }

                // Send heartbeat comment every 15 seconds to keep connection alive
                var now = DateTime.UtcNow;
                if ((now - lastHeartbeat).TotalSeconds >= 15)
                {
                    await WriteSseCommentAsync(writer, "heartbeat", token);
                    await writer.FlushAsync(token);
                    lastHeartbeat = now;
                }

                // Wait before next poll
                await Task.Delay(pollIntervalMs, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on client disconnect or max-duration timeout
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live SSE stream ended unexpectedly for index '{Index}'", index);
        }
    }

    /// <summary>
    /// Write an SSE data line: "data: {payload}\n\n"
    /// </summary>
    private static async Task WriteSseDataAsync(Stream stream, string data, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes($"data: {data}\n\n");
        await stream.WriteAsync(bytes, cancellationToken);
    }

    /// <summary>
    /// Write an SSE comment line: ": {comment}\n\n"
    /// </summary>
    private static async Task WriteSseCommentAsync(Stream stream, string comment, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes($": {comment}\n\n");
        await stream.WriteAsync(bytes, cancellationToken);
    }

    /// <summary>
    /// Get logs with pagination (offset/count, consistent with all other endpoints)
    /// </summary>
    [HttpGet]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(IEnumerable<LogEvent>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLogs(
        [FromQuery] int offset = 0,
        [FromQuery] int count = 100,
        [FromQuery] DateTime? startTime = null,
        [FromQuery] DateTime? endTime = null,
        [FromQuery] string? search = null,
        [FromQuery] string? source = null,
        [FromQuery] string? severity = null,
        [FromQuery] string? sortBy = "timestamp",
        [FromQuery] bool descending = true)
    {
        count = Math.Clamp(count, 1, 1000);
        offset = Math.Max(0, offset);

        var filter = BuildFilter(startTime, source, severity);

        if (endTime.HasValue)
            filter["timestamp_lte"] = endTime.Value;

        var events = await _storage.GetEventsAsync(
            filter,
            search,
            offset,
            count,
            sortBy: sortBy,
            descending: descending);

        var total = await _storage.GetEventCountAsync(filter, search: search);
        Response.Headers["X-Total-Count"] = total.ToString();
        await AuditReadAsync(id: null, $"Listed {events.Count()} events");

        return Ok(events);
    }

    /// <summary>
    /// Get a specific log event
    /// </summary>
    [HttpGet("{id}")]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(LogEvent), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLog(string id)
    {
        var evt = await _storage.GetEventAsync(id);
        if (evt == null)
        {
            return Problem(detail: "Log event not found", statusCode: 404);
        }

        await AuditReadAsync(id, "Read an event");
        return Ok(evt);
    }

    /// <summary>
    /// Delete logs
    /// </summary>
    [HttpDelete]
    [RequireCapability(Capability.IndexManagement)]
    [ProducesResponseType(typeof(DeleteResponse), StatusCodes.Status200OK)]
    [Authorize(Policy = "RequireWritePermission")]
    public async Task<IActionResult> DeleteLogs([FromQuery] DateTime? olderThan = null)
    {
        if (olderThan == null)
        {
            return Problem(detail: "olderThan is required", statusCode: 400);
        }

        if (_indexScope?.Principal?.IndexesWith(Capability.IndexManagement) != null)
        {
            return Problem(detail: "This deletes from every index, which needs IndexManagement on all of them", statusCode: 403);
        }

        var deleted = await _storage.PurgeOldEventsAsync(olderThan.Value);

        // Audit log the deletion (fire-and-forget)
        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Delete,
            ResourceType = "LogEvent",
            Description = $"Deleted {deleted} events (olderThan={olderThan})",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return Ok(new DeleteResponse
        {
            DeletedCount = deleted,
            Message = $"Deleted {deleted} log events"
        });
    }

    /// <summary>
    /// Get log statistics
    /// </summary>
    [HttpGet("stats")]
    [RequireCapability(Capability.SystemHealth)]
    [ProducesResponseType(typeof(LogStatistics), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatistics(
        [FromQuery] DateTime? startTime = null,
        [FromQuery] DateTime? endTime = null)
    {
        var stats = await _storage.GetStatsAsync();

        // If time range specified, adjust the total count to reflect the filtered range
        if (startTime.HasValue || endTime.HasValue)
        {
            var context = new QueryContext
            {
                StartTime = startTime,
                EndTime = endTime
            };
            stats.TotalEvents = await _storage.GetCountAsync(context);
        }

        return Ok(stats);
    }

    private Task AuditReadAsync(string? id, string description) => _auditLogger.LogAsync(new AuditEvent
    {
        PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
        Action = AuditAction.Search,
        ResourceType = "LogEvent",
        ResourceId = id,
        Description = description,
        SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
    });

    private static Dictionary<string, object> BuildFilter(
        DateTime? since = null,
        string? source = null,
        string? severity = null)
    {
        var filter = new Dictionary<string, object>();

        if (since.HasValue)
            filter["timestamp_gte"] = since.Value;

        if (!string.IsNullOrEmpty(source))
            filter["source_type"] = source;

        if (!string.IsNullOrEmpty(severity))
            filter["severity"] = severity;

        return filter;
    }
}

// Request/Response DTOs
public class IngestRequest
{
    public List<string> Logs { get; set; } = new();
    public Dictionary<string, string>? Metadata { get; set; }
}

public class IngestResponse
{
    public int TotalReceived { get; set; }
    public int TotalIngested { get; set; }
    public List<string> Errors { get; set; } = new();
}

public class DeleteResponse
{
    public int DeletedCount { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class LogStatistics
{
    public long TotalEvents { get; set; }
    public Dictionary<string, long> EventsByType { get; set; } = new();
    public Dictionary<string, long> EventsBySeverity { get; set; } = new();
    public DateTime? OldestEvent { get; set; }
    public DateTime? NewestEvent { get; set; }
}