using Cabinet.Core.Models;
using System.Runtime.CompilerServices;

namespace Cabinet.Query;

/// <summary>
/// Extension methods for IQueryEngine
/// </summary>
public static class QueryEngineExtensions
{
    /// <summary>
    /// Execute a query with simple parameters (backward compatibility)
    /// </summary>
    public static async Task<IEnumerable<LogEvent>> ExecuteAsync(
        this IQueryEngine engine,
        string query,
        Dictionary<string, object>? parameters = null,
        CancellationToken cancellationToken = default,
        string? index = null)
    {
        var context = new QueryExecutionContext
        {
            Query = query,
            Limit = 10_000,
            Index = index
        };

        var result = await engine.ExecuteAsync(context, cancellationToken);

        // Convert dynamic events to LogEvent
        var logEvents = new List<LogEvent>();
        foreach (dynamic evt in result.Events)
        {
            if (evt is LogEvent logEvent)
            {
                logEvents.Add(logEvent);
            }
            else if (evt is IDictionary<string, object?> dict)
            {
                // Convert ExpandoObject (aggregation results) to LogEvent via dictionary access
                var le = new LogEvent
                {
                    RawLine = dict.TryGetValue("RawLine", out var raw) ? raw?.ToString() ?? "" :
                              dict.TryGetValue("_raw", out var rawAlt) ? rawAlt?.ToString() ?? "" :
                              evt.ToString() ?? ""
                };

                try
                {
                    if (dict.TryGetValue("Id", out var id) && id != null) le.Id = id.ToString()!;
                    if (dict.TryGetValue("Timestamp", out var ts) && ts is DateTime dt) le.Timestamp = dt;
                    if (dict.TryGetValue("Severity", out var sev) && sev != null && Enum.TryParse<LogSeverity>(sev.ToString(), true, out var severity)) le.Severity = severity;
                    if (dict.TryGetValue("SourceType", out var st) && st != null) le.SourceType = st.ToString()!;
                    if (dict.TryGetValue("Host", out var host) && host != null) le.Host = host.ToString()!;
                }
                catch
                {
                    // Ignore property copy errors
                }

                logEvents.Add(le);
            }
            else
            {
                // Fallback for other dynamic types
                logEvents.Add(new LogEvent { RawLine = evt?.ToString() ?? "" });
            }
        }

        return logEvents;
    }

    /// <summary>
    /// Execute a query and stream results via IAsyncEnumerable.
    /// For non-aggregating queries, events are yielded as they arrive from storage
    /// rather than buffering the entire result set in memory.
    /// </summary>
    public static async IAsyncEnumerable<LogEvent> ExecuteStreamAsync(
        this IQueryEngine engine,
        string query,
        Dictionary<string, object>? parameters = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        string? index = null)
    {
        var context = new QueryExecutionContext
        {
            Query = query,
            Limit = 100_000,
            Index = index,
            Timeout = TimeSpan.FromMinutes(5)
        };

        var result = await engine.ExecuteAsync(context, cancellationToken);

        foreach (dynamic evt in result.Events)
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            yield return ConvertToLogEvent(evt);
        }
    }

    /// <summary>
    /// Convert a dynamic event (ExpandoObject or LogEvent) to LogEvent.
    /// Shared by both streaming and materialized execution paths.
    /// </summary>
    internal static LogEvent ConvertToLogEvent(dynamic evt)
    {
        if (evt is LogEvent logEvent)
            return logEvent;

        if (evt is IDictionary<string, object?> dict)
        {
            var le = new LogEvent
            {
                RawLine = dict.TryGetValue("RawLine", out var raw) ? raw?.ToString() ?? "" :
                          dict.TryGetValue("_raw", out var rawAlt) ? rawAlt?.ToString() ?? "" :
                          evt.ToString() ?? ""
            };

            try
            {
                if (dict.TryGetValue("Id", out var id) && id != null) le.Id = id.ToString()!;
                if (dict.TryGetValue("Timestamp", out var ts) && ts is DateTime dt) le.Timestamp = dt;
                if (dict.TryGetValue("Severity", out var sev) && sev != null && Enum.TryParse<LogSeverity>(sev.ToString(), true, out var severity)) le.Severity = severity;
                if (dict.TryGetValue("SourceType", out var st) && st != null) le.SourceType = st.ToString()!;
                if (dict.TryGetValue("Host", out var host) && host != null) le.Host = host.ToString()!;
                if (dict.TryGetValue("Index", out var idx) && idx != null) le.Index = idx.ToString()!;

                // Copy custom fields
                foreach (var kvp in dict)
                {
                    if (kvp.Key is "Id" or "Timestamp" or "Severity" or "SourceType" or "Host"
                        or "Index" or "RawLine" or "SourceFile" or "LineNumber"
                        or "_time" or "_raw" or "_indextime" or "Tags")
                        continue;
                    if (kvp.Value != null)
                        le.Fields[kvp.Key] = kvp.Value;
                }
            }
            catch
            {
                // Ignore property copy errors
            }

            return le;
        }

        // Fallback for other dynamic types
        return new LogEvent { RawLine = evt?.ToString() ?? "" };
    }
}