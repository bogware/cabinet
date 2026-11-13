using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Jint;
using Xunit;

namespace Cabinet.Tests.Services;

/// <summary>
/// The dashboard's page and charts, run without a browser. Chart code is written so its logic can
/// be tested this way.
/// </summary>
public sealed class DashboardTests
{
    private static string Asset(string name, [CallerFilePath] string testFile = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!, "..", "..", "..", "src", "Cabinet.API", "wwwroot", name)));

    private static string WwwrootFile(string name, [CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", "src", "Cabinet.API", "wwwroot", name));

    private static string Html => Asset("index.html");

    private static string App => Asset("app.js");

    private static Engine Charts()
    {
        var engine = new Engine();
        engine.Execute(Asset("charts.js"));
        return engine;
    }

    private static string Evaluate(string expression) =>
        Charts().Evaluate($"JSON.stringify({expression})").AsString();

    [Fact]
    public void Page_HasNoInlineScript_SoThePolicyCanForbidIt()
    {
        Regex.IsMatch(Html, @"\son[a-z]+\s*=\s*""", RegexOptions.IgnoreCase).Should().BeFalse("inline handlers would need 'unsafe-inline'");
        Regex.IsMatch(Html, @"<script(?![^>]*\ssrc=)", RegexOptions.IgnoreCase).Should().BeFalse();
        App.Should().NotContain(" onclick=\"").And.NotContain("eval(").And.NotContain("new Function");
    }

    [Fact]
    public void EveryHandlerInThePage_ExistsInTheScript()
    {
        // Named in the page, and in the HTML the scripts build
        var scripts = string.Concat(Directory.GetFiles(Path.GetDirectoryName(WwwrootFile("app.js"))!, "*.js").Order().Select(File.ReadAllText));
        var names = Regex.Matches(Html + scripts, @"data-(?:click|change)=""(\w+)""").Select(match => match.Groups[1].Value).Distinct().ToList();

        names.Should().NotBeEmpty();
        names.Should().OnlyContain(name => Regex.IsMatch(scripts, $@"\bfunction {name}\("), "each handler has to be defined");
    }

    [Fact]
    public void ThereIsNoDefaultApiKey()
    {
        App.Should().NotContain("dev-enterprise-key").And.NotContain("prompt(");
        Asset("search-page.js").Should().NotContain("prompt(");
        Html.Should().Contain("type=\"password\" id=\"loginKey\"");
    }

    [Fact]
    public void FormControls_AndNavigation_CanBeReachedByEveryone()
    {
        Html.Should().Contain("<nav").And.Contain("aria-label=\"Main\"").And.Contain("Skip to content");
        Regex.IsMatch(Html, @"<div class=""nav-item").Should().BeFalse("navigation has to work from a keyboard");
        Regex.Matches(Html, @"<select(?![^>]*\b(?:aria-label|id)=)[^>]*>").Should().BeEmpty("a select needs a name");
        App.Should().Contain("aria-modal").And.Contain("aria-selected").And.Contain("Escape");
    }

    [Fact]
    public void TimeSeries_BecomesALineChart_InTimeOrder()
    {
        var shaped = Evaluate("""
            CabinetCharts.shape([
                { _time: "2026-03-04T02:00:00Z", count: 5 },
                { _time: "2026-03-04T01:00:00Z", count: 3 }
            ])
            """);

        shaped.Should().Contain("\"kind\":\"line\"").And.Contain("\"values\":[3,5]");
    }

    [Fact]
    public void TimeSeriesByField_GetsOneSeriesPerValue_WithZeroForGaps()
    {
        var engine = Charts();
        var series = engine.Evaluate("""
            JSON.stringify(CabinetCharts.shape([
                { _time: "2026-03-04T01:00:00Z", host: "a", count: 1 },
                { _time: "2026-03-04T02:00:00Z", host: "a", count: 2 },
                { _time: "2026-03-04T02:00:00Z", host: "b", count: 7 }
            ]).series)
            """).AsString();

        series.Should().Be("""[{"name":"a","values":[1,2]},{"name":"b","values":[0,7]}]""");
    }

    [Fact]
    public void ManySeries_FoldIntoOther_RatherThanInventingColors()
    {
        var names = Charts().Evaluate("""
            const rows = [];
            for (let i = 0; i < 12; i++) rows.push({ _time: "2026-03-04T01:00:00Z", host: "h" + i, count: 100 - i });
            JSON.stringify(CabinetCharts.shape(rows).series.map(s => s.name));
            """).AsString();

        names.Should().Contain("Other").And.Contain("h0").And.NotContain("h11");
        names.Split(',').Should().HaveCount(8);
    }

    [Fact]
    public void Aggregate_BecomesBars_LargestFirst_AndLongListsSayWhatsHidden()
    {
        var bars = Charts().Evaluate("""
            const rows = [];
            for (let i = 0; i < 25; i++) rows.push({ host: "h" + i, count: i });
            const shaped = CabinetCharts.shape(rows);
            JSON.stringify({ kind: shaped.kind, first: shaped.bars[0].label, shown: shaped.bars.length, hidden: shaped.hidden });
            """).AsString();

        bars.Should().Be("""{"kind":"bar","first":"h24","shown":20,"hidden":5}""");
    }

    [Fact]
    public void NumericGroup_IsALabel_NotAValue()
    {
        var shaped = Evaluate("""CabinetCharts.shape([{ status: 200, count: 9 }, { status: 500, count: 1 }])""");

        shaped.Should().Contain("\"kind\":\"bar\"").And.Contain("\"labelKey\":\"status\"").And.Contain("\"valueKey\":\"count\"");
    }

    [Fact]
    public void SingleNumber_BecomesAStatTile()
    {
        Evaluate("""CabinetCharts.shape([{ count: 42 }])""").Should().Contain("\"kind\":\"stat\"").And.Contain("42");
    }

    [Fact]
    public void RawEvents_StayATable()
    {
        Evaluate("""CabinetCharts.shape([{ RawLine: "x", Severity: 2, Timestamp: "2026-03-04T01:00:00Z" }])""").Should().Contain("\"kind\":\"table\"");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(99)]
    [InlineData(1234567)]
    public void AxisTicks_ReachTheMaximum_FromZero(int max)
    {
        var ticks = Charts().Evaluate($"JSON.stringify(CabinetCharts.niceTicks({max}))").AsString();
        var values = ticks.Trim('[', ']').Split(',').Select(double.Parse).ToList();

        values[0].Should().Be(0);
        values[^1].Should().BeGreaterThanOrEqualTo(max);
        values.Count.Should().BeLessThan(9);
    }

    [Fact]
    public void WhatsDrawn_EscapesLogContent()
    {
        var engine = Charts();
        var payload = "<img src=x onerror=alert(1)>";
        engine.SetValue("payload", payload);

        var svg = engine.Evaluate("""
            CabinetCharts.timeSvg(
                Object.assign(CabinetCharts.shape([{ _time: "2026-03-04T01:00:00Z", host: payload, count: 1 }]), {}),
                600, 240, payload)
            """).AsString();
        var legend = engine.Evaluate("CabinetCharts.legendHtml([{ name: payload }, { name: 'b' }])").AsString();
        var bars = engine.Evaluate("""CabinetCharts.barsHtml(CabinetCharts.shape([{ host: payload, count: 1 }, { host: "b", count: 2 }]))""").AsString();
        var table = engine.Evaluate("""CabinetCharts.tableHtml([payload], [{ [payload]: payload }], 5)""").AsString();

        foreach (var drawn in new[] { svg, legend, bars, table })
        {
            drawn.Should().NotContain("<img");
        }
    }

    [Fact]
    public void Pie_FoldsSmallSlicesIntoOther_AndDropsNonPositive()
    {
        var shaped = Evaluate("""
            CabinetCharts.shape(Array.from({ length: 12 }, (_, i) => ({ host: 'h' + i, count: 12 - i })).concat([{ host: 'neg', count: -5 }]), 'pie').slices.map(s => s.label)
            """);

        shaped.Should().Be("""["h0","h1","h2","h3","h4","h5","h6","Other"]""");
    }

    [Fact]
    public void Gauge_UsesAMaxColumn_OrARoundNumberAboveTheValue()
    {
        Evaluate("CabinetCharts.shape([{ used: 70, max: 200 }], 'gauge')").Should().Contain("\"max\":200");
        Evaluate("CabinetCharts.shape([{ used: 73 }], 'gauge')").Should().Contain("\"kind\":\"gauge\"").And.Contain("\"max\":80");
    }

    [Fact]
    public void Map_NeedsCoordinates()
    {
        Evaluate("CabinetCharts.shape([{ lat: 51.5, lon: -0.1, City: 'London', count: 3 }], 'map')")
            .Should().Contain("\"kind\":\"map\"").And.Contain("\"label\":\"London\"");
        Evaluate("CabinetCharts.shape([{ host: 'a', count: 3 }], 'map')").Should().Contain("\"kind\":\"table\"");
    }

    [Fact]
    public void Heatmap_IsGroupsOverTime()
    {
        Evaluate("""CabinetCharts.shape([{ _time: "2026-03-04T01:00:00Z", host: "a", count: 1 }, { _time: "2026-03-04T02:00:00Z", host: "b", count: 2 }], 'heatmap')""")
            .Should().Contain("\"kind\":\"heatmap\"");
    }

    [Fact]
    public void NewCharts_EscapeLogContent()
    {
        var engine = Charts();
        engine.SetValue("payload", "<img src=x onerror=alert(1)>");

        var drawn = new[]
        {
            "CabinetCharts.pieSvg(CabinetCharts.shape([{ host: payload, count: 1 }, { host: 'b', count: 2 }], 'pie'), 200)",
            "CabinetCharts.gaugeSvg(Object.assign(CabinetCharts.shape([{ x: 1 }], 'gauge'), { label: payload }), 300)",
            "CabinetCharts.heatmapSvg(CabinetCharts.shape([{ _time: '2026-03-04T01:00:00Z', host: payload, count: 1 }], 'heatmap'), 600)",
            "CabinetCharts.mapSvg(CabinetCharts.shape([{ lat: 1, lon: 2, City: payload, count: 1 }], 'map'), 600)",
        }.Select(script => engine.Evaluate(script).AsString());

        drawn.Should().AllSatisfy(svg => svg.Should().NotContain("<img"));
    }

    [Fact]
    public void StackedBar_GivesEachGroupItsShare_AndFoldsTheRest()
    {
        var shares = Evaluate("""
            CabinetCharts.shape([{ status: '200', count: 82 }, { status: '304', count: 9 }, { status: '404', count: 6 }, { status: '500', count: 3 }], 'stacked').parts.map(p => p.label + ':' + Math.round(p.share * 100))
            """);
        shares.Should().Be("""["200:82","304:9","404:6","500:3"]""");

        Evaluate("CabinetCharts.shape(Array.from({ length: 12 }, (_, i) => ({ host: 'h' + i, count: 12 - i })), 'stacked').parts.length").Should().Be("8");
    }

    [Fact]
    public void StackedBar_EscapesLogContent_AndNamesEveryShare()
    {
        var engine = Charts();
        engine.SetValue("payload", "<img src=x onerror=alert(1)>");

        var html = engine.Evaluate("CabinetCharts.stackedHtml(CabinetCharts.shape([{ host: payload, count: 3 }, { host: 'b', count: 1 }], 'stacked'))").AsString();

        html.Should().NotContain("<img").And.Contain("75%").And.Contain("25%").And.Contain("role=\"img\"");
    }

    [Theory]
    [InlineData("find * | timechart span=1h count", "Number of events per hour")]
    [InlineData("find status>=500 | stats count", "Number of events, matching status>=500")]
    [InlineData("find * | stats perc95(duration_ms) as p95 by path | sort p95 desc", "95th percentile of duration_ms by path")]
    [InlineData("find * | top host limit=5", "Most common host, top 5")]
    [InlineData("error | timechart span=5m count by host", "Number of events per 5 minutes, by host, matching error")]
    [InlineData("find * | where action = \"deny\" | stats dc(src_ip)", "Distinct src_ip, where action = \"deny\"")]
    public void APanelWithoutADescription_SaysWhatItsQueryMeasures(string query, string expected)
    {
        var engine = Charts();
        engine.SetValue("query", query);

        engine.Evaluate("CabinetCharts.describeQuery(query)").AsString().Should().Be(expected);
    }

    [Theory]
    [InlineData(1284, 1146, true, "Up 12% on the day before", "bad")]
    [InlineData(316, 330, true, "Down 4% on the day before", "good")]
    [InlineData(500, 400, null, "Up 25% on the day before", "neutral")]
    [InlineData(5, 5, true, "Same as the day before", "neutral")]
    [InlineData(3, 0, true, "Up from none in the day before", "bad")]
    public void ABigNumber_ComparesWithThePreviousPeriod(double current, double previous, bool? lowerIsBetter, string text, string tone)
    {
        var engine = Charts();
        engine.SetValue("current", current);
        engine.SetValue("previous", previous);
        engine.SetValue("lower", lowerIsBetter.HasValue ? lowerIsBetter.Value : Jint.Native.JsValue.Null);

        engine.Evaluate("CabinetCharts.compare(current, previous, lower, 'the day before').text").AsString().Should().Be(text);
        engine.Evaluate("CabinetCharts.compare(current, previous, lower, 'the day before').tone").AsString().Should().Be(tone);
    }

    [Theory]
    [InlineData("Server errors", true)]
    [InlineData("find status>=500 | stats count", true)]
    [InlineData("Blocked connections", true)]
    [InlineData("Requests per hour", false)]
    public void ErrorsAndDenials_AreBetterLower(string text, bool expected)
    {
        var engine = Charts();
        engine.SetValue("text", text);

        engine.Evaluate("CabinetCharts.lowerIsBetter(text)").AsBoolean().Should().Be(expected);
    }

    [Fact]
    public void ShowEvents_DropsTheAggregation()
    {
        Charts().Evaluate("CabinetCharts.eventsQuery('find status>=500 | where host = \"web-02\" | stats count by path')").AsString()
            .Should().Be("find status>=500 | where host = \"web-02\"");
    }

    [Fact]
    public void OneMeasureOverTime_IsALineOverASoftWash()
    {
        var svg = Charts().Evaluate("""
            CabinetCharts.timeSvg(CabinetCharts.shape([{ _time: "2026-03-04T01:00:00Z", count: 1 }, { _time: "2026-03-04T02:00:00Z", count: 3 }], 'area'), 600, 200, "Requests")
            """).AsString();

        svg.Should().Contain("linearGradient").And.Contain("stroke=\"var(--series-1)\"");
    }

    private static Engine Tail()
    {
        var engine = new Engine();
        engine.Execute("var window = {};");
        engine.Execute(Asset("v3.js"));
        return engine;
    }

    [Theory]
    [InlineData("disk full on web-1", "disk", "<mark>disk</mark> full on web-1", true)]
    [InlineData("Disk FULL", "full disk", "<mark>Disk</mark> <mark>FULL</mark>", true)]
    [InlineData("nothing here", "disk", "nothing here", false)]
    [InlineData("a.b(c) a.b", "a.b(c)", "<mark>a.b(c)</mark> a.b", true)]
    public void LiveTail_HighlightsTheWords_AsPlainText(string line, string words, string html, bool matched)
    {
        var engine = Tail();
        engine.SetValue("line", line);
        engine.SetValue("words", words);

        engine.Execute("var result = highlightHtml(line, highlightTerms(words));");

        engine.Evaluate("result.html").AsString().Should().Be(html);
        engine.Evaluate("result.matched").AsBoolean().Should().Be(matched);
    }

    [Fact]
    public void LiveTail_EscapesTheLine_AroundAndInsideMatches()
    {
        var engine = Tail();
        engine.SetValue("line", "<img src=x onerror=alert(1)> img");

        var html = engine.Evaluate("highlightHtml(line, highlightTerms('img')).html").AsString();

        html.Should().NotContain("<img").And.Contain("&lt;<mark>img</mark>");
    }

    [Fact]
    public void Charts_ExplainThemselves_ToScreenReaders()
    {
        var svg = Charts().Evaluate("""
            CabinetCharts.timeSvg(CabinetCharts.shape([{ _time: "2026-03-04T01:00:00Z", count: 1 }]), 600, 240, "Errors")
            """).AsString();

        svg.Should().Contain("role=\"img\"").And.Contain("aria-label=\"Errors:").And.Contain("tabindex=\"0\"");
    }
}
