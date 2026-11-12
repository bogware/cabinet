using Cabinet.Core.Models;
using Cabinet.Parsers;
using Cabinet.Query;
using Cabinet.Query.Bql;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cabinet.CLI.Services;

/// <summary>
/// A detection rule kept as a YAML file, so rules can live in git, be reviewed, and be tested
/// before they're pushed. Its tests are log lines and whether the rule should match each.
/// </summary>
public sealed class RuleFile
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string Severity { get; set; } = "Warning";
    public bool Enabled { get; set; } = true;
    public string? Pattern { get; set; }
    public string? Condition { get; set; }
    public string? Extract { get; set; }
    public string? GroupBy { get; set; }
    public int Threshold { get; set; } = 1;
    public int WindowSeconds { get; set; } = 300;
    public int CooldownSeconds { get; set; } = 300;
    public string? Technique { get; set; }
    public List<RuleTest> Tests { get; set; } = [];

    /// <summary>The rule as the server takes it.</summary>
    public DetectionRule ToRule() => new()
    {
        Id = Id!,
        Name = Name!,
        Description = Description,
        Severity = Severity,
        Enabled = Enabled,
        Pattern = Pattern,
        Condition = Condition,
        Extract = Extract,
        GroupBy = GroupBy,
        Threshold = Threshold,
        WindowSeconds = WindowSeconds,
        CooldownSeconds = CooldownSeconds,
        Technique = Technique,
    };

    /// <summary>A rule as a file, for pulling rules into a repository.</summary>
    public static RuleFile From(DetectionRule rule) => new()
    {
        Id = rule.Id,
        Name = rule.Name,
        Description = rule.Description,
        Severity = rule.Severity,
        Enabled = rule.Enabled,
        Pattern = rule.Pattern,
        Condition = rule.Condition,
        Extract = rule.Extract,
        GroupBy = rule.GroupBy,
        Threshold = rule.Threshold,
        WindowSeconds = rule.WindowSeconds,
        CooldownSeconds = rule.CooldownSeconds,
        Technique = rule.Technique,
    };
}

/// <summary>One test: a log line, and whether the rule should match it.</summary>
public sealed class RuleTest
{
    public string? Line { get; set; }
    public bool Match { get; set; } = true;

    /// <summary>For a match: fields its extract should give, by name.</summary>
    public Dictionary<string, string>? Fields { get; set; }
}

/// <summary>What testing one rule file found.</summary>
public sealed record RuleFileResult(string Path, RuleFile? Rule, List<string> Problems, int TestsRun);

/// <summary>Loads, checks and tests rule files.</summary>
public static class RuleFiles
{
    private static readonly IDeserializer Reader = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    private static readonly ISerializer Writer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
        .Build();

    /// <summary>The rule files at a path: a file, or every .yaml and .yml under a directory.</summary>
    public static IEnumerable<string> Find(string path) =>
        File.Exists(path)
            ? [path]
            : Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.Ordinal)
                : throw new FileNotFoundException($"No rule file or directory at {path}");

    public static string Serialize(RuleFile rule) => Writer.Serialize(rule);

    /// <summary>Checks a file and runs its tests through the same matcher the server uses.</summary>
    public static async Task<RuleFileResult> TestAsync(string path, LogLineParser parser, CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();
        RuleFile? rule;
        try
        {
            rule = Reader.Deserialize<RuleFile>(await File.ReadAllTextAsync(path, cancellationToken));
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            return new RuleFileResult(path, null, [$"Not a valid rule file: {ex.Message}"], 0);
        }

        if (rule == null)
        {
            return new RuleFileResult(path, null, ["The file is empty"], 0);
        }

        if (string.IsNullOrWhiteSpace(rule.Id) || !System.Text.RegularExpressions.Regex.IsMatch(rule.Id, "^[A-Za-z0-9._-]{1,64}$"))
            problems.Add("id is required: 1 to 64 letters, digits, '.', '_' or '-'");
        if (string.IsNullOrWhiteSpace(rule.Name))
            problems.Add("name is required");
        if (string.IsNullOrWhiteSpace(rule.Pattern) && string.IsNullOrWhiteSpace(rule.Condition))
            problems.Add("a rule needs a pattern, a condition, or both");
        if (rule.Severity is not ("Info" or "Warning" or "Error" or "Critical"))
            problems.Add("severity is Info, Warning, Error or Critical");
        if (rule.Threshold < 1 || rule.WindowSeconds < 1 || rule.CooldownSeconds < 0)
            problems.Add("threshold and window_seconds are at least 1, and cooldown_seconds isn't negative");

        BqlEventMatcher matcher;
        try
        {
            matcher = new BqlEventMatcher(rule.Pattern, rule.Condition, rule.Extract);
        }
        catch (Exception ex) when (ex is QueryParseException or ArgumentException)
        {
            problems.Add($"The rule doesn't compile: {ex.Message}");
            return new RuleFileResult(path, rule, problems, 0);
        }

        if (rule.Tests.Count == 0)
        {
            problems.Add("tests: add at least one line it should match and one it shouldn't");
        }

        var number = 0;
        foreach (var test in rule.Tests)
        {
            number++;
            if (string.IsNullOrEmpty(test.Line))
            {
                problems.Add($"test {number} has no line");
                continue;
            }

            var logEvent = await parser.ParseAsync(test.Line, cancellationToken: cancellationToken);
            var matched = matcher.TryMatch(logEvent, out var fields);
            if (matched != test.Match)
            {
                problems.Add($"test {number} should {(test.Match ? "match" : "not match")}: {test.Line}");
                continue;
            }

            foreach (var (name, expected) in test.Fields ?? [])
            {
                var actual = fields.TryGetValue(name, out var value) ? value?.ToString() : logEvent.GetFieldString(name);
                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                {
                    problems.Add($"test {number}: {name} should be '{expected}', not '{actual ?? "(none)"}'");
                }
            }
        }

        return new RuleFileResult(path, rule, problems, rule.Tests.Count);
    }
}
