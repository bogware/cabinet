using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Cabinet.API.Services;

/// <summary>
/// Reads OTLP traces and metrics into the same records as logs, so each span and each data point
/// becomes a searchable event. Field numbers come from opentelemetry-proto's trace.proto and
/// metrics.proto. Unknown fields are skipped.
/// </summary>
public static class OtlpSignalDecoder
{
    private static readonly string[] SpanKinds = ["unspecified", "internal", "server", "client", "producer", "consumer"];

    /// <summary>Decodes ExportTraceServiceRequest in protobuf.</summary>
    public static IReadOnlyList<OtlpLogRecord> DecodeTracesProtobuf(ReadOnlyMemory<byte> data)
    {
        var records = new List<OtlpLogRecord>();
        foreach (var resourceSpans in OtlpWire.Fields(data).Where(f => f.Field == 1))
        {
            var resource = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var part in OtlpWire.Fields(resourceSpans.Bytes))
            {
                if (part.Field == 1)
                {
                    OtlpWire.ReadAttributes(part.Bytes, 1, resource);
                }
                else if (part.Field == 2)
                {
                    foreach (var span in OtlpWire.Fields(part.Bytes).Where(f => f.Field == 2))
                    {
                        records.Add(SpanRecord(ReadSpan(span.Bytes), resource));
                    }
                }
            }
        }

        return records;
    }

    /// <summary>Decodes ExportTraceServiceRequest in JSON.</summary>
    public static IReadOnlyList<OtlpLogRecord> DecodeTracesJson(byte[] data)
    {
        using var document = JsonDocument.Parse(data);
        var records = new List<OtlpLogRecord>();
        foreach (var resourceSpans in Array(document.RootElement, "resourceSpans"))
        {
            var resource = ResourceAttributes(resourceSpans);
            foreach (var scope in Array(resourceSpans, "scopeSpans"))
            {
                foreach (var item in Array(scope, "spans"))
                {
                    var span = new Span
                    {
                        TraceId = Text(item, "traceId"),
                        SpanId = Text(item, "spanId"),
                        ParentSpanId = Text(item, "parentSpanId"),
                        Name = Text(item, "name") ?? string.Empty,
                        Kind = item.TryGetProperty("kind", out var kind)
                            ? kind.ValueKind == JsonValueKind.Number ? kind.GetInt32() : System.Array.IndexOf(SpanKinds, kind.GetString()?.Replace("SPAN_KIND_", string.Empty, StringComparison.Ordinal).ToLowerInvariant())
                            : 0,
                        Start = Number(item, "startTimeUnixNano"),
                        End = Number(item, "endTimeUnixNano"),
                    };
                    if (item.TryGetProperty("status", out var status))
                    {
                        span.StatusCode = (int)Number(status, "code");
                        span.StatusMessage = Text(status, "message");
                    }

                    if (item.TryGetProperty("attributes", out var attributes))
                    {
                        OtlpWire.ReadJsonAttributes(attributes, span.Attributes);
                    }

                    records.Add(SpanRecord(span, resource));
                }
            }
        }

        return records;
    }

    /// <summary>Decodes ExportMetricsServiceRequest in protobuf.</summary>
    public static IReadOnlyList<OtlpLogRecord> DecodeMetricsProtobuf(ReadOnlyMemory<byte> data)
    {
        var records = new List<OtlpLogRecord>();
        foreach (var resourceMetrics in OtlpWire.Fields(data).Where(f => f.Field == 1))
        {
            var resource = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var part in OtlpWire.Fields(resourceMetrics.Bytes))
            {
                if (part.Field == 1)
                {
                    OtlpWire.ReadAttributes(part.Bytes, 1, resource);
                }
                else if (part.Field == 2)
                {
                    foreach (var metric in OtlpWire.Fields(part.Bytes).Where(f => f.Field == 2))
                    {
                        records.AddRange(ReadMetric(metric.Bytes, resource));
                    }
                }
            }
        }

        return records;
    }

    /// <summary>Decodes ExportMetricsServiceRequest in JSON.</summary>
    public static IReadOnlyList<OtlpLogRecord> DecodeMetricsJson(byte[] data)
    {
        using var document = JsonDocument.Parse(data);
        var records = new List<OtlpLogRecord>();
        foreach (var resourceMetrics in Array(document.RootElement, "resourceMetrics"))
        {
            var resource = ResourceAttributes(resourceMetrics);
            foreach (var scope in Array(resourceMetrics, "scopeMetrics"))
            {
                foreach (var metric in Array(scope, "metrics"))
                {
                    var name = Text(metric, "name") ?? string.Empty;
                    var unit = Text(metric, "unit");
                    foreach (var (type, body) in new[] { ("gauge", "gauge"), ("sum", "sum"), ("histogram", "histogram"), ("exponential_histogram", "exponentialHistogram"), ("summary", "summary") })
                    {
                        if (!metric.TryGetProperty(body, out var content))
                        {
                            continue;
                        }

                        foreach (var point in Array(content, "dataPoints"))
                        {
                            var attributes = new Dictionary<string, object>(StringComparer.Ordinal);
                            if (point.TryGetProperty("attributes", out var pointAttributes))
                            {
                                OtlpWire.ReadJsonAttributes(pointAttributes, attributes);
                            }

                            double? value = point.TryGetProperty("asDouble", out var asDouble) ? asDouble.GetDouble()
                                : point.TryGetProperty("asInt", out var asInt) ? (asInt.ValueKind == JsonValueKind.String ? double.Parse(asInt.GetString()!, CultureInfo.InvariantCulture) : asInt.GetDouble())
                                : null;
                            double? count = point.TryGetProperty("count", out var c) ? (c.ValueKind == JsonValueKind.String ? double.Parse(c.GetString()!, CultureInfo.InvariantCulture) : c.GetDouble()) : null;
                            double? sum = point.TryGetProperty("sum", out var s) ? s.GetDouble() : null;
                            records.Add(MetricRecord(name, unit, type, Number(point, "timeUnixNano"), value, count, sum, attributes, resource));
                        }
                    }
                }
            }
        }

        return records;
    }

    private static Span ReadSpan(ReadOnlyMemory<byte> data)
    {
        var span = new Span();
        foreach (var (field, _, bytes, number) in OtlpWire.Fields(data))
        {
            switch (field)
            {
                case 1: span.TraceId = Convert.ToHexString(bytes.Span).ToLowerInvariant(); break;
                case 2: span.SpanId = Convert.ToHexString(bytes.Span).ToLowerInvariant(); break;
                case 4: span.ParentSpanId = bytes.Length == 0 ? null : Convert.ToHexString(bytes.Span).ToLowerInvariant(); break;
                case 5: span.Name = Encoding.UTF8.GetString(bytes.Span); break;
                case 6: span.Kind = (int)number; break;
                case 7: span.Start = number; break;
                case 8: span.End = number; break;
                case 9: OtlpWire.ReadKeyValue(bytes, span.Attributes); break;
                case 15:
                    foreach (var status in OtlpWire.Fields(bytes))
                    {
                        if (status.Field == 2) span.StatusMessage = Encoding.UTF8.GetString(status.Bytes.Span);
                        if (status.Field == 3) span.StatusCode = (int)status.Number;
                    }

                    break;
            }
        }

        return span;
    }

    private static IEnumerable<OtlpLogRecord> ReadMetric(ReadOnlyMemory<byte> data, Dictionary<string, object> resource)
    {
        var fields = OtlpWire.Fields(data);
        var name = fields.Where(f => f.Field == 1).Select(f => Encoding.UTF8.GetString(f.Bytes.Span)).FirstOrDefault() ?? string.Empty;
        var unit = fields.Where(f => f.Field == 3).Select(f => Encoding.UTF8.GetString(f.Bytes.Span)).FirstOrDefault();
        var types = new Dictionary<int, string> { [5] = "gauge", [7] = "sum", [9] = "histogram", [10] = "exponential_histogram", [11] = "summary" };

        foreach (var body in fields.Where(f => types.ContainsKey(f.Field)))
        {
            var type = types[body.Field];
            foreach (var point in OtlpWire.Fields(body.Bytes).Where(f => f.Field == 1))
            {
                var attributes = new Dictionary<string, object>(StringComparer.Ordinal);
                ulong time = 0;
                double? value = null, count = null, sum = null;
                foreach (var (field, wire, bytes, number) in OtlpWire.Fields(point.Bytes))
                {
                    // Attribute and value fields differ by point type
                    var attributeField = type is "histogram" ? 9 : type is "exponential_histogram" ? 1 : 7;
                    if (field == attributeField && wire == 2)
                    {
                        OtlpWire.ReadKeyValue(bytes, attributes);
                    }
                    else if (field == 3)
                    {
                        time = number;
                    }
                    else if (type is "gauge" or "sum")
                    {
                        if (field == 4) value = BitConverter.UInt64BitsToDouble(number);
                        if (field == 6) value = (long)number;
                    }
                    else if (field == 4)
                    {
                        count = number;
                    }
                    else if (field == 5)
                    {
                        sum = BitConverter.UInt64BitsToDouble(number);
                    }
                }

                yield return MetricRecord(name, unit, type, time, value, count, sum, attributes, resource);
            }
        }
    }

    private static OtlpLogRecord SpanRecord(Span span, Dictionary<string, object> resource)
    {
        var durationMs = span.End > span.Start ? (span.End - span.Start) / 1_000_000.0 : 0;
        var status = span.StatusCode switch { 1 => "ok", 2 => "error", _ => "unset" };
        var record = new OtlpLogRecord
        {
            TimeUnixNano = span.Start,
            SeverityNumber = span.StatusCode == 2 ? 17 : 9,
            Body = $"span {span.Name} {durationMs.ToString("0.###", CultureInfo.InvariantCulture)}ms {status}{(string.IsNullOrEmpty(span.StatusMessage) ? string.Empty : ": " + span.StatusMessage)}",
            TraceId = span.TraceId,
            SpanId = span.SpanId,
            DefaultIndex = "traces",
        };
        Merge(record, resource, span.Attributes);
        record.Attributes["span.name"] = span.Name;
        record.Attributes["span.kind"] = span.Kind >= 0 && span.Kind < SpanKinds.Length ? SpanKinds[span.Kind] : "unspecified";
        record.Attributes["duration_ms"] = durationMs;
        record.Attributes["status"] = status;
        if (!string.IsNullOrEmpty(span.ParentSpanId))
        {
            record.Attributes["parent_span_id"] = span.ParentSpanId;
        }

        return record;
    }

    private static OtlpLogRecord MetricRecord(
        string name, string? unit, string type, ulong time, double? value, double? count, double? sum,
        Dictionary<string, object> attributes, Dictionary<string, object> resource)
    {
        var shown = value ?? (count is > 0 && sum.HasValue ? sum / count : sum);
        var record = new OtlpLogRecord
        {
            TimeUnixNano = time,
            SeverityNumber = 9,
            Body = $"metric {name}={(shown?.ToString("0.######", CultureInfo.InvariantCulture) ?? "-")}{(string.IsNullOrEmpty(unit) ? string.Empty : " " + unit)}",
            DefaultIndex = "metrics",
        };
        Merge(record, resource, attributes);
        record.Attributes["metric.name"] = name;
        record.Attributes["metric.type"] = type;
        if (!string.IsNullOrEmpty(unit)) record.Attributes["metric.unit"] = unit;
        if (value.HasValue) record.Attributes["value"] = value.Value;
        if (count.HasValue) record.Attributes["count"] = count.Value;
        if (sum.HasValue) record.Attributes["sum"] = sum.Value;
        return record;
    }

    private static void Merge(OtlpLogRecord record, Dictionary<string, object> resource, Dictionary<string, object> own)
    {
        foreach (var (key, value) in resource) record.Attributes[key] = value;
        foreach (var (key, value) in own) record.Attributes[key] = value;
    }

    private static Dictionary<string, object> ResourceAttributes(JsonElement item)
    {
        var attributes = new Dictionary<string, object>(StringComparer.Ordinal);
        if (item.TryGetProperty("resource", out var resource) && resource.TryGetProperty("attributes", out var list))
        {
            OtlpWire.ReadJsonAttributes(list, attributes);
        }

        return attributes;
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static ulong Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind == JsonValueKind.String ? ulong.Parse(value.GetString()!, CultureInfo.InvariantCulture) : value.GetUInt64();
    }

    private sealed class Span
    {
        public string? TraceId { get; set; }
        public string? SpanId { get; set; }
        public string? ParentSpanId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Kind { get; set; }
        public ulong Start { get; set; }
        public ulong End { get; set; }
        public int StatusCode { get; set; }
        public string? StatusMessage { get; set; }
        public Dictionary<string, object> Attributes { get; } = new(StringComparer.Ordinal);
    }
}
