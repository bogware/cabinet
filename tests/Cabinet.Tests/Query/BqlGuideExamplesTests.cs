using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Cabinet.Core.Models;
using Cabinet.Parsers;
using Cabinet.Query;
using Cabinet.Query.Bql;
using Cabinet.Query.NaturalLanguage;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Query;

/// <summary>
/// Runs every query in docs/BQL-GUIDE.md, so a documented example can't stop working unnoticed.
/// Results of each feature are checked in <see cref="BqlLanguageTests"/>.
/// </summary>
public class BqlGuideExamplesTests
{
    private static readonly Lazy<BqlQueryEngine> Engine = new(() => CreateEngine().GetAwaiter().GetResult());

    private static string GuidePath([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", "docs", "BQL-GUIDE.md"));

    public static IEnumerable<object[]> Examples() =>
        ExtractExamples(File.ReadAllText(GuidePath())).Distinct().Select(example => new object[] { example });

    [Fact]
    public void Guide_HasExamples()
    {
        Examples().Count().Should().BeGreaterThan(150, "extraction should find the guide's examples");
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public async Task Example_Runs(string query)
    {
        var result = await Engine.Value.ExecuteAsync(new QueryExecutionContext { Query = query, Limit = 1000 });

        result.Should().NotBeNull();
    }

    private static IEnumerable<string> ExtractExamples(string guide)
    {
        var inFence = false;
        var section = string.Empty;
        foreach (var rawLine in guide.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                var query = Regex.Replace(line, @"\s+#\s.*$", string.Empty).Trim();
                if (query.Length > 0 && !query.StartsWith('#') && !query.StartsWith('<'))
                {
                    yield return query;
                }

                continue;
            }

            if (line.StartsWith('#'))
            {
                section = line;
                continue;
            }

            if (!line.StartsWith('|') || line.StartsWith("|--", StringComparison.Ordinal))
            {
                continue;
            }

            var cells = Regex.Split(line.Trim('|'), @"(?<!\\)\|")
                .Select(cell => cell.Trim().Replace("\\|", "|"))
                .ToArray();

            // Function tables end with an example eval.
            var last = Regex.Match(cells[^1], "^`(eval .*)`$");
            if (last.Success)
            {
                yield return $"find * | {last.Groups[1].Value}";
            }

            // SPL and natural language tables show both forms of a query.
            if (section.Contains("SPL Compatibility", StringComparison.Ordinal)
                || section.Contains("Natural Language", StringComparison.Ordinal))
            {
                foreach (var cell in cells)
                {
                    var match = Regex.Match(cell, "^`(.*)`$");
                    if (match.Success)
                    {
                        yield return match.Groups[1].Value.Replace(@"\\", @"\");
                    }
                }
            }
        }
    }

    private static async Task<BqlQueryEngine> CreateEngine()
    {
        var storage = new InMemoryStorage();
        await storage.InitializeAsync();

        var knowledge = new InMemoryKnowledgeStorage();
        await knowledge.InitializeAsync();
        await knowledge.SaveLookupAsync(new LookupTable
        {
            Name = "users",
            Columns = ["user_id", "email", "name", "active"],
            Rows =
            [
                new Dictionary<string, string> { ["user_id"] = "1", ["email"] = "a@example.com", ["name"] = "Alice", ["active"] = "true" },
            ],
        });
        await knowledge.SaveLookupAsync(new LookupTable
        {
            Name = "my_lookup_table",
            Columns = ["key", "value"],
            Rows = [new Dictionary<string, string> { ["key"] = "a", ["value"] = "b" }],
        });

        var parser = new JsonLogParser();
        var events = new List<LogEvent>();
        for (var i = 0; i < 20; i++)
        {
            var line = $$"""
                {"timestamp":"2026-01-01T10:{{i:00}}:00Z","level":"{{(i % 3 == 0 ? "ERROR" : "INFO")}}","message":"user=alice{{i % 2}} connected from 10.0.0.{{i}}:80{{i % 10}}","host":"web-0{{i % 3}}","status":{{(i % 2 == 0 ? 200 : 500)}},"duration":{{100 + i}},"latency":{{i * 1.5}},"response_time":{{i * 2}},"bytes":{{i * 100}},"user":"u{{i % 4}}","user_id":"{{i % 2}}","endpoint":"/e{{i % 2}}","service":"s{{i % 2}}","method":"GET","session_id":"s{{i % 5}}","first_name":"Ada","last_name":"Lovelace","price":{{i}},"quantity":2,"qty":3,"memory":"1.5GB","number":"1,234","value":"100ms","msg":"id 42 from 1.2.3.4","source":"app","old_field":"x","path":"/a/b","file":"a.log"}
                """;
            events.Add((await parser.ParseAsync(line.Trim()))!);
        }

        await storage.StoreBatchAsync(events);

        return new BqlQueryEngine(
            storage,
            new BqlParser(),
            new BqlOptimizer(),
            NullLogger<BqlQueryEngine>.Instance,
            new RuleBasedTranslator(),
            knowledge);
    }
}
