using System.Text.RegularExpressions;

namespace Cabinet.Query.Natural;

/// <summary>One suggestion for the word being typed.</summary>
/// <param name="Text">What replaces the word.</param>
/// <param name="Kind">command, field, value, function, time or phrase.</param>
/// <param name="Description">A few words on what it is.</param>
/// <param name="Start">Where the replaced word starts in the text.</param>
/// <param name="Length">How long the replaced word is.</param>
public sealed record Completion(string Text, string Kind, string Description, int Start, int Length);

/// <summary>
/// Suggests what to type next: commands after a pipe, fields after "by" or "where", values after a
/// field and "=", functions inside stats, and otherwise fields, values and question starters, all
/// from the caller's own data where it can.
/// </summary>
public static partial class QueryCompletions
{
    /// <summary>Most suggestions given at once.</summary>
    public const int Most = 12;

    private static readonly (string Name, string Description)[] Commands =
    [
        ("where", "Keep events matching a condition"), ("stats", "Count, sum, average and more, by fields"), ("timechart", "A value over time"),
        ("top", "Most common values"), ("rare", "Least common values"), ("sort", "Order results"), ("head", "First N results"), ("table", "Choose columns"),
        ("dedup", "One event per value"), ("eval", "Compute a field"), ("rex", "Extract fields with a regex"), ("rename", "Rename fields"),
        ("fields", "Choose columns"), ("chart", "A table of values by two fields"), ("eventstats", "Add totals to each event"), ("streamstats", "Running totals"),
        ("transaction", "Group events into sessions"), ("lookup", "Add fields from a lookup"), ("iplocation", "Country and city of an IP"),
        ("cluster", "Group similar lines"), ("fillnull", "Fill missing values"), ("bin", "Group numbers or times into buckets"),
    ];

    private static readonly (string Name, string Description)[] Functions =
    [
        ("count", "How many events"), ("dc(", "How many distinct values"), ("avg(", "Average"), ("sum(", "Total"), ("min(", "Lowest"), ("max(", "Highest"),
        ("median(", "Middle value"), ("perc95(", "95th percentile"), ("perc99(", "99th percentile"), ("stdev(", "Standard deviation"),
        ("values(", "Every distinct value"), ("latest(", "Newest value"), ("earliest(", "Oldest value"),
    ];

    private static readonly (string Text, string Description)[] Starters =
    [
        ("errors", "Events at error level or worse"), ("how many", "Count something"), ("top 10", "Most common values of a field"),
        ("last 24 hours", "A time range"), ("since yesterday", "A time range"), ("over time", "Chart it over time"),
        ("by host", "One row per host"), ("average", "Average of a field"), ("p95", "95th percentile of a field"), ("where", "Compare a field"),
    ];

    [GeneratedRegex(@"[^\s|,()=<>!]*$", RegexOptions.None, 1000)]
    private static partial Regex CurrentWord();

    [GeneratedRegex(@"(?<field>[\w.]+)\s*(?:=|!=|:|\bis\b|\bis not\b|\bequals\b)\s*""?$", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex AfterFieldEquals();

    [GeneratedRegex(@"(?:\|\s*|^\s*)$", RegexOptions.None, 1000)]
    private static partial Regex AtStageStart();

    [GeneratedRegex(@"\b(?:by|per|table|fields|dedup|where|and|or|sort by|top(?:\s+\d+)?|rare(?:\s+\d+)?|for each)\s*(?:[\w.]+\s*,\s*)*$", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpectsField();

    [GeneratedRegex(@"\b(?:stats|eventstats|streamstats|timechart(?:\s+span=\S+)?)\s+(?:[^|]*,\s*)?$", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpectsFunction();

    [GeneratedRegex(@"\b(?:avg|sum|min|max|median|perc\d+|p\d+|dc|stdev|values|latest|earliest|count)\($", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex InsideFunction();

    /// <summary>Suggestions for the word ending at <paramref name="cursor"/>.</summary>
    public static List<Completion> For(string text, int cursor, FieldCatalog catalog)
    {
        var before = text[..cursor];
        var word = CurrentWord().Match(before).Value;
        var start = cursor - word.Length;
        var context = before[..start];
        var results = new List<Completion>();

        void Add(string candidate, string kind, string description)
        {
            if (results.Count < Most && candidate.Trim('"').StartsWith(word.Trim('"'), StringComparison.OrdinalIgnoreCase)
                && !(candidate.Equals(word, StringComparison.OrdinalIgnoreCase) && kind != "value")
                && !results.Any(r => r.Text.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(new Completion(candidate, kind, description, start, word.Length));
            }
        }

        // Values of the field just named: "host=" or "user is "
        if (AfterFieldEquals().Match(context) is { Success: true } equals && catalog.Field(equals.Groups["field"].Value) is { } valued)
        {
            // In a stage, text is quoted, or "web-01" would read as web minus 01
            var quote = context.Contains('|');
            foreach (var value in catalog.Values(valued).OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
            {
                var numeric = double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
                Add(quote && !numeric ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value, "value", $"A {valued} in your data");
            }

            return results;
        }

        // A command after a pipe
        if (AtStageStart().IsMatch(context) && context.Contains('|'))
        {
            foreach (var (name, description) in Commands)
            {
                Add(name, "command", description);
            }

            return results;
        }

        if (InsideFunction().IsMatch(context) || ExpectsField().IsMatch(context))
        {
            AddFields(catalog, Add);
            return results;
        }

        if (ExpectsFunction().IsMatch(context))
        {
            foreach (var (name, description) in Functions)
            {
                Add(name, "function", description);
            }

            return results;
        }

        // Anything else: question starters, then fields and values worth naming. A starter of
        // several words matches the words typed so far: "how m" finds "how many".
        if (!context.Contains('|'))
        {
            foreach (var (starter, description) in Starters)
            {
                var words = starter.Count(c => c == ' ') + 1;
                var tail = string.Join(' ', before.Split(' ', StringSplitOptions.RemoveEmptyEntries).TakeLast(words));
                if (words > 1 && tail.Length > word.Length && starter.StartsWith(tail, StringComparison.OrdinalIgnoreCase) && results.Count < Most
                    && before.EndsWith(tail, StringComparison.Ordinal))
                {
                    results.Add(new Completion(starter, "phrase", description, cursor - tail.Length, tail.Length));
                    continue;
                }

                Add(starter, "phrase", description);
            }
        }

        if (word.Length > 0)
        {
            AddFields(catalog, Add);
            foreach (var field in new[] { "host", "sourcetype", "index" })
            {
                foreach (var value in catalog.Values(field))
                {
                    Add(value, "value", $"A {field} in your data");
                }
            }
        }

        return results;
    }

    private static void AddFields(FieldCatalog catalog, Action<string, string, string> add)
    {
        foreach (var field in FieldCatalog.Builtin.Where(f => !f.StartsWith('_')).Concat(catalog.Fields.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)))
        {
            add(field, "field", FieldCatalog.Builtin.Contains(field) ? "Every event has this" : "A field in your data");
        }
    }
}

/// <summary>A starter question.</summary>
/// <param name="Category">What it's about: Overview, Errors, Performance, Security.</param>
/// <param name="Text">The question in words.</param>
/// <param name="Query">The BQL it runs as.</param>
public sealed record QueryExample(string Category, string Text, string Query);

/// <summary>Starter questions that fit the caller's data: its hosts, its fields, its source types.</summary>
public static class QueryExamples
{
    public static List<QueryExample> For(FieldCatalog catalog)
    {
        var host = catalog.Values("host").OrderBy(h => h, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        var examples = new List<QueryExample>
        {
            new("Overview", "how many events per sourcetype this week", string.Empty),
            new("Overview", "busiest hosts in the past 6 hours", string.Empty),
            new("Overview", "events per minute over the last hour by host", string.Empty),
            new("Errors", "errors in the last hour", string.Empty),
            new("Errors", "how many errors per host today", string.Empty),
            new("Errors", "error rate by hour for the last 2 days", string.Empty),
        };

        if (host != null)
        {
            examples.Add(new("Overview", $"what happened on {host} in the last 15 minutes", string.Empty));
        }

        bool Has(params string[] names) => names.Any(n => catalog.Field(n) != null);

        if (Has("duration_ms", "duration", "latency_ms", "latency", "response_time"))
        {
            var by = Has("path", "url", "uri", "endpoint") ? " by endpoint" : " by host";
            examples.Add(new("Performance", $"p95 response time{by}", string.Empty));
            if (Has("path", "url", "uri", "endpoint"))
            {
                examples.Add(new("Performance", "top 5 slowest endpoints", string.Empty));
            }

            examples.Add(new("Performance", "average response time over time", string.Empty));
        }

        if (Has("status", "status_code", "http_status"))
        {
            examples.Add(new("Errors", "count of 5xx errors per endpoint", string.Empty));
            examples.Add(new("Errors", "most common status codes", string.Empty));
        }

        if (Has("user", "username", "account"))
        {
            examples.Add(new("Security", "failed logins by user today", string.Empty));
            examples.Add(new("Security", "how many unique users per day", string.Empty));
        }

        if (Has("src_ip", "client_ip", "clientip", "ip"))
        {
            examples.Add(new("Security", "top 10 ips by number of requests", string.Empty));
        }

        return examples;
    }
}
