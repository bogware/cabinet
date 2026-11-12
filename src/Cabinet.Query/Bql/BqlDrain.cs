using System.Text.RegularExpressions;

namespace Cabinet.Query.Bql;

/// <summary>
/// Groups log lines into patterns with the Drain algorithm (He et al., 2017): lines with the
/// same number of words that share their first words and mostly agree word by word are one
/// pattern, and the words where they differ become <c>&lt;*&gt;</c>. Numbers, addresses, IDs and
/// times are wildcards from the start, so "took 35ms" and "took 41ms" agree.
/// </summary>
public sealed class BqlDrain
{
    public const string Wildcard = "<*>";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly Regex Variable = new(
        @"^(?:[+-]?\d+(?:[.,:]\d+)*(?:ms|s|us|ns|kb|mb|gb|b|%)?|0x[0-9a-f]+|[0-9a-f]{8,}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|\d{1,3}(?:\.\d{1,3}){3}(?::\d+)?|[0-9a-f:]{6,}:[0-9a-f:]+|\d{4}-\d{2}-\d{2}(?:[t ]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:z|[+-]\d{2}:?\d{2})?)?|\d{2}:\d{2}:\d{2}(?:\.\d+)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    private const int MaxWords = 64;
    private const int PrefixDepth = 1;

    private readonly double _similarity;
    private readonly int _maxPatterns;
    private readonly Dictionary<string, List<Pattern>> _groups = new(StringComparer.Ordinal);
    private int _count;

    /// <param name="similarity">How many of a line's words must match a pattern's, 0 to 1. SPL's cluster calls this t.</param>
    /// <param name="maxPatterns">Most patterns kept; lines past that join the closest one.</param>
    public BqlDrain(double similarity = 0.5, int maxPatterns = 10_000)
    {
        _similarity = Math.Clamp(similarity, 0.01, 1);
        _maxPatterns = Math.Max(1, maxPatterns);
    }

    /// <summary>A pattern and how many lines it covers.</summary>
    public sealed class Pattern
    {
        internal Pattern(int id, string[] words, string sample)
        {
            Id = id;
            Words = words;
            Sample = sample;
        }

        /// <summary>A number for the pattern, in the order patterns were found.</summary>
        public int Id { get; }

        internal string[] Words { get; private set; }

        /// <summary>The pattern, with <c>&lt;*&gt;</c> where lines differ.</summary>
        public string Template => string.Join(' ', Words);

        /// <summary>The first line seen.</summary>
        public string Sample { get; }

        /// <summary>Lines in the pattern.</summary>
        public long Count { get; internal set; }

        internal void Merge(string[] words)
        {
            for (var i = 0; i < Words.Length; i++)
            {
                if (Words[i] != words[i])
                {
                    Words[i] = Wildcard;
                }
            }
        }
    }

    /// <summary>The patterns found so far.</summary>
    public IEnumerable<Pattern> Patterns => _groups.Values.SelectMany(group => group);

    /// <summary>Adds a line and returns the pattern it joined or started.</summary>
    public Pattern Add(string? line)
    {
        var text = line ?? string.Empty;
        var words = Words(text);
        var key = Key(words);
        if (!_groups.TryGetValue(key, out var group))
        {
            group = [];
            _groups[key] = group;
        }

        Pattern? best = null;
        var bestScore = -1.0;
        foreach (var candidate in group)
        {
            var score = Similarity(candidate.Words, words);
            if (score > bestScore)
            {
                (best, bestScore) = (candidate, score);
            }
        }

        if (best != null && (bestScore >= _similarity || _count >= _maxPatterns))
        {
            best.Merge(words);
            best.Count++;
            return best;
        }

        var pattern = new Pattern(++_count, words, text.Length > 500 ? text[..500] : text) { Count = 1 };
        group.Add(pattern);
        return pattern;
    }

    /// <summary>A line's pattern words without adding it: its words, with variables as wildcards.</summary>
    public static string[] Words(string line)
    {
        var words = line.Split((char[]?)null, MaxWords + 1, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > MaxWords)
        {
            // The rest of a very long line is one word, so length still tells lines apart
            words[MaxWords] = Wildcard;
        }

        for (var i = 0; i < words.Length; i++)
        {
            words[i] = IsVariable(words[i]) ? Wildcard : words[i];
        }

        return words;
    }

    private static bool IsVariable(string word)
    {
        var trimmed = word.Trim('"', '\'', ',', ';', '(', ')', '[', ']', '{', '}');
        if (trimmed.Length == 0)
        {
            return false;
        }

        // key=value: the value decides
        var equals = trimmed.IndexOf('=');
        if (equals > 0 && equals < trimmed.Length - 1)
        {
            trimmed = trimmed[(equals + 1)..].Trim('"', '\'');
        }

        try
        {
            return Variable.IsMatch(trimmed);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>Lines are only compared with lines of the same length and first words.</summary>
    private static string Key(string[] words)
    {
        var prefix = words.Take(PrefixDepth).Select(word => word.Any(char.IsDigit) ? Wildcard : word);
        return $"{words.Length}\u001f{string.Join('\u001f', prefix)}";
    }

    private static double Similarity(string[] pattern, string[] words)
    {
        if (pattern.Length == 0)
        {
            return 1;
        }

        var same = 0;
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == words[i] || pattern[i] == Wildcard)
            {
                same++;
            }
        }

        return (double)same / pattern.Length;
    }
}

/// <summary>
/// cluster [t=0.5] [field=RawLine] [labelonly=false]: one row per pattern, with its count,
/// template and a sample line, most common first; or with labelonly=true, every row gets its
/// pattern's cluster_label and cluster_pattern.
/// </summary>
public sealed class ClusterStage : BqlStage
{
    public override string StageName => "cluster";

    public double Similarity { get; init; } = 0.5;

    public string Field { get; init; } = "RawLine";

    public bool LabelOnly { get; init; }
}

internal sealed class LegacyClusterOperator : QueryOperator
{
    private readonly ClusterStage _stage;

    public LegacyClusterOperator(ClusterStage stage)
    {
        _stage = stage;
    }

    public override string Name => "cluster";

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken cancellationToken = default)
    {
        var limits = Budget?.Limits ?? new QueryLimits();
        var drain = new BqlDrain(_stage.Similarity, limits.MaxGroups);
        if (_stage.LabelOnly)
        {
            var labelled = new List<dynamic>();
            foreach (object row in input)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dict = (IDictionary<string, object?>)row;
                var pattern = drain.Add(dict.TryGetValue(_stage.Field, out var value) ? value?.ToString() : null);
                dict["cluster_label"] = pattern.Id;
                labelled.Add(row);
            }

            // A pattern only settles once every line has been seen
            var templates = drain.Patterns.ToDictionary(p => p.Id, p => (p.Template, p.Count));
            foreach (IDictionary<string, object?> row in labelled)
            {
                var (template, count) = templates[(int)row["cluster_label"]!];
                row["cluster_pattern"] = template;
                row["cluster_count"] = count;
            }

            return Task.FromResult<IEnumerable<dynamic>>(labelled);
        }

        foreach (object row in input)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dict = (IDictionary<string, object?>)row;
            drain.Add(dict.TryGetValue(_stage.Field, out var value) ? value?.ToString() : null);
        }

        var rows = drain.Patterns
            .OrderByDescending(p => p.Count)
            .ThenBy(p => p.Id)
            .Select(p => (dynamic)new BqlRow
            {
                ["cluster_label"] = p.Id,
                ["cluster_count"] = p.Count,
                ["cluster_pattern"] = p.Template,
                ["sample"] = p.Sample,
            })
            .ToList();
        return Task.FromResult<IEnumerable<dynamic>>(rows);
    }
}
