using System.Text.RegularExpressions;
using Cabinet.Query.Bql;

namespace Cabinet.Query.Natural;

/// <summary>How a query was understood.</summary>
public enum UnderstandingKind
{
    /// <summary>Strict BQL or SPL, run exactly as typed.</summary>
    Exact,

    /// <summary>Strict BQL with a mistyped command fixed.</summary>
    Corrected,

    /// <summary>Plain words, read into BQL.</summary>
    Natural,

    /// <summary>Not understood well enough to run.</summary>
    Unclear,
}

/// <summary>What Cabinet made of a query, and whether it will run it.</summary>
public sealed class QueryUnderstanding
{
    /// <summary>What was typed.</summary>
    public required string Input { get; init; }

    public UnderstandingKind Kind { get; init; }

    /// <summary>The BQL that runs, or null when it isn't clear enough to run.</summary>
    public string? Query { get; init; }

    /// <summary>Whether <see cref="Query"/> runs without asking.</summary>
    public bool WillRun => Query != null && Kind != UnderstandingKind.Unclear;

    /// <summary>How sure the reading is, from 0 to 1. Exact queries are 1.</summary>
    public double Confidence { get; init; } = 1;

    /// <summary>What the query does, in words, for a natural reading.</summary>
    public string? Explanation { get; init; }

    /// <summary>Words read as other words.</summary>
    public IReadOnlyList<QueryCorrection> Corrections { get; init; } = [];

    /// <summary>Other ways to read it, to offer as one-click choices.</summary>
    public IReadOnlyList<QueryReadingChoice> Alternatives { get; init; } = [];

    /// <summary>Problems in what was typed, with where they are and a fix where there's one.</summary>
    public IReadOnlyList<QueryProblem> Problems { get; init; } = [];

    /// <summary>Whether to offer the AI assistant, because the reader couldn't manage.</summary>
    public bool SuggestAssistant { get; init; }
}

/// <summary>Another reading of a query.</summary>
public sealed record QueryReadingChoice(string Query, string Description);

/// <summary>A problem at a place in the query, and the query with it fixed, if there's a fix.</summary>
public sealed record QueryProblem(int Start, int Length, string Message, string? Fix = null);

/// <summary>
/// Decides what to run for what someone typed. Strict BQL and SPL run as typed. Text with no pipe
/// whose words have clear structure (a time, a comparison with a field, a count, grouping, sorting)
/// is read naturally, with the exact-words search offered back. A mistyped command is fixed when
/// only one is close. Anything else isn't run, and comes back with readings to choose from.
/// Only interactive searches come here; saved searches, alerts and rules always run strictly.
/// </summary>
public sealed partial class QueryUnderstander
{
    /// <summary>Readings below this are offered rather than run.</summary>
    public const double RunThreshold = 0.6;

    private readonly BqlParser _parser = new();
    private readonly NaturalReader _reader;

    public QueryUnderstander(Lexicon? words = null)
    {
        _reader = new NaturalReader(words);
    }

    /// <summary>The stage keywords BQL knows, for correcting a mistyped one.</summary>
    public static readonly IReadOnlyList<string> StageKeywords =
    [
        "where", "group", "sort", "limit", "compute", "extract", "select", "unique", "rename", "fill", "expand", "fillnull", "bucket", "bin",
        "makeresults", "mvexpand", "chart", "filldown", "addcoltotals", "inputlookup", "rare", "regex", "fieldsummary", "xyseries", "untable",
        "convert", "iplocation", "cluster", "eventstats", "streamstats", "stats", "eval", "rex", "head", "tail", "table", "fields", "dedup",
        "top", "timechart", "fieldalias", "transaction", "lookup", "append", "join", "macro", "subsearch", "multisearch", "search", "find",
    ];

    private static readonly HashSet<string> EverydayCommands = ["unique", "select", "group", "fill", "expand", "convert", "append", "join", "limit", "extract", "compute", "bucket", "bin", "chart", "lookup", "cluster", "transaction", "rename", "top", "rare", "sort", "fields", "table"];

    [GeneratedRegex(@"\b[\w.]+\s*(?:!=|=)\s*\S|\b(?:OR|AND|NOT)\b|[()*]|\bearliest=|\blatest=|\bfrom last \d+[smhdw]\b", RegexOptions.None, 1000)]
    private static partial Regex SplSyntax();

    [GeneratedRegex(@"^\s*(?:find|search)\b\s*", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex SearchVerb();

    [GeneratedRegex(@"Unknown stage: (?<stage>\S+)", RegexOptions.None, 1000)]
    private static partial Regex UnknownStage();

    /// <summary>Understands a query typed into a search box, the CLI or the API.</summary>
    public QueryUnderstanding Understand(string input, FieldCatalog? catalog = null, DateTime? now = null)
    {
        catalog ??= FieldCatalog.Empty;
        var at = now ?? DateTime.UtcNow;
        var text = input.Trim();
        var segments = SplitPipes(text);
        var strictError = StrictError(text);

        // "stats count by host" on its own means every event, then that stage. Commands that are
        // also everyday words ("unique ips", "top users") are read as words first.
        if (strictError != null && segments.Count == 1 && StageKeywords.Contains(FirstWord(text)) && FirstWord(text) is not ("find" or "search")
            && StrictError("find * | " + text) == null
            && !(EverydayCommands.Contains(FirstWord(text)) && _reader.Read(text, catalog, at) is { HasStructure: true, Confidence: >= RunThreshold } wordsFirst && StrictError(wordsFirst.Query) == null))
        {
            text = "find * | " + text;
            segments = SplitPipes(text);
            strictError = null;
        }

        if (strictError == null)
        {
            if (segments.Count > 1 || SplSyntax().IsMatch(text) || StartsWithStage(text))
            {
                return ExactOrFieldFixed(input, text, catalog);
            }

            // No pipe: plain words may be a question
            var reading = _reader.Read(SearchVerb().Replace(text, string.Empty), catalog, at);
            var understood = reading.HasStructure || (reading.AsksInWords && reading.Query != "find " + text);
            if (understood && reading.Confidence < RunThreshold && LooksLikeQuestion(text))
            {
                // A question it half understood: searching its words would find nothing useful
                return Unclear(input, text, null, reading);
            }

            if (!understood || reading.Confidence < RunThreshold || StrictError(reading.Query) != null)
            {
                return ExactOrFieldFixed(input, text, catalog);
            }

            return Natural(input, reading, [new QueryReadingChoice(text, "Search for these exact words instead"), .. SeverityAsWord(reading)]);
        }

        // A pipeline with a mistake: fix mistyped commands, then read stages that still fail as words
        if (segments.Count > 1)
        {
            return UnderstandPipeline(input, segments, catalog, at, strictError);
        }

        var natural = _reader.Read(SearchVerb().Replace(text, string.Empty), catalog, at);
        if (natural.Confidence >= RunThreshold && StrictError(natural.Query) == null)
        {
            return Natural(input, natural, SeverityAsWord(natural).ToList());
        }

        return Unclear(input, text, strictError, natural);
    }

    private QueryUnderstanding UnderstandPipeline(string input, List<string> segments, FieldCatalog catalog, DateTime at, QueryParseException strictError)
    {
        var corrections = new List<QueryCorrection>();
        var fixedSegments = new List<string> { segments[0] };
        var naturalUsed = false;
        var confidence = 1.0;
        var explanations = new List<string>();
        var offset = segments[0].Length + 1;

        for (var s = 1; s < segments.Count; s++)
        {
            var segment = segments[s].Trim();
            if (StrictError("find * | " + segment) == null)
            {
                fixedSegments.Add(segment);
            }
            else if (FixStageKeyword(segment) is { } corrected && StrictError("find * | " + corrected.Text) == null)
            {
                corrections.Add(new QueryCorrection(corrected.From, corrected.To, offset + segments[s].IndexOf(corrected.From, StringComparison.Ordinal), corrected.From.Length));
                fixedSegments.Add(corrected.Text);
                confidence -= 0.05;
            }
            else
            {
                var stages = _reader.ReadStages(segment, catalog, at);
                if (stages.Stages.Count == 0 || stages.UnknownWords.Count > 0)
                {
                    return Unclear(input, string.Join(" | ", segments), strictError, null);
                }

                // Times read in a later stage belong to the search
                if (stages.Earliest != null || stages.Latest != null)
                {
                    fixedSegments[0] += (stages.Earliest != null ? $" earliest={stages.Earliest}" : string.Empty) + (stages.Latest != null ? $" latest={stages.Latest}" : string.Empty);
                }

                fixedSegments.Add(string.Join(" | ", stages.Stages));
                corrections.AddRange(stages.Corrections);
                confidence = Math.Min(confidence, stages.Confidence);
                explanations.Add(stages.Explanation);
                naturalUsed = true;
            }

            offset += segments[s].Length + 1;
        }

        var query = string.Join(" | ", fixedSegments.Select(f => f.Trim()));
        if (StrictError(query) is { } stillWrong || confidence < RunThreshold)
        {
            return Unclear(input, query, strictError, null);
        }

        return new QueryUnderstanding
        {
            Input = input,
            Kind = naturalUsed ? UnderstandingKind.Natural : UnderstandingKind.Corrected,
            Query = query,
            Confidence = confidence,
            Corrections = corrections,
            Explanation = explanations.Count > 0 ? string.Join("; ", explanations) : null,
        };
    }

    private static QueryUnderstanding Natural(string input, NaturalReading reading, List<QueryReadingChoice> alternatives) => new()
    {
        Input = input,
        Kind = UnderstandingKind.Natural,
        Query = reading.Query,
        Confidence = reading.Confidence,
        Explanation = reading.Explanation,
        Corrections = reading.Corrections,
        Alternatives = alternatives,
    };

    /// <summary>"errors" read as a level can also be the plain word.</summary>
    private static IEnumerable<QueryReadingChoice> SeverityAsWord(NaturalReading reading)
    {
        if (reading.SeverityWord is { } word && reading.WordQuery is { } query)
        {
            yield return new QueryReadingChoice(query, $"Lines containing the word \"{word.ToLowerInvariant().TrimEnd('s')}\", whatever their level");
        }
    }

    private static string FirstWord(string text) => text.TrimStart().Split([' ', '|'], 2)[0].ToLowerInvariant();

    /// <summary>
    /// A strict query, with any field the data doesn't have but one it nearly matches replaced by
    /// that one ("hots" for host), shown as a correction. Other problems are only reported.
    /// </summary>
    private QueryUnderstanding ExactOrFieldFixed(string input, string text, FieldCatalog catalog)
    {
        var problems = FieldProblems(text, catalog);
        if (problems.Count == 0)
        {
            return new QueryUnderstanding { Input = input, Kind = UnderstandingKind.Exact, Query = text };
        }

        var corrections = new List<QueryCorrection>();
        var query = text;
        foreach (var problem in problems.OrderByDescending(p => p.Start))
        {
            var from = query.Substring(problem.Start, problem.Length);
            var fix = problem.Fix!;
            var to = fix[problem.Start..(fix.Length - (query.Length - problem.Start - problem.Length))];
            query = query[..problem.Start] + to + query[(problem.Start + problem.Length)..];
            corrections.Add(new QueryCorrection(from, to, problem.Start, problem.Length));
        }

        if (StrictError(query) != null)
        {
            return new QueryUnderstanding { Input = input, Kind = UnderstandingKind.Exact, Query = text, Problems = problems };
        }

        return new QueryUnderstanding
        {
            Input = input,
            Kind = UnderstandingKind.Corrected,
            Query = query,
            Confidence = 0.9,
            Corrections = corrections,
            Alternatives = [new QueryReadingChoice(text, "Run it exactly as typed")],
        };
    }

    [GeneratedRegex(@"^\s*(?:which|what|who|how|when|where|why|show|list|give|find me|are there|is there|did|does|do)\b|\?\s*$", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Question();

    private static bool LooksLikeQuestion(string text) => Question().IsMatch(text);

    private QueryUnderstanding Unclear(string input, string text, QueryParseException? error, NaturalReading? reading)
    {
        var alternatives = new List<QueryReadingChoice>();
        if (reading is { HasStructure: true } && StrictError(reading.Query) == null)
        {
            alternatives.Add(new QueryReadingChoice(reading.Query, reading.Explanation));
        }

        var words = Regex.Replace(SearchVerb().Replace(text, string.Empty), @"[|=<>()""]", " ").Trim();
        if (words.Length > 0 && StrictError("find " + words) == null)
        {
            alternatives.Add(new QueryReadingChoice("find " + words, "Search for these words"));
        }

        return new QueryUnderstanding
        {
            Input = input,
            Kind = UnderstandingKind.Unclear,
            Confidence = reading?.Confidence ?? 0,
            Corrections = reading?.Corrections ?? [],
            Alternatives = alternatives,
            Problems = error != null ? [ProblemFrom(text, error)] : [new QueryProblem(0, text.Length, UnclearMessage(reading))],
            SuggestAssistant = true,
        };
    }

    /// <summary>What to say about a question read only in part: the words it couldn't place.</summary>
    private static string UnclearMessage(NaturalReading? reading) =>
        reading is { UnknownWords.Count: > 0 }
            ? $"Not sure what \"{string.Join(" ", reading.UnknownWords)}\" means here. Pick a reading below, rephrase, or ask the assistant."
            : "Not sure what this asks for. Pick a reading below, rephrase, or ask the assistant.";

    /// <summary>The parse error for a query, or null if it parses.</summary>
    private QueryParseException? StrictError(string query)
    {
        try
        {
            _parser.ParseBql(query);
            return null;
        }
        catch (QueryParseException ex)
        {
            return ex;
        }
    }

    private static bool StartsWithStage(string text)
    {
        var first = text.TrimStart().Split(' ', 2)[0].ToLowerInvariant();
        return first is "|" or "*" || (first != "find" && first != "search" && StageKeywords.Contains(first) && text.Contains('|'));
    }

    /// <summary>A segment with its first word, a mistyped stage name, replaced by the one close stage name.</summary>
    private static (string Text, string From, string To)? FixStageKeyword(string segment)
    {
        var parts = segment.Split(' ', 2);
        var word = parts[0];
        if (word.Length < 3 || StageKeywords.Contains(word.ToLowerInvariant()))
        {
            return null;
        }

        var allowed = word.Length < 6 ? 1 : 2;
        var close = StageKeywords.Select(k => (Keyword: k, Distance: Fuzzy.Distance(word, k))).Where(c => c.Distance <= allowed).OrderBy(c => c.Distance).ToList();
        if (close.Count == 0 || (close.Count > 1 && close[0].Distance == close[1].Distance))
        {
            return null;
        }

        return (close[0].Keyword + (parts.Length > 1 ? " " + parts[1] : string.Empty), word, close[0].Keyword);
    }

    private static QueryProblem ProblemFrom(string text, QueryParseException error)
    {
        var start = 0;
        var length = text.Length;
        if (error.Position is { } position && Regex.Match(position, @"\d+") is { Success: true } number && int.TryParse(number.Value, out var at) && at <= text.Length)
        {
            start = at;
            length = Math.Max(1, Regex.Match(text[at..], @"^\S+").Length);
        }

        string? fix = null;
        var message = error.Message;
        if (UnknownStage().Match(error.Message) is { Success: true } unknown)
        {
            var stage = unknown.Groups["stage"].Value;
            var near = Fuzzy.Closest(stage, StageKeywords);
            message = near != null ? $"\"{stage}\" isn't a command. Did you mean \"{near}\"?" : $"\"{stage}\" isn't a command. Try where, stats, top, sort, head or table.";
            if (near != null && text.IndexOf(stage, StringComparison.Ordinal) is var index and >= 0)
            {
                start = index;
                length = stage.Length;
                fix = text[..index] + near + text[(index + stage.Length)..];
            }
        }

        return new QueryProblem(start, length, message, fix);
    }

    [GeneratedRegex(@"\b(?:by|where|and|or)\s+(?<field>[A-Za-z_][\w.]*)\s*(?=$|,|\||\s|=|!=|<|>)", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex FieldUse();

    /// <summary>Field names in a strict query the data doesn't have, with the close one it likely meant.</summary>
    private static List<QueryProblem> FieldProblems(string text, FieldCatalog catalog)
    {
        var problems = new List<QueryProblem>();
        if (!catalog.HasData)
        {
            return problems;
        }

        foreach (Match match in FieldUse().Matches(text))
        {
            var field = match.Groups["field"];
            var name = field.Value;
            if (catalog.Field(name) != null || StageKeywords.Contains(name.ToLowerInvariant()) || name.ToLowerInvariant() is "not" or "and" or "or" or "count" or "desc" or "asc" or "like" or "in"
                || name.Contains('.') || name.StartsWith('_'))
            {
                continue;
            }

            if (Fuzzy.Closest(name, catalog.Fields) is { } near)
            {
                problems.Add(new QueryProblem(field.Index, field.Length, $"No events have a field \"{name}\". Did you mean \"{near}\"?", text[..field.Index] + near + text[(field.Index + field.Length)..]));
            }
        }

        return problems;
    }

    /// <summary>Splits on pipes outside quotes.</summary>
    internal static List<string> SplitPipes(string text)
    {
        var parts = new List<string>();
        var start = 0;
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '|')
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }

        parts.Add(text[start..]);
        return parts;
    }
}
