using System.Text;
using System.Text.RegularExpressions;
using Cabinet.API.Services;
using Cabinet.Core.Models;
using Cabinet.Query.Bql;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Services;

public sealed class ReportTests
{
    private static async Task<List<RenderedSection>> RunAsync(Report report)
    {
        var storage = new InMemoryStorage();
        await storage.StoreBatchAsync(Enumerable.Range(0, 40).Select(i => new LogEvent { RawLine = $"<b>request</b> {i} (done)", Host = i % 2 == 0 ? "web-1" : "web-2" }));
        var engine = new BqlQueryEngine(storage, new BqlParser(), new BqlOptimizer(), NullLogger<BqlQueryEngine>.Instance);
        return await ReportRenderer.RunAsync(report, engine, CancellationToken.None);
    }

    private static Report Weekly() => new()
    {
        Name = "Weekly <ops>",
        Recipients = ["ops@example.com"],
        Sections =
        [
            new ReportSection { Title = "By host", Query = "find * | stats count by host" },
            new ReportSection { Title = "Latest", Query = "find * | table RawLine", MaxRows = 5 },
            new ReportSection { Title = "Broken", Query = "find * | nosuchstage" },
        ],
    };

    [Fact]
    public async Task Sections_RunEachSearch_AndAFailureOnlyAffectsItsOwn()
    {
        var sections = await RunAsync(Weekly());

        sections[0].Rows.Should().HaveCount(2);
        sections[1].Rows.Should().HaveCount(5);
        sections[2].Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Html_EncodesWhatCameFromLogs()
    {
        var report = Weekly();
        var html = ReportRenderer.Html(report, await RunAsync(report), DateTime.UtcNow);

        html.Should().NotContain("<b>request").And.Contain("&lt;b&gt;request").And.Contain("Weekly &lt;ops&gt;").And.Contain("The search failed");
    }

    [Fact]
    public async Task Pdf_IsWellFormed_WithEveryObjectWhereTheIndexSays()
    {
        var report = Weekly();
        report.Sections.Add(new ReportSection { Title = "Many", Query = "find *", MaxRows = 500 });
        var pdf = ReportRenderer.Pdf(report, await RunAsync(report), DateTime.UtcNow);
        var text = Encoding.Latin1.GetString(pdf);

        text.Should().StartWith("%PDF-1.4").And.EndWith("%%EOF\n");
        var startXref = long.Parse(Regex.Match(text, @"startxref\n(\d+)").Groups[1].Value);
        text[(int)startXref..].Should().StartWith("xref");
        var offsets = Regex.Matches(text[(int)startXref..], @"(\d{10}) 00000 n").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        offsets.Should().NotBeEmpty();
        for (var i = 0; i < offsets.Count; i++)
        {
            text[offsets[i]..].Should().StartWith($"{i + 1} 0 obj");
        }

        text.Should().Contain("\\(done\\)", "parentheses in text are escaped");
        Regex.Matches(text, "/Type /Page ").Count.Should().BeGreaterThan(1, "a long report runs onto more pages");
    }
}
