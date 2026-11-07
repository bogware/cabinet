using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Jint;
using Xunit;

namespace Cabinet.Tests.Services;

/// <summary>
/// Log content is attacker-controlled and the dashboard renders it with innerHTML,
/// including inside quoted attributes, so <c>esc</c> is what stands between a log line
/// and script running with an analyst's API key.
/// </summary>
public class DashboardEscapingTests
{
    private static string DashboardPath([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!, "..", "..", "..", "src", "Cabinet.API", "wwwroot", "index.html"));

    private static string Html => File.ReadAllText(DashboardPath());

    private static string Esc(string? value)
    {
        var function = Regex.Match(Html, @"function esc\(s\) \{.*?\n    \}", RegexOptions.Singleline);
        function.Success.Should().BeTrue("the dashboard should define esc()");

        var engine = new Engine();
        engine.Execute(function.Value);
        return engine.Invoke("esc", value!).AsString();
    }

    [Theory]
    [InlineData("<img src=x onerror=alert(1)>", "&lt;img src=x onerror=alert(1)&gt;")]
    [InlineData("a&b", "a&amp;b")]
    [InlineData("x\"class=\"spinner\"onanimationstart=\"alert(1)", "x&quot;class=&quot;spinner&quot;onanimationstart=&quot;alert(1)")]
    [InlineData("x' onclick='alert(1)", "x&#39; onclick=&#39;alert(1)")]
    [InlineData("plain text", "plain text")]
    public void Esc_EncodesEverythingThatCanBreakOutOfHtml(string input, string expected)
    {
        Esc(input).Should().Be(expected);
    }

    [Fact]
    public void Esc_Null_ReturnsEmpty()
    {
        Esc(null).Should().BeEmpty();
    }

    [Fact]
    public void MacroParameters_AreEscaped()
    {
        Html.Should().NotContain("${(m.parameters || []).join(', ') || '-'}");
    }
}
