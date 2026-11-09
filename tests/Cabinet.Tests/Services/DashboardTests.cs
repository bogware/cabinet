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
        var scripts = App + Asset("search-page.js") + Asset("settings-page.js");
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
    public void Charts_ExplainThemselves_ToScreenReaders()
    {
        var svg = Charts().Evaluate("""
            CabinetCharts.timeSvg(CabinetCharts.shape([{ _time: "2026-03-04T01:00:00Z", count: 1 }]), 600, 240, "Errors")
            """).AsString();

        svg.Should().Contain("role=\"img\"").And.Contain("aria-label=\"Errors:").And.Contain("tabindex=\"0\"");
    }
}
