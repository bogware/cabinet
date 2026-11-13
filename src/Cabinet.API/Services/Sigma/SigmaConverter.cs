using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Cabinet.Core.Models;
using YamlDotNet.RepresentationModel;

namespace Cabinet.API.Services.Sigma;

/// <summary>A Sigma rule converted, or why it couldn't be.</summary>
/// <param name="Title">The rule's title.</param>
/// <param name="Rule">The detection rule, or null if it couldn't be converted.</param>
/// <param name="Problem">Why it couldn't, or what was left out.</param>
public sealed record SigmaConversion(string Title, DetectionRule? Rule, string? Problem);

/// <summary>
/// Converts Sigma rules (sigmahq.io) to detection rules: each selection becomes a BQL condition on
/// the event's fields, with Sigma's case-insensitive matching and value modifiers. Field names are
/// kept as Sigma writes them, so they match events whose parser uses the same names, such as
/// Windows events.
/// </summary>
public static class SigmaConverter
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex FieldName = new(@"^[A-Za-z_][A-Za-z0-9_.]*$", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex Technique = new(@"^attack\.(t\d{4}(?:\.\d{3})?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex CountCondition = new(@"^(?<expr>.+?)\s*\|\s*count\(\s*\)\s*(?:by\s+(?<by>[A-Za-z_][\w.]*)\s*)?(?<op>>=|>)\s*(?<n>\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>The most YAML text converted at once.</summary>
    public const int MaxInputLength = 2_000_000;

    /// <summary>How deeply maps and lists may nest; real Sigma rules use four or five levels.</summary>
    public const int MaxDepth = 50;

    /// <summary>The most value tests one rule's condition may hold.</summary>
    public const int MaxConditions = 5_000;

    /// <summary>Converts every rule in a YAML text; documents are separated by ---.</summary>
    public static List<SigmaConversion> Convert(string yaml)
    {
        if (yaml.Length > MaxInputLength)
        {
            return [Unreadable($"It's longer than {MaxInputLength} characters")];
        }

        var stream = new YamlStream();
        try
        {
            // Checked before loading, which expands aliases and builds nodes recursively
            if (Shape(yaml) is { } problem)
            {
                return [Unreadable(problem)];
            }

            stream.Load(new StringReader(yaml));
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            return [Unreadable($"Not valid YAML: {ex.Message}")];
        }

        return stream.Documents
            .Where(document => document.RootNode is YamlMappingNode)
            .Select(document => ConvertOne((YamlMappingNode)document.RootNode))
            .ToList();
    }

    private static SigmaConversion Unreadable(string problem) => new("(unreadable)", null, problem);

    /// <summary>
    /// Why the YAML can't be loaded safely, or null. Sigma rules don't use aliases, which can
    /// expand a few hundred bytes into gigabytes, and nesting is limited so nothing recurses deeply.
    /// The parser reads one event at a time, so it stops at the first level too deep.
    /// </summary>
    private static string? Shape(string yaml)
    {
        var parser = new YamlDotNet.Core.Parser(new StringReader(yaml));
        var depth = 0;
        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case YamlDotNet.Core.Events.AnchorAlias:
                    return "It uses a YAML alias (*name), which Sigma rules don't need";
                case YamlDotNet.Core.Events.MappingStart or YamlDotNet.Core.Events.SequenceStart:
                    if (++depth > MaxDepth) return $"It nests more than {MaxDepth} levels deep";
                    break;
                case YamlDotNet.Core.Events.MappingEnd or YamlDotNet.Core.Events.SequenceEnd:
                    depth--;
                    break;
            }
        }

        return null;
    }

    /// <summary>Counts the value tests in one rule, so a small rule can't make a huge condition.</summary>
    private sealed class Budget
    {
        public int Used { get; private set; }

        public void Spend(int tests)
        {
            Used += tests;
            if (Used > MaxConditions)
            {
                throw new NotSupportedException($"It makes more than {MaxConditions} conditions");
            }
        }
    }

    private static SigmaConversion ConvertOne(YamlMappingNode root)
    {
        var title = Scalar(root, "title") ?? "(untitled Sigma rule)";
        try
        {
            if (Child(root, "detection") is not YamlMappingNode detection)
            {
                return new SigmaConversion(title, null, "It has no detection section");
            }

            var conditionText = Scalar(detection, "condition")
                ?? throw new NotSupportedException("It has no condition");
            string? groupBy = null;
            var threshold = 1;
            if (CountCondition.Match(conditionText) is { Success: true } count)
            {
                conditionText = count.Groups["expr"].Value;
                groupBy = count.Groups["by"].Success ? count.Groups["by"].Value : null;
                var n = int.Parse(count.Groups["n"].Value, CultureInfo.InvariantCulture);
                threshold = count.Groups["op"].Value == ">" ? n + 1 : n;
            }
            else if (conditionText.Contains('|'))
            {
                throw new NotSupportedException("Its condition aggregates in a way only count() is converted");
            }

            var selections = detection.Children
                .Where(pair => pair.Key is YamlScalarNode { Value: not ("condition" or "timeframe") })
                .ToDictionary(pair => ((YamlScalarNode)pair.Key).Value!, pair => Counted(pair.Value), StringComparer.Ordinal);
            var condition = new ConditionParser(conditionText, selections, new Budget()).Parse();

            var level = Scalar(root, "level")?.ToLowerInvariant();
            var technique = Sequence(root, "tags")
                .Select(tag => Technique.Match(tag))
                .FirstOrDefault(match => match.Success)?.Groups[1].Value.ToUpperInvariant();
            var logsource = Child(root, "logsource") is YamlMappingNode source
                ? string.Join(", ", source.Children.Select(pair => $"{((YamlScalarNode)pair.Key).Value}={(pair.Value as YamlScalarNode)?.Value}"))
                : null;
            var description = new StringBuilder(Scalar(root, "description") ?? string.Empty);
            description.Append($"\n\nImported from Sigma{(Scalar(root, "id") is { } id ? $" rule {id}" : string.Empty)}");
            if (logsource != null) description.Append($" (log source: {logsource})");
            if (Scalar(root, "falsepositives") is { } falsePositives) description.Append($". False positives: {falsePositives}");
            else if (Sequence(root, "falsepositives").ToList() is { Count: > 0 } list) description.Append($". False positives: {string.Join("; ", list)}");

            var window = Scalar(detection, "timeframe") is { } timeframe ? Seconds(timeframe) : 300;
            return new SigmaConversion(title, new DetectionRule
            {
                Name = title.Length > 200 ? title[..200] : title,
                Description = description.ToString().Trim(),
                Condition = condition,
                Severity = level switch
                {
                    "critical" => "Critical",
                    "high" => "Error",
                    "medium" => "Warning",
                    _ => "Info",
                },
                Technique = technique,
                GroupBy = groupBy,
                Threshold = threshold,
                WindowSeconds = window,

                // An imported rule says nothing until someone has checked it against their logs
                Enabled = false,
            }, null);
        }
        catch (NotSupportedException ex)
        {
            return new SigmaConversion(title, null, ex.Message);
        }
    }

    /// <summary>A selection's condition, and how many value tests it holds.</summary>
    private sealed record CountedSelection(string Condition, int Tests);

    private static CountedSelection Counted(YamlNode node)
    {
        var budget = new Budget();
        var condition = Selection(node, budget);
        return new CountedSelection(condition, budget.Used);
    }

    /// <summary>A selection as a BQL condition: a map is AND of its fields, a list is OR of its items.</summary>
    private static string Selection(YamlNode node, Budget budget)
    {
        switch (node)
        {
            case YamlMappingNode map:
                return Join(map.Children.Select(pair => FieldCondition(((YamlScalarNode)pair.Key).Value!, pair.Value, budget)), " AND ");
            case YamlSequenceNode list when list.Children.All(item => item is YamlScalarNode):
                budget.Spend(list.Children.Count);
                return Join(list.Children.Select(item => Match("RawLine", ((YamlScalarNode)item).Value ?? string.Empty, "contains")), " OR ");
            case YamlSequenceNode list:
                return Join(list.Children.Select(item => Selection(item, budget)), " OR ");
            case YamlScalarNode scalar:
                budget.Spend(1);
                return Match("RawLine", scalar.Value ?? string.Empty, "contains");
            default:
                throw new NotSupportedException("A selection is neither a map nor a list");
        }
    }

    private static string FieldCondition(string key, YamlNode value, Budget budget)
    {
        var parts = key.Split('|');
        var field = parts[0];
        if (!FieldName.IsMatch(field))
        {
            throw new NotSupportedException($"The field name '{field}' can't be used in a condition");
        }

        var modifiers = parts.Skip(1).Select(m => m.ToLowerInvariant()).ToList();
        var all = modifiers.Remove("all");
        var unsupported = modifiers.FirstOrDefault(m => m is not ("contains" or "startswith" or "endswith" or "re" or "cidr" or "windash"));
        if (unsupported != null)
        {
            throw new NotSupportedException($"The value modifier |{unsupported} isn't supported");
        }

        var kind = modifiers.FirstOrDefault(m => m is "contains" or "startswith" or "endswith" or "re" or "cidr") ?? "equals";
        var windash = modifiers.Contains("windash");
        var values = value switch
        {
            YamlSequenceNode list => list.Children.Select(item => (item as YamlScalarNode)?.Value).ToList(),
            YamlScalarNode scalar => [scalar.Value],
            _ => throw new NotSupportedException($"The value for {field} isn't a value or a list"),
        };
        budget.Spend(windash ? values.Count * 4 : values.Count);

        // windash: a Windows option can start with - or / (or a dash look-alike), wherever it is
        var conditions = values.SelectMany(v => windash && v != null && v.Contains('-')
                ? new[] { v, v.Replace('-', '/'), v.Replace('-', '–'), v.Replace('-', '—') }
                : new[] { v })
            .Select(v => v == null || v is "null" ? $"isnull({field})" : Match(field, v, kind))
            .ToList();
        return Join(conditions, all ? " AND " : " OR ");
    }

    /// <summary>One value test. Sigma matches case-insensitively, and * and ? are wildcards unless escaped.</summary>
    private static string Match(string field, string value, string kind)
    {
        if (kind == "re")
        {
            return $"match({field}, {Quote(value)})";
        }

        if (kind == "cidr")
        {
            return $"cidrmatch({Quote(value)}, {field})";
        }

        var hasWildcard = Regex.IsMatch(value, @"(?<!\\)[*?]", RegexOptions.None, RegexTimeout);
        if (kind == "equals" && !hasWildcard)
        {
            return $"{field} = {Quote(Unescape(value))}";
        }

        var body = WildcardRegex(value);
        var pattern = kind switch
        {
            "contains" => body,
            "startswith" => $"^{body}",
            "endswith" => $"{body}$",
            _ => $"^{body}$",
        };
        return $"match({field}, {Quote(pattern)})";
    }

    private static string WildcardRegex(string value)
    {
        var regex = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\' && i + 1 < value.Length && value[i + 1] is '*' or '?' or '\\')
            {
                regex.Append(Regex.Escape(value[++i].ToString()));
            }
            else if (c == '*')
            {
                regex.Append(".*");
            }
            else if (c == '?')
            {
                regex.Append('.');
            }
            else
            {
                regex.Append(Regex.Escape(c.ToString()));
            }
        }

        return regex.ToString();
    }

    private static string Unescape(string value) => value.Replace(@"\*", "*").Replace(@"\?", "?").Replace(@"\\", @"\");

    private static string Quote(string value) => $"\"{value.Replace(@"\", @"\\").Replace("\"", "\\\"")}\"";

    private static string Join(IEnumerable<string> parts, string separator)
    {
        var list = parts.ToList();
        return list.Count switch
        {
            0 => throw new NotSupportedException("A selection is empty"),
            1 => list[0],
            _ => $"({string.Join(separator, list)})",
        };
    }

    private static int Seconds(string timeframe)
    {
        var match = Regex.Match(timeframe.Trim(), @"^(\d+)([smhd])$", RegexOptions.None, RegexTimeout);
        if (!match.Success) return 300;
        var n = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return Math.Min(n * match.Groups[2].Value switch { "s" => 1, "m" => 60, "h" => 3600, _ => 86400 }, 7 * 86400);
    }

    private static YamlNode? Child(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var child) ? child : null;

    private static string? Scalar(YamlMappingNode node, string key) => (Child(node, key) as YamlScalarNode)?.Value;

    private static IEnumerable<string> Sequence(YamlMappingNode node, string key) =>
        Child(node, key) is YamlSequenceNode list ? list.Children.OfType<YamlScalarNode>().Select(s => s.Value ?? string.Empty) : [];

    /// <summary>Sigma's condition: names, and, or, not, parentheses, "1 of x*", "all of them".</summary>
    private sealed class ConditionParser(string text, Dictionary<string, CountedSelection> selections, Budget budget)
    {
        private readonly List<string> _tokens = Regex.Matches(text, @"\(|\)|[^\s()]+", RegexOptions.None, RegexTimeout).Select(m => m.Value).ToList();
        private int _position;
        private int _depth;

        public string Parse()
        {
            var result = Or();
            if (_position < _tokens.Count) throw new NotSupportedException($"The condition has '{_tokens[_position]}' where it should end");
            return result;
        }

        private string? Peek => _position < _tokens.Count ? _tokens[_position] : null;

        private string Or()
        {
            var parts = new List<string> { And() };
            while (Peek?.Equals("or", StringComparison.OrdinalIgnoreCase) == true) { _position++; parts.Add(And()); }
            return parts.Count == 1 ? parts[0] : $"({string.Join(" OR ", parts)})";
        }

        private string And()
        {
            var parts = new List<string> { Not() };
            while (Peek?.Equals("and", StringComparison.OrdinalIgnoreCase) == true) { _position++; parts.Add(Not()); }
            return parts.Count == 1 ? parts[0] : $"({string.Join(" AND ", parts)})";
        }

        private string Not()
        {
            if (Peek?.Equals("not", StringComparison.OrdinalIgnoreCase) == true)
            {
                _position++;
                return $"NOT {Nested(Not)}";
            }

            return Primary();
        }

        private string Primary()
        {
            var token = Peek ?? throw new NotSupportedException("The condition ends too soon");
            _position++;
            if (token == "(")
            {
                var inner = Nested(Or);
                if (Peek != ")") throw new NotSupportedException("The condition is missing a ')'");
                _position++;
                return inner;
            }

            if ((token is "1" or "all" || token.Equals("any", StringComparison.OrdinalIgnoreCase)) && Peek?.Equals("of", StringComparison.OrdinalIgnoreCase) == true)
            {
                _position++;
                var target = Peek ?? throw new NotSupportedException("'of' needs a name after it");
                _position++;
                var names = target == "them"
                    ? selections.Keys.Where(k => !k.StartsWith('_')).ToList()
                    : selections.Keys.Where(k => Regex.IsMatch(k, "^" + Regex.Escape(target).Replace(@"\*", ".*") + "$", RegexOptions.None, RegexTimeout)).ToList();
                if (names.Count == 0) throw new NotSupportedException($"No selection matches '{target}'");
                return names.Count == 1 ? Use(names[0]) : $"({string.Join(token == "all" ? " AND " : " OR ", names.Select(Use))})";
            }

            return selections.ContainsKey(token)
                ? Use(token)
                : throw new NotSupportedException($"The condition names '{token}', which isn't a selection");
        }

        // "not" and "(" recurse, so a condition can't nest without limit
        private string Nested(Func<string> parse)
        {
            if (++_depth > MaxDepth) throw new NotSupportedException($"The condition nests more than {MaxDepth} levels deep");
            var result = parse();
            _depth--;
            return result;
        }

        // A selection named again is written out again, so its tests count every time
        private string Use(string name)
        {
            var selection = selections[name];
            budget.Spend(selection.Tests);
            return selection.Condition;
        }
    }
}
