using Cabinet.Parsers;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Core.Utilities;
using Cabinet.API.Middleware;

namespace Cabinet.API.Controllers;

/// <summary>
/// Splunk-compatible HTTP Event Collector (HEC) endpoint.
/// Accepts events in Splunk HEC JSON format for drop-in compatibility.
/// </summary>
[ApiController]
[Route("services/collector")]
[Authorize]
public class HecController : ControllerBase
{
    private readonly ILogStorage _storage;
    private readonly LogLineParser _lineParser;
    private readonly IAuditLogger _auditLogger;
    private readonly Services.QueryCacheService? _queryCache;
    private readonly ILogger<HecController> _logger;

    public HecController(ILogStorage storage, LogLineParser lineParser, IAuditLogger auditLogger, ILogger<HecController> logger,
        Services.QueryCacheService? queryCache = null)
    {
        _storage = storage;
        _lineParser = lineParser;
        _auditLogger = auditLogger;
        _queryCache = queryCache;
        _logger = logger;
    }

    /// <summary>
    /// Ingest events in Splunk HEC JSON format.
    /// Supports single events and batched events (one JSON object per line).
    /// </summary>
    [HttpPost("event")]
    [Consumes("application/json")]
    [RequireCapability(Capability.Ingest)]
    [RequestSizeLimit(5 * 1024 * 1024)] // 5 MB max per HEC request
    [ProducesResponseType(typeof(HecResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(HecResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ReceiveEvent(CancellationToken cancellationToken)
    {
        // Defense-in-depth: enforce max body size at application level in addition to
        // the [RequestSizeLimit] attribute which operates at the Kestrel/middleware layer.
        // This guards against memory exhaustion from oversized payloads even if the
        // framework-level limit is misconfigured or bypassed by a reverse proxy.
        const int MaxBodySize = 10 * 1024 * 1024; // 10 MB
        if (Request.ContentLength > MaxBodySize)
        {
            return BadRequest(new HecResponse { Text = "Payload too large", Code = 5 });
        }

        string body;
        using (var reader = new StreamReader(Request.Body))
        {
            body = await reader.ReadToEndAsync(cancellationToken);
        }

        // Post-read size check for chunked transfers where Content-Length is absent
        if (body.Length > MaxBodySize)
        {
            return BadRequest(new HecResponse { Text = "Payload too large", Code = 5 });
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return BadRequest(new HecResponse { Text = "No data", Code = 5 });
        }

        var events = new List<LogEvent>();
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parseErrors = 0;

        foreach (var line in lines)
        {
            try
            {
                var hecEvent = JsonSerializer.Deserialize<HecEvent>(line, HecJsonOptions);
                if (hecEvent == null)
                {
                    continue;
                }

                events.Add(await ConvertHecEventAsync(hecEvent, cancellationToken));
            }
            catch (JsonException ex)
            {
                parseErrors++;
                if (parseErrors <= 10)
                    _logger.LogWarning(ex, "Failed to parse HEC event line ({ErrorCount} so far)", parseErrors);

                // Stop processing after too many parse errors to prevent CPU abuse
                if (parseErrors > 50)
                {
                    _logger.LogError("HEC request aborted: exceeded 50 parse errors");
                    break;
                }
            }
        }

        if (events.Count == 0)
        {
            return BadRequest(new HecResponse { Text = "No valid events", Code = 5 });
        }

        try
        {
            await _storage.StoreBatchAsync(events, cancellationToken);
            _logger.LogDebug("HEC ingested {Count} events ({Errors} parse errors)", events.Count, parseErrors);

            // Invalidate cached query results so dashboards get fresh data
            var hecIndex = events.FirstOrDefault()?.Index ?? "main";
            _queryCache?.InvalidateIndex(hecIndex);

            await _auditLogger.LogAsync(new AuditEvent
            {
                PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
                Action = AuditAction.Ingest,
                ResourceType = "LogEvent",
                Description = $"HEC ingested {events.Count} events ({parseErrors} skipped)",
                SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
            });

            var text = parseErrors > 0
                ? $"Success ({parseErrors} event(s) skipped due to parse errors)"
                : "Success";
            return Ok(new HecResponse { Text = text, Code = 0 });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store HEC events");
            return StatusCode(500, new HecResponse { Text = "Internal server error", Code = 8 });
        }
    }

    /// <summary>
    /// Ingest raw text events. Each line becomes a separate log event.
    /// Metadata (host, sourcetype, index) provided via query parameters.
    /// </summary>
    [HttpPost("raw")]
    [RequireCapability(Capability.Ingest)]
    [RequestSizeLimit(5 * 1024 * 1024)] // 5 MB max per HEC request
    [ProducesResponseType(typeof(HecResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(HecResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ReceiveRaw(
        [FromQuery] string? host = null,
        [FromQuery] string? sourcetype = null,
        [FromQuery] string? index = null,
        [FromQuery] string? source = null,
        CancellationToken cancellationToken = default)
    {
        // Validate query parameter lengths to prevent abuse
        if (host?.Length > 256 || sourcetype?.Length > 256 || index?.Length > 256 || source?.Length > 1024)
        {
            return BadRequest(new HecResponse { Text = "Query parameter too long", Code = 5 });
        }

        // Defense-in-depth: enforce max body size at application level in addition to
        // the [RequestSizeLimit] attribute which operates at the Kestrel/middleware layer.
        const int MaxBodySize = 10 * 1024 * 1024; // 10 MB
        if (Request.ContentLength > MaxBodySize)
        {
            return BadRequest(new HecResponse { Text = "Payload too large", Code = 5 });
        }

        string body;
        using (var reader = new StreamReader(Request.Body))
        {
            body = await reader.ReadToEndAsync(cancellationToken);
        }

        // Post-read size check for chunked transfers where Content-Length is absent
        if (body.Length > MaxBodySize)
        {
            return BadRequest(new HecResponse { Text = "Payload too large", Code = 5 });
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return BadRequest(new HecResponse { Text = "No data", Code = 5 });
        }

        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var events = new List<LogEvent>(lines.Length);

        foreach (var line in lines)
        {
            var logEvent = await _lineParser.ParseAsync(line, sourcetype, cancellationToken: cancellationToken);
            logEvent.Host = host ?? logEvent.Host;
            logEvent.Index = index ?? "main";
            logEvent.SourceFile = source;

            events.Add(logEvent);
        }

        if (events.Count == 0)
        {
            return BadRequest(new HecResponse { Text = "No valid events", Code = 5 });
        }

        try
        {
            await _storage.StoreBatchAsync(events, cancellationToken);
            _logger.LogDebug("HEC raw ingested {Count} events", events.Count);

            // Invalidate cached query results so dashboards get fresh data
            var rawIndex = events.FirstOrDefault()?.Index ?? "main";
            _queryCache?.InvalidateIndex(rawIndex);

            await _auditLogger.LogAsync(new AuditEvent
            {
                PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
                Action = AuditAction.Ingest,
                ResourceType = "LogEvent",
                Description = $"HEC raw ingested {events.Count} events",
                SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
            });
            return Ok(new HecResponse { Text = "Success", Code = 0 });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store HEC raw events");
            return StatusCode(500, new HecResponse { Text = "Internal server error", Code = 8 });
        }
    }

    /// <summary>
    /// HEC health check endpoint (anonymous, no auth required).
    /// Returns 200 if the collector is available.
    /// </summary>
    [HttpGet("health")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(HecHealthResponse), StatusCodes.Status200OK)]
    public IActionResult Health()
    {
        return Ok(new HecHealthResponse { Text = "HEC is healthy", Code = 17 });
    }

    /// <summary>
    /// A sender can supply a stable ID so re-sending is safe: storage ignores IDs it already has.
    /// </summary>
    private static bool IsValidEventId(string? id) =>
        id is { Length: >= 16 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private async Task<LogEvent> ConvertHecEventAsync(HecEvent hecEvent, CancellationToken cancellationToken)
    {
        DateTime? sentTime = hecEvent.Time.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(hecEvent.Time.Value * 1000)).UtcDateTime
            : null;

        var rawLine = hecEvent.Event is JsonElement jsonElement
            ? jsonElement.ValueKind == JsonValueKind.String
                ? jsonElement.GetString() ?? string.Empty
                : jsonElement.GetRawText()
            : hecEvent.Event?.ToString() ?? string.Empty;

        // A timestamp in the line says when it happened. The sender's is when it was read.
        var logEvent = await _lineParser.ParseAsync(rawLine, hecEvent.SourceType, sentTime, cancellationToken);

        if (IsValidEventId(hecEvent.Id))
        {
            logEvent.Id = hecEvent.Id!;
        }

        logEvent.Host = hecEvent.Host ?? logEvent.Host;
        logEvent.Index = hecEvent.Index ?? "main";
        logEvent.SourceFile = hecEvent.Source;

        // Copy HEC fields metadata
        if (hecEvent.Fields != null)
        {
            foreach (var field in hecEvent.Fields)
            {
                logEvent.SetField(field.Key, field.Value);
            }
        }

        return logEvent;
    }

    private static readonly JsonSerializerOptions HecJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

// HEC DTOs

/// <summary>
/// Splunk HEC event format
/// </summary>
public class HecEvent
{
    /// <summary>
    /// Optional stable event ID, 16 to 64 letters, digits, hyphens or underscores. An event with
    /// an ID that's already stored is ignored, so senders can safely retry.
    /// </summary>
    public string? Id { get; set; }

    public object? Event { get; set; }
    public double? Time { get; set; }
    public string? Host { get; set; }
    public string? Source { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("sourcetype")]
    public string? SourceType { get; set; }

    public string? Index { get; set; }
    public Dictionary<string, object>? Fields { get; set; }
}

/// <summary>
/// Splunk-compatible HEC response
/// </summary>
public class HecResponse
{
    public string Text { get; set; } = string.Empty;
    public int Code { get; set; }
}

/// <summary>
/// HEC health response
/// </summary>
public class HecHealthResponse
{
    public string Text { get; set; } = string.Empty;
    public int Code { get; set; }
}
