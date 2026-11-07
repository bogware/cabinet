using System.Diagnostics;
using System.Text.RegularExpressions;
using Cabinet.Agent.Configuration;

namespace Cabinet.Agent.Monitoring;

/// <summary>
/// One log event, made of one or more lines.
/// </summary>
/// <param name="Text">The event's text.</param>
/// <param name="StartOffset">Byte offset of its first line, which identifies the event.</param>
/// <param name="EndOffset">Byte offset just past its last line.</param>
/// <param name="LineNumber">Number of its last line.</param>
public readonly record struct MergedLine(string Text, long StartOffset, long EndOffset, long LineNumber);

/// <summary>
/// Merges continuation lines into events. A stack trace can span several polls, so an event stays
/// open until the next event starts, it reaches the line limit, or the timeout passes with no
/// more lines. An open event isn't checkpointed, so a crash re-reads it instead of losing it.
/// </summary>
public sealed class MultiLineMerger
{
    private readonly MultiLineConfiguration? _config;
    private readonly Regex? _pattern;
    private readonly TimeSpan _timeout;
    private readonly ILogger _logger;
    private readonly List<string> _pending = [];
    private long _startOffset;
    private long _endOffset;
    private long _lineNumber;
    private long _lastLineTicks;

    public MultiLineMerger(MultiLineConfiguration? config, ILogger logger)
    {
        _config = config;
        _logger = logger;
        _timeout = TimeSpan.FromSeconds(config?.TimeoutSeconds ?? 0);

        if (string.IsNullOrEmpty(config?.Pattern))
        {
            return;
        }

        try
        {
            _pattern = new Regex(config.Pattern, RegexOptions.Compiled, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            _logger.LogError(ex, "Invalid multi-line pattern, treating each line as an event: {Pattern}", config.Pattern);
        }
    }

    /// <summary>Adds a line, returning any events it completes.</summary>
    public IReadOnlyList<MergedLine> Add(TailLine line, long lineNumber)
    {
        if (_pattern == null)
        {
            return [new MergedLine(line.Text, line.StartOffset, line.EndOffset, lineNumber)];
        }

        var completed = new List<MergedLine>(1);
        var continues = _pending.Count > 0 && IsContinuation(line.Text) && _pending.Count < _config!.MaxLines;
        if (!continues && Take() is { } previous)
        {
            completed.Add(previous);
        }

        if (_pending.Count == 0)
        {
            _startOffset = line.StartOffset;
        }

        _pending.Add(line.Text);
        _endOffset = line.EndOffset;
        _lineNumber = lineNumber;
        _lastLineTicks = Stopwatch.GetTimestamp();
        return completed;
    }

    /// <summary>Returns the open event if no line has arrived within the timeout.</summary>
    public MergedLine? TakeIfExpired() =>
        _pending.Count > 0 && Stopwatch.GetElapsedTime(_lastLineTicks) >= _timeout ? Take() : null;

    /// <summary>Returns the open event, if there is one.</summary>
    public MergedLine? Take()
    {
        if (_pending.Count == 0)
        {
            return null;
        }

        var merged = new MergedLine(string.Join('\n', _pending), _startOffset, _endOffset, _lineNumber);
        _pending.Clear();
        return merged;
    }

    private bool IsContinuation(string line)
    {
        try
        {
            // Negate (the default) means the pattern marks the start of a new event.
            var matches = _pattern!.IsMatch(line);
            return _config!.Negate ? !matches : matches;
        }
        catch (RegexMatchTimeoutException ex)
        {
            _logger.LogWarning(ex, "Multi-line pattern timed out, treating the line as a new event");
            return false;
        }
    }
}
