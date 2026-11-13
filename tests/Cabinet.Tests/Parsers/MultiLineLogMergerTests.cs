using Cabinet.Parsers;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Parsers;

public class MultiLineLogMergerTests
{
    [Fact]
    public void ProcessLines_TimestampStartPattern_MergesCorrectly()
    {
        var config = MultiLinePatterns.TimestampPrefixed;
        var merger = new MultiLineLogMerger(config);

        var lines = new[]
        {
            "2024-01-15 10:00:00 First event",
            "  continuation line 1",
            "  continuation line 2",
            "2024-01-15 10:01:00 Second event",
            "  another continuation"
        };

        var results = merger.ProcessLines(lines).ToList();

        results.Should().HaveCount(2);
        results[0].Should().Contain("First event");
        results[0].Should().Contain("continuation line 1");
        results[1].Should().Contain("Second event");
    }

    [Fact]
    public void ProcessLines_JavaStackTrace_MergesCorrectly()
    {
        var config = MultiLinePatterns.JavaStackTrace;
        var merger = new MultiLineLogMerger(config);

        var lines = new[]
        {
            "java.lang.NullPointerException: Something went wrong",
            "\tat com.example.Main.method(Main.java:42)",
            "\tat com.example.Main.main(Main.java:10)",
            "Normal log line after stack trace"
        };

        var results = merger.ProcessLines(lines).ToList();

        results.Should().HaveCount(2);
        results[0].Should().Contain("NullPointerException");
        results[0].Should().Contain("Main.java:42");
    }

    [Fact]
    public void ProcessLines_SingleLines_PassThrough()
    {
        var config = MultiLinePatterns.TimestampPrefixed;
        var merger = new MultiLineLogMerger(config);

        var lines = new[]
        {
            "2024-01-15 10:00:00 Line one",
            "2024-01-15 10:01:00 Line two",
            "2024-01-15 10:02:00 Line three"
        };

        var results = merger.ProcessLines(lines).ToList();
        results.Should().HaveCount(3);
    }

    [Fact]
    public void ProcessLines_EmptyInput_ReturnsEmpty()
    {
        var config = MultiLinePatterns.TimestampPrefixed;
        var merger = new MultiLineLogMerger(config);

        var results = merger.ProcessLines(Array.Empty<string>()).ToList();
        results.Should().BeEmpty();
    }

    [Fact]
    public void ProcessLines_CatastrophicPattern_TimesOutAsNoMatch()
    {
        // A configured pattern that backtracks exponentially on a line that almost matches
        const string pattern = @"^(\w+\s?)*$";
        var line = new string('a', 40) + "!";
        var act = () => new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(200)).IsMatch(line);
        act.Should().Throw<System.Text.RegularExpressions.RegexMatchTimeoutException>("the pattern must really be catastrophic for this test to mean anything");

        var merger = new MultiLineLogMerger(new MultiLineConfig { Mode = MultiLineMode.StartPattern, Pattern = pattern });

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var results = merger.ProcessLines(new[] { "start", line }).ToList();
        watch.Stop();

        results.Should().ContainSingle().Which.Should().Be("start\n" + line);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }
}
