using System.Text;
using System.Text.RegularExpressions;

namespace Cabinet.Query.NaturalLanguage;

/// <summary>
/// Rule-based natural language to BQL translator.
/// Uses pattern matching and keyword extraction to convert common
/// natural language queries into valid BQL. Serves as the default
/// translator and a fallback when AI-based translation is unavailable.
/// </summary>
public sealed class RuleBasedTranslator : INaturalLanguageTranslator
{
    // Patterns that indicate natural language (vs structured query)
    private static readonly string[] NaturalLanguageIndicators =
    {
        "show me", "find all", "get all", "list all", "give me",
        "how many", "what are", "which", "display", "count the",
        "most recent", "latest", "oldest", "top ", "last ",
        "grouped by", "sorted by", "ordered by", "broken down by",
        "with errors", "with warnings", "that contain", "containing",
        "from server", "on server", "from the last", "in the past",
        "more than", "less than", "greater than", "at least",
        "between", "since", "before", "after"
    };

    // Translation rules ordered by specificity (most specific first)
    private static readonly TranslationRule[] Rules =
    {
        // Counting patterns
        new(@"how many (errors?|warnings?|events?|logs?)\s*(from|in|on|for)?\s*(?:the\s+)?(?:last\s+)?(\d+\s*[hdmw])?",
            m => BuildCountQuery(m.Groups[1].Value, m.Groups.Count > 3 ? m.Groups[3].Value : "")),

        // "show me errors from the last 24h"
        new(@"(?:show|find|get|list|give)\s+(?:me\s+)?(?:all\s+)?(errors?|warnings?|critical|fatal)\s+(?:from|in)\s+(?:the\s+)?last\s+(\d+\s*[hdmw])",
            m => $"find {NormalizeSeverityTerm(m.Groups[1].Value)} from last {m.Groups[2].Value.Replace(" ", "")}"),

        // "show me errors from server web-01"
        new(@"(?:show|find|get|list)\s+(?:me\s+)?(?:all\s+)?(errors?|warnings?|events?|logs?)\s+(?:from|on)\s+server\s+(\S+)",
            m => $"find {NormalizeSeverityTerm(m.Groups[1].Value)} | where server_id = \"{m.Groups[2].Value}\""),

        // "top 10 errors by host"
        new(@"top\s+(\d+)\s+(errors?|warnings?|events?|sources?|hosts?)\s+(?:grouped\s+)?by\s+(\w+)",
            m => $"find {NormalizeSeverityTerm(m.Groups[2].Value)} | top {m.Groups[1].Value} {m.Groups[3].Value}"),

        // "errors grouped by host"
        new(@"(errors?|warnings?|events?|logs?)\s+(?:grouped|broken\s+down|sorted|ordered)\s+by\s+(\w+)",
            m => $"find {NormalizeSeverityTerm(m.Groups[1].Value)} | group by {m.Groups[2].Value}"),

        // "latest/most recent N errors"
        new(@"(?:latest|most\s+recent|last)\s+(\d+)\s+(errors?|warnings?|events?|logs?)",
            m => $"find {NormalizeSeverityTerm(m.Groups[2].Value)} | sort by Timestamp desc | limit {m.Groups[1].Value}"),

        // "events containing 'timeout'"
        new(@"(?:events?|logs?)\s+(?:containing|that\s+contain|with)\s+['""]?(\w[\w\s]*?)['""]?\s*$",
            m => $"find \"{m.Groups[1].Value.Trim()}\""),

        // "errors from last 24h sorted by severity"
        new(@"(errors?|warnings?|events?|logs?)\s+(?:from|in)\s+(?:the\s+)?last\s+(\d+\s*[hdmw])\s+(?:sorted|ordered)\s+by\s+(\w+)(?:\s+(asc|desc))?",
            m => $"find {NormalizeSeverityTerm(m.Groups[1].Value)} from last {m.Groups[2].Value.Replace(" ", "")} | sort by {m.Groups[3].Value} {m.Groups[4].Value}".TrimEnd()),

        // "show me everything from the last 24h"
        new(@"(?:show|find|get|list)\s+(?:me\s+)?(?:all|everything)\s+(?:from|in)\s+(?:the\s+)?last\s+(\d+\s*[hdmw])",
            m => $"find * from last {m.Groups[1].Value.Replace(" ", "")}"),

        // Simple severity searches
        new(@"^(?:show|find|get|list)\s+(?:me\s+)?(?:all\s+)?(errors?|warnings?|critical|fatal|info)\s*$",
            m => $"find * | where severity >= {SeverityToNumber(m.Groups[1].Value)}"),

        // Fallback: extract key terms
        new(@"^(.+)$", m => ExtractTermsAsBql(m.Groups[1].Value)),
    };

    public bool IsNaturalLanguage(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;

        // If it starts with a BQL/SPL keyword, it's structured
        var trimmed = input.TrimStart().ToLowerInvariant();
        if (trimmed.StartsWith("find ") || trimmed.StartsWith("search ") ||
            trimmed.StartsWith("* |") || trimmed.StartsWith("| "))
            return false;

        // If it contains pipes, it's likely structured
        if (input.Contains('|'))
            return false;

        // Check for natural language patterns
        var lower = input.ToLowerInvariant();
        foreach (var indicator in NaturalLanguageIndicators)
        {
            if (lower.Contains(indicator))
                return true;
        }

        // If it's a short phrase with spaces and no operators, likely NL
        if (input.Split(' ').Length >= 3 && !input.Contains('=') && !input.Contains('>') && !input.Contains('<'))
            return true;

        return false;
    }

    public Task<NaturalLanguageTranslation> TranslateAsync(string naturalLanguageQuery, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(naturalLanguageQuery))
            return Task.FromResult(NaturalLanguageTranslation.Failed(naturalLanguageQuery, "Empty query"));

        var input = naturalLanguageQuery.Trim().ToLowerInvariant();

        foreach (var rule in Rules)
        {
            var match = rule.Pattern.Match(input);
            if (match.Success)
            {
                var bql = rule.Transform(match);
                if (!string.IsNullOrWhiteSpace(bql))
                {
                    return Task.FromResult(new NaturalLanguageTranslation
                    {
                        Success = true,
                        BqlQuery = bql,
                        Confidence = rule == Rules[^1] ? 0.3 : 0.8, // Fallback rule gets low confidence
                        OriginalQuery = naturalLanguageQuery,
                        Explanation = $"Translated to BQL: {bql}"
                    });
                }
            }
        }

        return Task.FromResult(NaturalLanguageTranslation.Failed(naturalLanguageQuery));
    }

    // ─── Helper methods ──────────────────────────────────────

    private static string BuildCountQuery(string term, string timeRange)
    {
        var sb = new StringBuilder("find ");
        sb.Append(NormalizeSeverityTerm(term));

        if (!string.IsNullOrWhiteSpace(timeRange))
            sb.Append($" from last {timeRange.Replace(" ", "")}");

        sb.Append(" | stats count");
        return sb.ToString();
    }

    private static string NormalizeSeverityTerm(string term)
    {
        var normalized = Depluralize(term).ToLowerInvariant();
        return normalized switch
        {
            "error" => "* | where severity >= 4",
            "warning" or "warn" => "* | where severity >= 3",
            "critical" or "crit" => "* | where severity >= 5",
            "fatal" => "* | where severity >= 6",
            "info" or "information" => "* | where severity >= 2",
            "event" or "log" => "*",
            "source" or "host" => "*",
            _ => $"\"{term}\""
        };
    }

    private static string SeverityToNumber(string term)
    {
        return Depluralize(term).ToLowerInvariant() switch
        {
            "error" or "err" => "4",
            "warning" or "warn" => "3",
            "critical" or "crit" => "5",
            "fatal" => "6",
            "info" or "information" => "2",
            "debug" => "1",
            _ => "2"
        };
    }

    /// <summary>
    /// Simple depluralization: only strip trailing 's' from known safe patterns.
    /// Avoids mangling words like "status", "process", "address".
    /// </summary>
    private static string Depluralize(string word)
    {
        if (word.Length <= 2) return word;
        if (!word.EndsWith('s')) return word;

        // Don't strip 's' from words ending in "ss", "us", "is"
        if (word.EndsWith("ss", StringComparison.OrdinalIgnoreCase) ||
            word.EndsWith("us", StringComparison.OrdinalIgnoreCase) ||
            word.EndsWith("is", StringComparison.OrdinalIgnoreCase))
            return word;

        return word[..^1];
    }

    private static string ExtractTermsAsBql(string input)
    {
        // Remove common filler words and build a search query
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "show", "me", "find", "get", "list", "give", "all", "the",
            "a", "an", "of", "with", "for", "and", "or", "that", "are",
            "is", "was", "were", "be", "been", "being", "have", "has",
            "had", "do", "does", "did", "will", "would", "could",
            "should", "can", "may", "might", "shall", "please"
        };

        var terms = input.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !stopWords.Contains(w))
            .ToList();

        return terms.Count > 0 ? $"find {string.Join(" ", terms)}" : "find *";
    }

    private sealed class TranslationRule
    {
        public Regex Pattern { get; }
        public Func<Match, string> Transform { get; }

        public TranslationRule(string pattern, Func<Match, string> transform)
        {
            Pattern = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
            Transform = transform;
        }
    }
}
