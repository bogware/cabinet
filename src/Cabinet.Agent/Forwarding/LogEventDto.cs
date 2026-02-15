using System.Text.Json.Serialization;

namespace Cabinet.Agent.Forwarding;

/// <summary>
/// Lightweight log event DTO sent to the Cabinet API.
/// Matches the Splunk HEC JSON format for compatibility with the /services/collector/event endpoint.
/// </summary>
public sealed class LogEventDto
{
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
    /// Line number in the source file.
    /// </summary>
    [JsonIgnore]
    public long LineNumber { get; set; }
}
