using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Cabinet.API.Authentication;
using Cabinet.API.Middleware;
using Cabinet.API.Services;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Core.Utilities;
using Cabinet.Parsers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cabinet.API.Controllers;

/// <summary>
/// OpenTelemetry logs over HTTP: point an OTLP/HTTP exporter or collector here. Accepts protobuf
/// and JSON, compressed with gzip or not. A retried export stores nothing twice, since an event's
/// ID comes from its content.
/// </summary>
[ApiController]
[Route("v1")]
[Authorize]
public class OtlpLogsController : ControllerBase
{
    // A compressed body can expand a lot. Real exports are a few megabytes.
    private readonly long _maxDecompressedBytes;

    private readonly ILogStorage _storage;
    private readonly LogLineParser _lineParser;
    private readonly IndexAccessScope? _indexScope;

    public OtlpLogsController(ILogStorage storage, LogLineParser lineParser, IndexAccessScope? indexScope = null, Infrastructure.ServerLimits? limits = null)
    {
        _maxDecompressedBytes = (limits ?? new Infrastructure.ServerLimits()).OtlpMaxDecompressedMB * Infrastructure.ServerLimits.Megabyte;
        _storage = storage;
        _lineParser = lineParser;
        _indexScope = indexScope;
    }

    /// <summary>
    /// Receive logs. The index is the <c>X-Cabinet-Index</c> header, or a <c>cabinet.index</c>
    /// attribute on the record or its resource, or <c>main</c>.
    /// </summary>
    [HttpPost("logs")]
    [RequireCapability(Capability.Ingest)]
    [ConfiguredRequestSizeLimit(RequestSizeLimitKind.Otlp)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    public Task<IActionResult> Export(CancellationToken cancellationToken = default) =>
        ReceiveAsync(OtlpLogDecoder.DecodeProtobuf, OtlpLogDecoder.DecodeJson, cancellationToken);

    /// <summary>
    /// Receive traces. Each span becomes an event in index <c>traces</c>, unless the request names another.
    /// </summary>
    [HttpPost("traces")]
    [RequireCapability(Capability.Ingest)]
    [ConfiguredRequestSizeLimit(RequestSizeLimitKind.Otlp)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    public Task<IActionResult> ExportTraces(CancellationToken cancellationToken = default) =>
        ReceiveAsync(OtlpSignalDecoder.DecodeTracesProtobuf, OtlpSignalDecoder.DecodeTracesJson, cancellationToken);

    /// <summary>
    /// Receive metrics. Each data point becomes an event in index <c>metrics</c>, unless the request names another.
    /// </summary>
    [HttpPost("metrics")]
    [RequireCapability(Capability.Ingest)]
    [ConfiguredRequestSizeLimit(RequestSizeLimitKind.Otlp)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    public Task<IActionResult> ExportMetrics(CancellationToken cancellationToken = default) =>
        ReceiveAsync(OtlpSignalDecoder.DecodeMetricsProtobuf, OtlpSignalDecoder.DecodeMetricsJson, cancellationToken);

    private async Task<IActionResult> ReceiveAsync(
        Func<ReadOnlyMemory<byte>, IReadOnlyList<OtlpLogRecord>> decodeProtobuf,
        Func<byte[], IReadOnlyList<OtlpLogRecord>> decodeJson,
        CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(cancellationToken);
        if (body == null)
        {
            return Problem(detail: "The request is too large once decompressed", statusCode: 413);
        }

        var contentType = Request.ContentType?.Split(';')[0].Trim().ToLowerInvariant();
        IReadOnlyList<OtlpLogRecord> records;
        try
        {
            records = contentType switch
            {
                "application/x-protobuf" => decodeProtobuf(body),
                "application/json" => decodeJson(body),
                _ => throw new NotSupportedException(),
            };
        }
        catch (NotSupportedException)
        {
            return Problem(detail: "Send application/x-protobuf or application/json", statusCode: 415);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or InvalidDataException)
        {
            return Problem(detail: $"Not a valid OTLP request: {ex.Message}", statusCode: 400);
        }

        var headerIndex = Request.Headers["X-Cabinet-Index"].FirstOrDefault();
        var events = new List<LogEvent>(records.Count);
        foreach (var record in records)
        {
            var logEvent = await ToEventAsync(record, headerIndex, cancellationToken);
            if (_indexScope?.Principal != null && !_indexScope.Can(Capability.Ingest, logEvent.Index))
            {
                return Problem(detail: $"Not allowed to ingest into index '{logEvent.Index}'", statusCode: 403);
            }

            events.Add(logEvent);
        }

        if (events.Count > 0)
        {
            using var span = CabinetDiagnostics.Source.StartActivity("ingest.store");
            span?.SetTag("cabinet.events", events.Count);
            await _storage.StoreBatchAsync(events, cancellationToken);
        }

        // An empty ExportLogsServiceResponse is an empty message in protobuf, and {} in JSON
        return contentType == "application/json"
            ? Content("{}", "application/json")
            : File(Array.Empty<byte>(), "application/x-protobuf");
    }

    private async Task<byte[]?> ReadBodyAsync(CancellationToken cancellationToken)
    {
        Stream source = Request.Body;
        await using var gzip = string.Equals(Request.Headers.ContentEncoding, "gzip", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(Request.Body, CompressionMode.Decompress)
            : null;
        source = gzip ?? source;

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > _maxDecompressedBytes)
            {
                return null;
            }
        }

        return buffer.ToArray();
    }

    private async Task<LogEvent> ToEventAsync(OtlpLogRecord record, string? headerIndex, CancellationToken cancellationToken)
    {
        var time = record.TimeUnixNano > 0
            ? DateTimeOffset.UnixEpoch.AddTicks((long)(record.TimeUnixNano / 100)).UtcDateTime
            : (DateTime?)null;

        // The line may hold fields worth having, though the record's own values come first
        var logEvent = await _lineParser.ParseAsync(record.Body, fallbackTime: time, cancellationToken: cancellationToken);
        if (time != null)
        {
            logEvent.Timestamp = time.Value;
        }

        if (record.SeverityNumber > 0)
        {
            logEvent.Severity = FromSeverityNumber(record.SeverityNumber);
        }
        else if (!string.IsNullOrEmpty(record.SeverityText) && LogSeverities.TryParse(record.SeverityText, out var named))
        {
            logEvent.Severity = named;
        }

        logEvent.Id = StableId(record);
        logEvent.SourceType = record.Attribute("service.name") ?? "otlp";
        logEvent.Host = record.Attribute("host.name") ?? logEvent.Host;
        logEvent.Index = headerIndex ?? record.Attribute("cabinet.index") ?? record.DefaultIndex;
        foreach (var (key, value) in record.Attributes)
        {
            logEvent.SetField(key, value);
        }

        if (!string.IsNullOrEmpty(record.TraceId))
        {
            logEvent.SetField("trace_id", record.TraceId);
        }

        if (!string.IsNullOrEmpty(record.SpanId))
        {
            logEvent.SetField("span_id", record.SpanId);
        }

        return logEvent;
    }

    /// <summary>OTLP's numbers run 1 to 24, in fours: trace, debug, info, warn, error, fatal.</summary>
    private static LogSeverity FromSeverityNumber(int number) => number switch
    {
        <= 4 => LogSeverity.Trace,
        <= 8 => LogSeverity.Debug,
        <= 12 => LogSeverity.Info,
        <= 16 => LogSeverity.Warning,
        <= 20 => LogSeverity.Error,
        _ => LogSeverity.Fatal,
    };

    /// <summary>From the record's content, so that an exporter's retry can't store it twice.</summary>
    private static string StableId(OtlpLogRecord record)
    {
        var material = new StringBuilder()
            .Append(record.TimeUnixNano).Append('|')
            .Append(record.ObservedTimeUnixNano).Append('|')
            .Append(record.TraceId).Append('|')
            .Append(record.SpanId).Append('|')
            .Append(record.Body);
        foreach (var (key, value) in record.Attributes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            material.Append('|').Append(key).Append('=').Append(value);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()));
        return $"otlp-{Convert.ToHexString(hash)[..32].ToLowerInvariant()}";
    }
}
