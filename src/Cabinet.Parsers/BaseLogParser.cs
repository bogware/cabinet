using System.Globalization;
using System.Text.RegularExpressions;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cabinet.Parsers;

/// <summary>
/// Base class for log parsers with common functionality
/// </summary>
public abstract partial class BaseLogParser : ILogParser
{
    protected static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    protected readonly ILogger _logger;

    protected BaseLogParser(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public abstract string Name { get; }
    public abstract string SourceType { get; }

    public abstract bool CanParse(string line);

    /// <summary>Maximum line length to parse (1 MB). Lines exceeding this are truncated to prevent DoS.</summary>
    protected const int MaxLineLength = 1_048_576;

    public virtual async Task<LogEvent?> ParseAsync(string line, string? sourceFile = null, long lineNumber = 0, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        // Truncate extremely long lines to prevent DoS via regex processing
        if (line.Length > MaxLineLength)
        {
            _logger.LogWarning("Truncating line {LineNumber} from {File}: {Length} chars exceeds max {Max}",
                lineNumber, sourceFile, line.Length, MaxLineLength);
            line = line[..MaxLineLength];
        }

        try
        {
            var logEvent = new LogEvent
            {
                RawLine = line,
                SourceFile = sourceFile,
                LineNumber = lineNumber,
                SourceType = SourceType,
                ParserName = Name
            };

            // Extract common patterns
            ExtractTimestamp(line, logEvent);
            ExtractSeverity(line, logEvent);
            ExtractSpecificFields(line, logEvent);

            return await Task.FromResult(logEvent);
        }
        catch (Exception ex)
        {
            // Log the parsing error but don't fail the entire ingestion
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to parse line {LineNumber} from {File}: {Line}",
                    lineNumber, sourceFile, line?.Length > 100 ? line[..100] + "..." : line);
            }

            // Return a minimal event on parse failure
            return new LogEvent
            {
                RawLine = line ?? string.Empty,
                SourceFile = sourceFile,
                LineNumber = lineNumber,
                SourceType = SourceTypes.Generic,
                ParserName = Name,
                Severity = LogSeverity.Info,
                Tags = new HashSet<string> { "parse_error" }
            };
        }
    }

    public virtual async IAsyncEnumerable<LogEvent> ParseBatchAsync(
        IEnumerable<string> lines,
        string? sourceFile = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long lineNum = 0;
        foreach (var line in lines)
        {
            if (cancellationToken.IsCancellationRequested)
                yield break;

            lineNum++;
            var evt = await ParseAsync(line, sourceFile, lineNum, cancellationToken);
            if (evt != null)
                yield return evt;
        }
    }

    /// <summary>
    /// Extract timestamp from log line
    /// </summary>
    protected virtual void ExtractTimestamp(string line, LogEvent logEvent)
    {
        // Common timestamp patterns
        var patterns = new[]
        {
            @"(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})",  // 2024-01-01 12:34:56
            @"(\d{2}/\d{2}/\d{4}\s+\d{2}:\d{2}:\d{2})",  // 01/01/2024 12:34:56
            @"(\d{2}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2})",  // 01/01/24 12:34:56
            @"\[([^\]]+)\]",  // [timestamp in brackets]
        };

        foreach (var pattern in patterns)
        {
            try
            {
                var match = Regex.Match(line, pattern, RegexOptions.None, RegexTimeout);
                if (match.Success)
                {
                    if (DateTime.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
                    {
                        logEvent.Timestamp = timestamp.ToUniversalTime();
                        return;
                    }
                }
            }
            catch (RegexMatchTimeoutException) { /* skip to next pattern */ }
        }

        // Default to current time if no timestamp found
        logEvent.Timestamp = DateTime.UtcNow;
    }

    /// <summary>
    /// Extract severity level from log line
    /// </summary>
    protected virtual void ExtractSeverity(string line, LogEvent logEvent)
    {
        var lower = line.ToLowerInvariant();

        if (lower.Contains("fatal") || lower.Contains("critical"))
            logEvent.Severity = LogSeverity.Fatal;
        else if (lower.Contains("error") || lower.Contains("err:"))
            logEvent.Severity = LogSeverity.Error;
        else if (lower.Contains("warn") || lower.Contains("warning"))
            logEvent.Severity = LogSeverity.Warning;
        else if (lower.Contains("info") || lower.Contains("inf:"))
            logEvent.Severity = LogSeverity.Info;
        else if (lower.Contains("debug") || lower.Contains("dbg:"))
            logEvent.Severity = LogSeverity.Debug;
        else if (lower.Contains("trace") || lower.Contains("trc:"))
            logEvent.Severity = LogSeverity.Trace;
        else
            logEvent.Severity = LogSeverity.Info;
    }

    /// <summary>
    /// Extract specific fields based on parser type
    /// </summary>
    protected abstract void ExtractSpecificFields(string line, LogEvent logEvent);

    /// <summary>
    /// Extract Steam ID from text
    /// </summary>
    protected string? ExtractSteamId(string text)
    {
        try
        {
            var steamIdPattern = @"STEAM_[0-9]:[0-9]:[0-9]+|\[U:[0-9]:[0-9]+\]";
            var match = Regex.Match(text, steamIdPattern, RegexOptions.None, RegexTimeout);
            return match.Success ? match.Value : null;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    /// <summary>
    /// Extract IP address from text
    /// </summary>
    protected string? ExtractIpAddress(string text)
    {
        try
        {
            // Match valid IPv4 only: each octet 0-255
            var ipPattern = @"\b(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\b";
            var match = Regex.Match(text, ipPattern, RegexOptions.None, RegexTimeout);
            return match.Success ? match.Value : null;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    /// <summary>
    /// Extract player name from text
    /// </summary>
    protected string? ExtractPlayerName(string text)
    {
        try
        {
            // Look for patterns like "PlayerName<123><STEAM_ID>"
            var pattern = @"([^<]+)<\d+><[^>]+>";
            var match = Regex.Match(text, pattern, RegexOptions.None, RegexTimeout);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }
}