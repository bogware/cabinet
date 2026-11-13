using System.Text;
using System.Text.RegularExpressions;

namespace Cabinet.Parsers;

/// <summary>
/// Mode of multi-line detection
/// </summary>
public enum MultiLineMode
{
    /// <summary>
    /// Lines matching the pattern start a new log entry.
    /// Subsequent non-matching lines are continuation lines merged into the current entry.
    /// </summary>
    StartPattern,

    /// <summary>
    /// Lines matching the pattern are continuation lines that merge into the previous entry.
    /// Non-matching lines start a new entry.
    /// </summary>
    ContinuationPattern
}

/// <summary>
/// Configuration for multi-line log merging
/// </summary>
public sealed class MultiLineConfig
{
    /// <summary>Detection mode</summary>
    public MultiLineMode Mode { get; set; } = MultiLineMode.StartPattern;

    /// <summary>
    /// Regex pattern for detection.
    /// In StartPattern mode: lines matching this start a new entry.
    /// In ContinuationPattern mode: lines matching this are continuation lines.
    /// </summary>
    public required string Pattern { get; set; }

    /// <summary>Maximum number of lines to merge into a single entry (prevents runaway merging)</summary>
    public int MaxLines { get; set; } = 500;

    /// <summary>Maximum total character count for a merged entry</summary>
    public int MaxCharacters { get; set; } = 100_000;
}

/// <summary>
/// Predefined patterns for common multi-line log formats
/// </summary>
public static class MultiLinePatterns
{
    /// <summary>
    /// Java stack traces: start with a timestamp or log level, continuations start with
    /// whitespace, "at ", "Caused by:", "...", or "}"
    /// </summary>
    public static MultiLineConfig JavaStackTrace => new()
    {
        Mode = MultiLineMode.ContinuationPattern,
        Pattern = @"^(?:\s+at\s|Caused\s+by:|\.{3}\s+\d+\s+more|\s+\.\.\.|\t)"
    };

    /// <summary>
    /// Python tracebacks: start with "Traceback", continuations are indented lines,
    /// "File " lines, or the final exception line.
    /// Uses start pattern matching on timestamp-prefixed lines.
    /// </summary>
    public static MultiLineConfig PythonTraceback => new()
    {
        Mode = MultiLineMode.ContinuationPattern,
        Pattern = @"^(?:\s+File\s|Traceback\s|(?:\s+)(?!\S)|\w+Error:|\w+Exception:)"
    };

    /// <summary>
    /// Timestamp-prefixed logs: any line starting with a common timestamp format starts
    /// a new entry. Lines without a leading timestamp are continuations.
    /// </summary>
    public static MultiLineConfig TimestampPrefixed => new()
    {
        Mode = MultiLineMode.StartPattern,
        Pattern = @"^\d{4}[-/]\d{2}[-/]\d{2}[\sT]\d{2}:\d{2}:\d{2}"
    };

    /// <summary>
    /// Syslog-style: lines starting with a standard syslog timestamp (Mon DD HH:MM:SS)
    /// begin a new entry.
    /// </summary>
    public static MultiLineConfig SyslogTimestamp => new()
    {
        Mode = MultiLineMode.StartPattern,
        Pattern = @"^(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\s+\d{1,2}\s+\d{2}:\d{2}:\d{2}"
    };

    /// <summary>
    /// JSON logs: lines starting with '{' begin a new entry.
    /// Supports pretty-printed JSON where continuation lines don't start with '{'.
    /// </summary>
    public static MultiLineConfig JsonLogs => new()
    {
        Mode = MultiLineMode.StartPattern,
        Pattern = @"^\s*\{"
    };
}

/// <summary>
/// Merges continuation lines into single log entries based on configurable patterns.
/// Supports start-pattern mode (pattern identifies entry starts) and continuation-pattern
/// mode (pattern identifies lines to merge into the previous entry).
/// </summary>
public sealed class MultiLineLogMerger
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);
    private readonly Regex _pattern;
    private readonly MultiLineConfig _config;

    public MultiLineLogMerger(MultiLineConfig config)
    {
        _config = config;
        // The pattern comes from configuration and runs on every line, so it gets a time limit
        _pattern = new Regex(config.Pattern, RegexOptions.Compiled, PatternTimeout);
    }

    // A line the pattern can't decide in time counts as not matching
    private bool Matches(string line)
    {
        try
        {
            return _pattern.IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Process input lines and merge multi-line entries into single strings.
    /// Each yielded string represents one complete log entry (potentially spanning multiple input lines).
    /// </summary>
    public IEnumerable<string> ProcessLines(IEnumerable<string> lines)
    {
        var buffer = new StringBuilder();
        var lineCount = 0;

        foreach (var line in lines)
        {
            var isPatternMatch = Matches(line);
            bool isNewEntry;

            if (_config.Mode == MultiLineMode.StartPattern)
            {
                // In StartPattern mode, pattern match = new entry
                isNewEntry = isPatternMatch;
            }
            else
            {
                // In ContinuationPattern mode, pattern match = continuation (NOT a new entry)
                isNewEntry = !isPatternMatch;
            }

            if (isNewEntry && buffer.Length > 0)
            {
                // Flush the current buffer as a complete entry
                yield return buffer.ToString();
                buffer.Clear();
                lineCount = 0;
            }

            // Check buffer limits before appending
            if (lineCount >= _config.MaxLines || buffer.Length + line.Length > _config.MaxCharacters)
            {
                if (buffer.Length > 0)
                {
                    // Flush oversized buffer
                    yield return buffer.ToString();
                    buffer.Clear();
                    lineCount = 0;
                }
            }

            // Truncate individual lines that exceed MaxCharacters on their own
            var appendLine = line.Length > _config.MaxCharacters
                ? line[.._config.MaxCharacters]
                : line;

            if (buffer.Length > 0)
            {
                buffer.Append('\n');
            }
            buffer.Append(appendLine);
            lineCount++;
        }

        // Flush remaining buffer
        if (buffer.Length > 0)
        {
            yield return buffer.ToString();
        }
    }
}
