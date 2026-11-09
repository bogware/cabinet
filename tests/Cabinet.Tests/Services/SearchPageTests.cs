using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Jint;
using Xunit;

namespace Cabinet.Tests.Services;

/// <summary>
/// The search page's logic (wwwroot/search.js), run without a browser: what a click puts in the
/// query, what the timeline counts, and what reaches the page as HTML.
/// </summary>
public sealed class SearchPageTests
{
    private static string Asset(string name, [CallerFilePath] string testFile = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!, "..", "..", "..", "src", "Cabinet.API", "wwwroot", name)));

    private static readonly string Script = Asset("search.js");

    private static Engine Search()
    {
        var engine = new Engine(options => options.LocalTimeZone(TimeZoneInfo.Utc));
        engine.Execute(Script);
        return engine;
    }

    private static string Eval(string expression) => Search().Evaluate($"JSON.stringify({expression})").AsString();

    private static T Eval<T>(string expression) => JsonSerializer.Deserialize<T>(Eval(expression))!;

    [Theory]
    [InlineData("find error", "Map", "\"c1m1_hotel\"", "find error | where Map = \"c1m1_hotel\"")]
    [InlineData("find * | where x = 1 | stats count by host", "Weapon", "\"smg\"", "find * | where x = 1 | where Weapon = \"smg\" | stats count by host")]
    [InlineData("find *", "status", "500", "find * | where status = 500")]
    [InlineData("find *", "metric.name", "\"orders\"", "find * | where metric.name = \"orders\"")]
    [InlineData("find *", "Host", "\"l4d2-dev\"", "find * | where host = \"l4d2-dev\"")]
    [InlineData("find *", "Severity", "\"Warning\"", "find * | where severity = 3")]
    [InlineData("find *", "Team", "null", "find * | where isnull(Team)")]
    [InlineData("find *", "Victim", "\"a \\\"b\\\" \\\\c\"", "find * | where Victim = \"a \\\"b\\\" \\\\c\"")]
    [InlineData("", "Map", "\"x\"", "find * | where Map = \"x\"")]
    public void Filters_GoAfterTheSearch_InValidSyntax(string query, string field, string valueJs, string expected)
    {
        Eval<string>($"CabinetSearch.addFilter({JsonSerializer.Serialize(query)}, {JsonSerializer.Serialize(field)}, {valueJs}, false)").Should().Be(expected);
    }

    [Fact]
    public void Exclusion_UsesNotEqual_AndRepeatsAreNotAdded()
    {
        Eval<string>("CabinetSearch.addFilter('find *', 'Map', 'x', true)").Should().Be("find * | where Map != \"x\"");
        Eval<string>("CabinetSearch.addFilter('find * | where Map = \"x\"', 'Map', 'x', false)").Should().Be("find * | where Map = \"x\"");
    }

    [Fact]
    public void AFieldBqlCantName_GetsNoFilter()
    {
        Eval("CabinetSearch.addFilter('find *', 'my field', 'x', false)").Should().Be("null");
    }

    [Fact]
    public void Pipelines_SplitOutsideQuotesAndBrackets()
    {
        Eval<string[]>("CabinetSearch.splitPipeline('find \"a|b\" | where x=\"c|d\" | rex \"(?<y>a|b)\"')")
            .Should().Equal("find \"a|b\"", "where x=\"c|d\"", "rex \"(?<y>a|b)\"");
    }

    [Theory]
    [InlineData("find error | where x=1 | head 5", true)]
    [InlineData("find error | stats count", false)]
    [InlineData("find * | timechart span=1h count", false)]
    [InlineData("find *", true)]
    public void WhetherResultsAreEvents(string query, bool events)
    {
        Eval<bool>($"CabinetSearch.returnsEvents({JsonSerializer.Serialize(query)})").Should().Be(events);
    }

    [Fact]
    public void TheTimeline_CountsTheSearchAndItsFilters_NotWhatFollows()
    {
        Eval<string>("CabinetSearch.timelineQuery('find error | where x=1 | stats count by host', '5m')")
            .Should().Be("find error | where x=1 | timechart span=5m count");
    }

    [Theory]
    [InlineData(15 * 60, "30s")]
    [InlineData(3600, "1m")]
    [InlineData(24 * 3600, "30m")]
    [InlineData(7 * 24 * 3600, "3h")]
    [InlineData(365 * 24 * 3600, "7d")]
    public void Buckets_AreRound_AndAtMostSixtyBars(int seconds, string span)
    {
        Eval<JsonElement>($"CabinetSearch.chooseSpan(0, {seconds}000)").GetProperty("span").GetString().Should().Be(span);
    }

    [Fact]
    public void Ranges_ResolveFromTheirChoice()
    {
        const long now = 1_800_000_000_000;
        Eval<JsonElement>($"CabinetSearch.resolveRange({{ preset: '24h' }}, {now})").GetProperty("start").GetInt64().Should().Be(now - 86_400_000);
        Eval<JsonElement>($"CabinetSearch.resolveRange({{ last: 3, unit: 'd' }}, {now})").GetProperty("label").GetString().Should().Be("Last 3 days");
        Eval<JsonElement>("CabinetSearch.resolveRange({ preset: 'all' })").GetProperty("start").ValueKind.Should().Be(JsonValueKind.Null);
        var between = Eval<JsonElement>("CabinetSearch.resolveRange({ from: '2026-11-08T02:00:00Z', to: '2026-11-08T04:30:00Z' })");
        between.GetProperty("end").GetInt64().Should().Be(new DateTimeOffset(2026, 11, 8, 4, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Links_RoundTripTheSearch()
    {
        var state = Eval<JsonElement>("CabinetSearch.decodeState(CabinetSearch.encodeState({ query: 'find \"a b\" | where x = 1 & y', range: { last: 3, unit: 'h' }, index: 'l4d2' }))");

        state.GetProperty("query").GetString().Should().Be("find \"a b\" | where x = 1 & y");
        state.GetProperty("range").GetProperty("last").GetInt32().Should().Be(3);
        state.GetProperty("index").GetString().Should().Be("l4d2");
        Eval("CabinetSearch.decodeState('#settings')").Should().Be("null");
    }

    [Theory]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("\" onmouseover=\"alert(1)")]
    [InlineData("<script>alert(1)</script> error")]
    public void Highlighting_NeverLetsMarkupThrough(string line)
    {
        var html = Eval<string>($"CabinetSearch.highlight({JsonSerializer.Serialize(line)}, ['error', 'img', 'onmouseover'])");

        html.Should().NotContain("<img").And.NotContain("<script").And.NotContain("\" on");
        html.Replace("<mark>", string.Empty).Replace("</mark>", string.Empty).Should().NotContain("<").And.NotContain(">");
    }

    [Fact]
    public void Highlighting_MarksWholeWordsInAnyCase()
    {
        Eval<string>("CabinetSearch.highlight('Error: reconnection failed', ['error', 'connection'])").Should().Be("<mark>Error</mark>: reconnection failed");
    }

    [Fact]
    public void QueryHighlighting_KeepsTheQuery_AndEscapesIt()
    {
        const string query = "find \"<b>\" from last 24h | stats count(x) as n by host";
        var html = Eval<string>($"CabinetSearch.highlightQuery({JsonSerializer.Serialize(query)})");

        html.Should().Contain("<span class=\"q-command\">find</span>").And.Contain("&lt;b&gt;").And.NotContain("<b>");
        Eval<string>($"CabinetSearch.tokenize({JsonSerializer.Serialize(query)}).map(t => t.text).join('')").Should().Be(query);
        Eval<string>("CabinetSearch.tokenize('find * | stats count by host').find(t => t.text === 'count').type").Should().Be("function");
    }

    [Fact]
    public void Autocomplete_OffersWhatFitsWhereTheCaretIs()
    {
        Eval<string[]>("CabinetSearch.complete('find * | st', 11, []).items.map(i => i.label)").Should().Equal("stats", "streamstats");
        Eval<string[]>("CabinetSearch.complete('find * | stats count by Ma', 26, ['Map', 'Weapon', 'Host']).items.map(i => i.label)").Should().Equal("Map");
        Eval<string[]>("CabinetSearch.complete('find * | where is', 17, []).items.map(i => i.label)").Should().Contain("isnull");
        var replace = Eval<JsonElement>("CabinetSearch.complete('find * | st', 11, [])");
        replace.GetProperty("from").GetInt32().Should().Be(9);
    }

    [Fact]
    public void FieldSummaries_CountValuesAndNumbers()
    {
        var fields = Eval<JsonElement>("""
            CabinetSearch.summarizeFields([
                { Id: 'a', RawLine: 'x', Host: 'web-1', Severity: 4, Map: 'c1', ms: 1 },
                { Id: 'b', RawLine: 'y', Host: 'web-1', Severity: 2, Map: 'c2', ms: 5 },
                { Id: 'c', RawLine: 'z', Host: 'web-2', Severity: 2, ms: 3 }])
            """).EnumerateArray().ToList();

        fields.Select(f => f.GetProperty("name").GetString()).Should().NotContain(["Id", "RawLine"]);
        var host = fields.Single(f => f.GetProperty("name").GetString() == "Host");
        host.GetProperty("distinct").GetInt32().Should().Be(2);
        host.GetProperty("top")[0].GetProperty("value").GetString().Should().Be("web-1");
        host.GetProperty("top")[0].GetProperty("share").GetDouble().Should().BeApproximately(2 / 3.0, 0.001);
        fields.Single(f => f.GetProperty("name").GetString() == "Severity").GetProperty("top")[0].GetProperty("value").GetString().Should().Be("Info");
        var ms = fields.Single(f => f.GetProperty("name").GetString() == "ms").GetProperty("numeric");
        ms.GetProperty("mean").GetDouble().Should().Be(3);
        fields.Single(f => f.GetProperty("name").GetString() == "Map").GetProperty("coverage").GetDouble().Should().BeApproximately(2 / 3.0, 0.001);
    }

    [Fact]
    public void TimelineBuckets_FillGaps_AndKeepEventsPastTheEnd()
    {
        // A sender's clock running ahead put matches after "now"; they still count
        var counts = Eval<int[]>("""
            CabinetSearch.timelineBuckets([
                { _time: '1970-01-01T00:05:00Z', count: 3 },
                { _time: '1970-01-01T00:15:00Z', count: 2 },
                { _time: '1970-01-01T00:25:00', count: 4 }], 0, 20 * 60000, 5 * 60000).map(b => b.count)
            """);

        counts.Should().Equal(0, 3, 0, 2, 0, 4);
    }

    [Fact]
    public void TimelineSvg_DrawsOneBarPerNonEmptyBucket_AndEscapesItsLabel()
    {
        var svg = Eval<string>("CabinetSearch.timelineSvg(CabinetSearch.timelineBuckets([{ _time: '1970-01-01T00:05:00Z', count: 3 }], 0, 20 * 60000, 5 * 60000), 600, 96)");

        System.Text.RegularExpressions.Regex.Matches(svg, "class=\"timeline-bar\"").Count.Should().Be(1);
        svg.Should().Contain("role=\"img\"").And.Contain("aria-label=\"3 events over 4 periods of 5 minutes\"");
    }

    [Fact]
    public void ZonelessTimes_AreUtc()
    {
        Eval<long>("CabinetSearch.parseTime('2026-11-09T03:53:07.426')").Should().Be(new DateTimeOffset(2026, 11, 9, 3, 53, 7, 426, TimeSpan.Zero).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Csv_QuotesAndDefusesFormulas()
    {
        Eval<string>("CabinetSearch.toCsv([{ a: '=cmd()', b: 'x,y' }, { a: 'ok', b: 'q\"t' }])")
            .Should().Be("a,b\r\n'=cmd(),\"x,y\"\r\nok,\"q\"\"t\"");
    }
}
