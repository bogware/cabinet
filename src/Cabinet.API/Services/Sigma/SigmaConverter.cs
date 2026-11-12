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

    /// <summary>Converts every rule in a YAML text; documents are separated by ---.</summary>
    public static List<SigmaConversion> Convert(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            return [new SigmaConversion("(unreadable)", null, $"Not valid YAML: {ex.Message}")];
        }

        return stream.Documents
            .Where(document => document.RootNode is YamlMappingNode)
            .Select(document => ConvertOne((YamlMappingNode)document.RootNode))
            .ToList();
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
                .ToDictionary(pair => ((YamlScalarNode)pair.Key).Value!, pair => Selection(pair.Value), StringComparer.Ordinal);
            var condition = new ConditionParser(conditionText, selections).Parse();

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

    /// <summary>A selection as a BQL condition: a map is AND of its fields, a list is OR of its items.</summary>
    private static string Selection(YamlNode node) => node switch
    {
        YamlMappingNode map => Join(map.Children.Select(pair => FieldCondition(((YamlScalarNode)pair.Key).Value!, pair.Value)), " AND "),
        YamlSequenceNode list when list.Children.All(item => item is YamlScalarNode) =>
            Join(list.Children.Select(item => Match("RawLine", ((YamlScalarNode)item).Value ?? string.Empty, "contains")), " OR "),
        YamlSequenceNode list => Join(list.Children.Select(Selection), " OR "),
        YamlScalarNode scalar => Match("RawLine", scalar.Value ?? string.Empty, "contains"),
        _ => throw new NotSupportedException("A selection is neither a map nor a list"),
    };

    private static string FieldCondition(string key, YamlNode value)
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
    private sealed class ConditionParser(string text, Dictionary<string, string> selections)
    {
        private readonly List<string> _tokens = Regex.Matches(text, @"\(|\)|[^\s()]+", RegexOptions.None, RegexTimeout).Select(m => m.Value).ToList();
        private int _position;

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
                return $"NOT {Not()}";
            }

            return Primary();
        }

        private string Primary()
        {
            var token = Peek ?? throw new NotSupportedException("The condition ends too soon");
            _position++;
            if (token == "(")
            {
                var inner = Or();
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
                return names.Count == 1 ? selections[names[0]] : $"({string.Join(token == "all" ? " AND " : " OR ", names.Select(n => selections[n]))})";
            }

            return selections.TryGetValue(token, out var selection)
                ? selection
                : throw new NotSupportedException($"The condition names '{token}', which isn't a selection");
        }
    }
}
