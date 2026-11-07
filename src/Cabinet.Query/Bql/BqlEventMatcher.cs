using System.Text.RegularExpressions;
using Cabinet.Core.Models;

namespace Cabinet.Query.Bql;

/// <summary>
/// Tests one event at a time against a detection rule's pattern, condition and extraction, with
/// the same field names and expression behavior as a query.
/// </summary>
public sealed class BqlEventMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private readonly Regex? _pattern;
    private readonly Regex? _extract;
    private readonly BqlExpression? _condition;

    /// <summary>Compiles a rule's expressions.</summary>
    /// <exception cref="QueryParseException">An expression isn't valid.</exception>
    public BqlEventMatcher(string? pattern, string? condition, string? extract)
    {
        _pattern = Compile(pattern, RegexOptions.IgnoreCase);
        _extract = Compile(extract, RegexOptions.None);

        if (!string.IsNullOrWhiteSpace(condition))
        {
            var query = new BqlParser().ParseBql($"* | where {condition}");
            _condition = query.Stages.OfType<WhereStage>().LastOrDefault()?.Condition
                ?? throw new QueryParseException($"Not a condition: {condition}");
        }

        if (_pattern == null && _condition == null)
        {
            throw new QueryParseException("A rule needs a pattern or a condition");
        }
    }

    /// <summary>Times a pattern ran out of time on an event, which counts as not matching.</summary>
    public int Timeouts { get; private set; }

    /// <summary>
    /// Tests an event. On a match, <paramref name="fields"/> holds the event's fields, including
    /// anything extracted.
    /// </summary>
    public bool TryMatch(LogEvent logEvent, out IDictionary<string, object?> fields)
    {
        fields = default!;
        try
        {
            if (_pattern != null && !_pattern.IsMatch(logEvent.RawLine))
            {
                return false;
            }

            var row = BqlQueryEngine.ConvertToExpando(logEvent);
            if (_condition != null && !BqlExpressionEvaluator.EvaluateBool(_condition, row))
            {
                return false;
            }

            if (_extract != null)
            {
                var match = _extract.Match(logEvent.RawLine);
                foreach (var name in _extract.GetGroupNames().Where(name => !int.TryParse(name, out _)))
                {
                    if (match.Groups[name].Success)
                    {
                        row[name] = match.Groups[name].Value;
                    }
                }
            }

            fields = row;
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            Timeouts++;
            return false;
        }
    }

    private static Regex? Compile(string? pattern, RegexOptions options)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        try
        {
            var regex = new Regex(pattern, options | RegexOptions.Compiled, RegexTimeout);

            // The first match compiles the expression, which on a busy machine can take longer
            // than the timeout allows, and would count as a slow rule
            regex.IsMatch(string.Empty);
            return regex;
        }
        catch (ArgumentException ex)
        {
            throw new QueryParseException($"Invalid regular expression '{pattern}': {ex.Message}");
        }
    }
}
