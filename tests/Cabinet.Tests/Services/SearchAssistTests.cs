using System.Runtime.CompilerServices;
using System.Text.Json;
using Cabinet.Query.Bql;
using FluentAssertions;
using Jint;
using Xunit;

namespace Cabinet.Tests.Services;

/// <summary>
/// The search page's help: the guided builder's BQL, problem marks under the editor, and merging
/// the server's suggestions with the page's own. Run from wwwroot/search.js without a browser.
/// </summary>
public sealed class SearchAssistTests
{
    private static string Asset(string name, [CallerFilePath] string testFile = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!, "..", "..", "..", "src", "Cabinet.API", "wwwroot", name)));

    private static readonly Lazy<Engine> Js = new(() =>
    {
        var engine = new Engine();
        engine.Execute(Asset("search.js"));
        return engine;
    });

    private static string Build(object spec) =>
        Js.Value.Evaluate($"CabinetSearch.buildQuery({JsonSerializer.Serialize(spec)})").AsString();

    public static TheoryData<string, string> Specs => new()
    {
        { """{"show":"events"}""", "find *" },
        { """{"words":"timeout","time":"1h","show":"events","limit":"50"}""", "find timeout from last 1h | head 50" },
        { """{"time":"yesterday","conditions":[{"field":"host","op":"=","value":"web-01"},{"field":"status","op":">=","value":"500"}],"show":"count","by":["path"]}""",
          "find * earliest=-1d@d latest=@d | where host = \"web-01\" and status >= 500 | stats count by path | sort by count desc" },
        { """{"show":"measure","measure":{"fn":"perc95","field":"duration_ms"},"by":["host"]}""",
          "find * | stats perc95(duration_ms) as perc95_duration_ms by host | sort by perc95_duration_ms desc" },
        { """{"show":"top","field":"user","limit":"5"}""", "find * | top 5 user" },
        { """{"show":"rare","field":"status"}""", "find * | rare 10 status" },
        { """{"time":"24h","show":"overtime","by":["host"]}""", "find * from last 24h | timechart span=1h count by host" },
        { """{"time":"15m","show":"overtime","measure":{"fn":"avg","field":"bytes"}}""", "find * from last 15m | timechart span=1m avg(bytes)" },
        { """{"show":"table","columns":["host","path"],"conditions":[{"field":"path","op":"contains","value":"api"},{"field":"user","op":"missing"}]}""",
          "find * | where path like \"%api%\" and isnull(user) | table host, path" },
        { """{"show":"events","conditions":[{"field":"user","op":"=","value":"o\"brien"},{"field":"","op":"=","value":"ignored"},{"field":"host","op":"=","value":""}]}""",
          "find * | where user = \"o\\\"brien\"" },
    };

    [Theory]
    [MemberData(nameof(Specs))]
    public void TheBuilder_WritesBql_ThatParses(string spec, string expected)
    {
        var query = Build(JsonSerializer.Deserialize<JsonElement>(spec));

        query.Should().Be(expected);
        new BqlParser().Invoking(p => p.ParseBql(query)).Should().NotThrow();
    }

    [Fact]
    public void Problems_AreMarked_AndTheTextEscaped()
    {
        var html = Js.Value.Evaluate("""CabinetSearch.markProblems('find <b> | sotr by x', [{start: 11, length: 4, message: 'Did you mean "sort"?'}])""").AsString();

        html.Should().Be("find &lt;b&gt; | <mark class=\"sx-problem\" title=\"Did you mean &quot;sort&quot;?\">sotr</mark> by x");
    }

    [Fact]
    public void OverlappingOrOutOfRangeProblems_DontBreakTheText()
    {
        var html = Js.Value.Evaluate("CabinetSearch.markProblems('abcdef', [{start: 1, length: 3}, {start: 2, length: 2}, {start: 40, length: 2}])").AsString();

        html.Should().Be("a<mark class=\"sx-problem\" title=\"\">bcd</mark>ef");
    }

    [Fact]
    public void ServerSuggestions_JoinThePagesOwn_WithoutRepeats_KeepingWhereTheyApply()
    {
        var merged = Js.Value.Evaluate("""
            JSON.stringify(CabinetSearch.mergeCompletions(
                [{ label: 'host', kind: 'field', insert: 'host' }],
                [{ text: 'host', kind: 'field', description: 'x', start: 9, length: 2 },
                 { text: 'how many', kind: 'phrase', description: 'Count something', start: 0, length: 5 }],
                9, 11).map(i => [i.insert, i.kind, i.from, i.to]))
            """).AsString();

        merged.Should().Be("""[["host","field",9,11],["how many","phrase",0,5]]""");
    }
}
