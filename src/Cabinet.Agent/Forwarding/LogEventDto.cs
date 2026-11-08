using System.Text.Json.Serialization;

namespace Cabinet.Agent.Forwarding;

/// <summary>
/// Lightweight log event DTO sent to the Cabinet API.
/// Matches the Splunk HEC JSON format for compatibility with the /services/collector/event endpoint.
/// </summary>
public sealed class LogEventDto
{
    /// <summary>
    /// Stable ID for this event. The server ignores an ID it already has, so re-sending is safe.
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// The file contents this event was read from. Not sent to the server.
    /// </summary>
    [JsonIgnore]
    public string? FileKey { get; set; }

    /// <summary>
    /// Offset where this event starts in its file.
    /// </summary>
    [JsonIgnore]
    public long StartOffset { get; set; }

    /// <summary>
    /// Offset just past this event in its file, where the checkpoint can move once it's durable.
    /// </summary>
    [JsonIgnore]
    public long EndOffset { get; set; }

    /// <summary>
    /// When the event occurred.
    /// </summary>
    [JsonIgnore]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Epoch time with millisecond precision (Splunk HEC format).
    /// </summary>
    [JsonPropertyName("time")]
    public double Time => new DateTimeOffset(Timestamp).ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>
    /// The raw log event data.
    /// </summary>
    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    /// <summary>
    /// Host that generated the event.
    /// </summary>
    [JsonPropertyName("host")]
    public string? Host { get; set; }

    /// <summary>
    /// Source file path.
    /// </summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>
    /// Source type classification.
    /// </summary>
    [JsonPropertyName("sourcetype")]
    public string? SourceType { get; set; }

    /// <summary>
    /// Target index.
    /// </summary>
    [JsonPropertyName("index")]
    public string? Index { get; set; }

    /// <summary>
    /// This host's time zone, so the server can place times written without an offset.
    /// </summary>
    [JsonPropertyName("tz")]
    public string? TimeZone { get; set; }

    /// <summary>
    /// Line number in the source file.
    /// </summary>
    [JsonIgnore]
    public long LineNumber { get; set; }
}
