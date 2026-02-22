using System.Collections.Concurrent;
using Cabinet.Core.Utilities;

namespace Cabinet.Core.Models;

/// <summary>
/// Represents a normalized log event extracted from any log source
/// </summary>
public sealed class LogEvent
{
    public LogEvent()
    {
        Fields = new ConcurrentDictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Unique identifier for this log event.
    /// Uses ULID (Universally Unique Lexicographically Sortable Identifier) for time-ordered sorting.
    /// </summary>
    public string Id { get; set; } = Ulid.NewUlid();

    /// <summary>
    /// When the event occurred (extracted from the log line by the parser)
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the event was indexed/stored (set at ingestion time).
    /// Analogous to Splunk's _indextime — enables searches like "indexed in the last hour".
    /// </summary>
    public DateTime IndexTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Logical index this event belongs to (e.g., "main", "gameserver", "security").
    /// Analogous to Splunk's index concept — enables per-index queries and permissions.
    /// </summary>
    public string Index { get; set; } = "main";

    /// <summary>
    /// Source type as a free-form string (e.g., "SteamCMD", "SourceMod", "L4D2", "Generic", "CloudWatch", etc.)
    /// Converted from enum to string for extensibility — any log source can define its own type.
    /// </summary>
    public string SourceType { get; set; } = SourceTypes.Generic;

    /// <summary>
    /// Host identifier (server hostname, container ID, instance name).
    /// Renamed from ServerId for general-purpose naming consistent with Splunk/ELK.
    /// </summary>
    public string? Host { get; set; }

    /// <summary>
    /// Log severity level
    /// </summary>
    public LogSeverity Severity { get; set; } = LogSeverity.Info;

    /// <summary>
    /// Original unparsed log line
    /// </summary>
    public required string RawLine { get; set; }

    /// <summary>
    /// Source file path
    /// </summary>
    public string? SourceFile { get; set; }

    /// <summary>
    /// Line number in source file
    /// </summary>
    public long LineNumber { get; set; }

    /// <summary>
    /// Extracted structured fields (e.g., SteamID, PlayerName, Map, etc.)
    /// </summary>
    public ConcurrentDictionary<string, object?> Fields { get; set; }

    /// <summary>
    /// Tags for categorization and filtering.
    /// For concurrent access, use AddTag/HasTag/GetTagSnapshot which are lock-protected.
    /// Direct property access is fine during single-threaded parsing/construction.
    /// </summary>
    public HashSet<string> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _tagsLock = new();

    /// <summary>Thread-safe tag add (use for concurrent scenarios)</summary>
    public void AddTag(string tag) { lock (_tagsLock) Tags.Add(tag); }

    /// <summary>Thread-safe tag check (use for concurrent scenarios)</summary>
    public bool HasTag(string tag) { lock (_tagsLock) return Tags.Contains(tag); }

    /// <summary>Thread-safe snapshot of all tags (use for concurrent iteration)</summary>
    public string[] GetTagSnapshot() { lock (_tagsLock) return Tags.ToArray(); }

    /// <summary>
    /// Parser that processed this event
    /// </summary>
    public string? ParserName { get; set; }

    /// <summary>
    /// Add or update a field value
    /// </summary>
    public void SetField(string key, object? value)
    {
        Fields[key] = value;
    }

    /// <summary>
    /// Get a field value with type safety
    /// </summary>
    public T? GetField<T>(string key)
    {
        if (Fields.TryGetValue(key, out var value) && value is T typedValue)
        {
            return typedValue;
        }
        return default;
    }

    /// <summary>
    /// Get a field value as string
    /// </summary>
    public string? GetFieldString(string key)
    {
        if (Fields.TryGetValue(key, out var value))
        {
            return value?.ToString();
        }
        return null;
    }
}

/// <summary>
/// Well-known source type constants. Use these instead of the deprecated SourceType enum.
/// Custom parsers can define any string value — not limited to these constants.
/// </summary>
public static class SourceTypes
{
    public const string Generic = "Generic";
    public const string SteamCMD = "SteamCMD";
    public const string SourceMod = "SourceMod";
    public const string L4D2 = "L4D2";
    public const string Custom = "Custom";
    public const string CloudWatch = "CloudWatch";
    public const string CloudTrail = "CloudTrail";
    public const string Json = "JSON";
    public const string Csv = "CSV";
    public const string Cef = "CEF";
    public const string Apache = "Apache";
    public const string Nginx = "Nginx";
}

/// <summary>
/// Deprecated: Use string-based SourceTypes constants instead.
/// Kept temporarily for backward compatibility during migration.
/// </summary>
[Obsolete("Use string-based SourceTypes constants instead (e.g., SourceTypes.Generic)")]
public enum SourceType
{
    Generic,
    SteamCMD,
    SourceMod,
    L4D2,
    Custom
}

public enum LogSeverity
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4,
    Critical = 5,
    Fatal = 6
}