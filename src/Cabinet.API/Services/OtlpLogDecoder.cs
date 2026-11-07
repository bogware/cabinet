using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Cabinet.API.Services;

/// <summary>One log record from an OTLP export, with its resource's attributes merged in.</summary>
public sealed class OtlpLogRecord
{
    public ulong TimeUnixNano { get; set; }
    public ulong ObservedTimeUnixNano { get; set; }
    public int SeverityNumber { get; set; }
    public string? SeverityText { get; set; }
    public string Body { get; set; } = string.Empty;
    public string? TraceId { get; set; }
    public string? SpanId { get; set; }
    public Dictionary<string, object> Attributes { get; } = new(StringComparer.Ordinal);

    /// <summary>An attribute's value, as text.</summary>
    public string? Attribute(string name) => Attributes.TryGetValue(name, out var value) ? value.ToString() : null;
}

/// <summary>
/// Reads OTLP's ExportLogsServiceRequest. The protobuf reader only needs the wire format and the
/// field numbers from opentelemetry-proto's logs.proto and common.proto, which saves a dependency
/// and generated code. Unknown fields are skipped, so newer senders still work.
/// </summary>
public static class OtlpLogDecoder
{
    private const int WireVarint = 0;
    private const int WireFixed64 = 1;
    private const int WireLengthDelimited = 2;
    private const int WireFixed32 = 5;

    /// <summary>Decodes the protobuf encoding.</summary>
    /// <exception cref="FormatException">The data isn't valid.</exception>
    public static IReadOnlyList<OtlpLogRecord> DecodeProtobuf(ReadOnlyMemory<byte> data)
    {
        var records = new List<OtlpLogRecord>();
        foreach (var (field, _, value, _) in Fields(data))
        {
            if (field == 1)
            {
                ReadResourceLogs(value, records);
            }
        }

        return records;
    }

    /// <summary>Decodes the JSON encoding.</summary>
    /// <exception cref="JsonException">The data isn't valid JSON.</exception>
    public static IReadOnlyList<OtlpLogRecord> DecodeJson(byte[] data)
    {
        using var document = JsonDocument.Parse(data);
        var records = new List<OtlpLogRecord>();
        if (!document.RootElement.TryGetProperty("resourceLogs", out var resourceLogs))
        {
            return records;
        }

        foreach (var resourceLog in resourceLogs.EnumerateArray())
        {
            var resourceAttributes = new Dictionary<string, object>(StringComparer.Ordinal);
            if (resourceLog.TryGetProperty("resource", out var resource) && resource.TryGetProperty("attributes", out var attributes))
            {
                ReadJsonAttributes(attributes, resourceAttributes);
            }

            if (!resourceLog.TryGetProperty("scopeLogs", out var scopeLogs))
            {
                continue;
            }

            foreach (var scopeLog in scopeLogs.EnumerateArray())
            {
                if (!scopeLog.TryGetProperty("logRecords", out var logRecords))
                {
                    continue;
                }

                foreach (var item in logRecords.EnumerateArray())
                {
                    var record = new OtlpLogRecord
                    {
                        TimeUnixNano = JsonNumber(item, "timeUnixNano"),
                        ObservedTimeUnixNano = JsonNumber(item, "observedTimeUnixNano"),
                        SeverityNumber = (int)JsonNumber(item, "severityNumber"),
                        SeverityText = item.TryGetProperty("severityText", out var text) ? text.GetString() : null,
                        Body = item.TryGetProperty("body", out var body) ? JsonAnyValue(body)?.ToString() ?? string.Empty : string.Empty,
                        TraceId = item.TryGetProperty("traceId", out var traceId) ? traceId.GetString() : null,
                        SpanId = item.TryGetProperty("spanId", out var spanId) ? spanId.GetString() : null,
                    };

                    foreach (var (key, value) in resourceAttributes)
                    {
                        record.Attributes[key] = value;
                    }

                    if (item.TryGetProperty("attributes", out var recordAttributes))
                    {
                        ReadJsonAttributes(recordAttributes, record.Attributes);
                    }

                    records.Add(record);
                }
            }
        }

        return records;
    }

    private static void ReadResourceLogs(ReadOnlyMemory<byte> data, List<OtlpLogRecord> records)
    {
        var resourceAttributes = new Dictionary<string, object>(StringComparer.Ordinal);
        var scopes = new List<ReadOnlyMemory<byte>>();
        foreach (var (field, _, value, _) in Fields(data))
        {
            if (field == 1)
            {
                foreach (var (resourceField, _, attribute, _) in Fields(value))
                {
                    if (resourceField == 1)
                    {
                        ReadKeyValue(attribute, resourceAttributes);
                    }
                }
            }
            else if (field == 2)
            {
                scopes.Add(value);
            }
        }

        foreach (var scope in scopes)
        {
            foreach (var (field, _, value, _) in Fields(scope))
            {
                if (field == 2)
                {
                    records.Add(ReadLogRecord(value, resourceAttributes));
                }
            }
        }
    }

    private static OtlpLogRecord ReadLogRecord(ReadOnlyMemory<byte> data, Dictionary<string, object> resourceAttributes)
    {
        var record = new OtlpLogRecord();
        foreach (var (key, value) in resourceAttributes)
        {
            record.Attributes[key] = value;
        }

        foreach (var (field, _, bytes, number) in Fields(data))
        {
            switch (field)
            {
                case 1:
                    record.TimeUnixNano = number;
                    break;
                case 2:
                    record.SeverityNumber = (int)number;
                    break;
                case 3:
                    record.SeverityText = Encoding.UTF8.GetString(bytes.Span);
                    break;
                case 5:
                    record.Body = ReadAnyValue(bytes)?.ToString() ?? string.Empty;
                    break;
                case 6:
                    ReadKeyValue(bytes, record.Attributes);
                    break;
                case 9:
                    record.TraceId = Convert.ToHexString(bytes.Span).ToLowerInvariant();
                    break;
                case 10:
                    record.SpanId = Convert.ToHexString(bytes.Span).ToLowerInvariant();
                    break;
                case 11:
                    record.ObservedTimeUnixNano = number;
                    break;
            }
        }

        return record;
    }

    private static void ReadKeyValue(ReadOnlyMemory<byte> data, Dictionary<string, object> target)
    {
        string? key = null;
        object? value = null;
        foreach (var (field, _, bytes, _) in Fields(data))
        {
            if (field == 1)
            {
                key = Encoding.UTF8.GetString(bytes.Span);
            }
            else if (field == 2)
            {
                value = ReadAnyValue(bytes);
            }
        }

        if (key != null && value != null)
        {
            target[key] = value;
        }
    }

    private static object? ReadAnyValue(ReadOnlyMemory<byte> data)
    {
        foreach (var (field, _, bytes, number) in Fields(data))
        {
            switch (field)
            {
                case 1:
                    return Encoding.UTF8.GetString(bytes.Span);
                case 2:
                    return number != 0;
                case 3:
                    return (long)number;
                case 4:
                    return BitConverter.UInt64BitsToDouble(number);
                case 5:
                    return string.Join(", ", Fields(bytes).Where(item => item.Field == 1).Select(item => ReadAnyValue(item.Bytes)));
                case 6:
                {
                    var map = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var item in Fields(bytes).Where(item => item.Field == 1))
                    {
                        ReadKeyValue(item.Bytes, map);
                    }

                    return JsonSerializer.Serialize(map);
                }
                case 7:
                    return Convert.ToHexString(bytes.Span).ToLowerInvariant();
            }
        }

        return null;
    }

    private static List<(int Field, int Wire, ReadOnlyMemory<byte> Bytes, ulong Number)> Fields(ReadOnlyMemory<byte> data)
    {
        var fields = new List<(int, int, ReadOnlyMemory<byte>, ulong)>();
        var position = 0;
        while (position < data.Length)
        {
            var tag = ReadVarint(data.Span, ref position);
            var field = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            switch (wire)
            {
                case WireVarint:
                    fields.Add((field, wire, default, ReadVarint(data.Span, ref position)));
                    break;
                case WireFixed64:
                    Require(data, position, 8);
                    fields.Add((field, wire, default, BitConverter.ToUInt64(data.Span.Slice(position, 8))));
                    position += 8;
                    break;
                case WireLengthDelimited:
                    var length = ReadVarint(data.Span, ref position);
                    if (length > (ulong)(data.Length - position))
                    {
                        throw new FormatException("A field runs past the end of the message");
                    }

                    fields.Add((field, wire, data.Slice(position, (int)length), 0));
                    position += (int)length;
                    break;
                case WireFixed32:
                    Require(data, position, 4);
                    fields.Add((field, wire, default, BitConverter.ToUInt32(data.Span.Slice(position, 4))));
                    position += 4;
                    break;
                default:
                    throw new FormatException($"Unknown wire type {wire}");
            }
        }

        return fields;
    }

    private static void Require(ReadOnlyMemory<byte> data, int position, int count)
    {
        if (position + count > data.Length)
        {
            throw new FormatException("A field runs past the end of the message");
        }
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int position)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (position >= data.Length)
            {
                throw new FormatException("A number runs past the end of the message");
            }

            var current = data[position++];
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return value;
            }
        }

        throw new FormatException("A number is too long");
    }

    private static void ReadJsonAttributes(JsonElement attributes, Dictionary<string, object> target)
    {
        foreach (var attribute in attributes.EnumerateArray())
        {
            if (attribute.TryGetProperty("key", out var key) && key.GetString() is { } name
                && attribute.TryGetProperty("value", out var value) && JsonAnyValue(value) is { } parsed)
            {
                target[name] = parsed;
            }
        }
    }

    private static object? JsonAnyValue(JsonElement value)
    {
        if (value.TryGetProperty("stringValue", out var text))
        {
            return text.GetString();
        }

        if (value.TryGetProperty("intValue", out var integer))
        {
            return integer.ValueKind == JsonValueKind.String
                ? long.Parse(integer.GetString()!, CultureInfo.InvariantCulture)
                : integer.GetInt64();
        }

        if (value.TryGetProperty("doubleValue", out var number))
        {
            return number.GetDouble();
        }

        if (value.TryGetProperty("boolValue", out var boolean))
        {
            return boolean.GetBoolean();
        }

        return value.GetRawText();
    }

    private static ulong JsonNumber(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
        {
            return 0;
        }

        // 64-bit numbers are strings in OTLP JSON, since JavaScript can't hold them
        return value.ValueKind == JsonValueKind.String
            ? ulong.Parse(value.GetString()!, CultureInfo.InvariantCulture)
            : value.GetUInt64();
    }
}
