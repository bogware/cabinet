using Cabinet.Core.Models;
using Cabinet.Query;
using Cabinet.Query.Bql;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Query;

/// <summary>
/// Found on a live server: <c>max(_time)</c> compared times as text, so 9:59 PM beat 10:16 PM, and
/// a search for a host name with a hyphen found nothing.
/// </summary>
public sealed class TimeAndHyphenTests
{
    private static readonly DateTime Late = new(2026, 11, 9, 22, 16, 27, DateTimeKind.Utc);
    private static readonly DateTime Early = new(2026, 11, 9, 21, 59, 59, DateTimeKind.Utc);
    private static readonly DateTime Morning = new(2026, 11, 9, 9, 5, 0, DateTimeKind.Utc);

    private static async Task<List<IDictionary<string, object?>>> RunAsync(string query)
    {
        var storage = new InMemoryStorage();
        await storage.StoreBatchAsync(new[]
        {
            new LogEvent { RawLine = "late", Host = "l4d2-dev", Timestamp = Late },
            new LogEvent { RawLine = "early", Host = "l4d2-dev", Timestamp = Early },
            new LogEvent { RawLine = "morning", Host = "l4d2-prod", Timestamp = Morning },
        });
        var engine = new BqlQueryEngine(storage, new BqlParser(), new BqlOptimizer(), NullLogger<BqlQueryEngine>.Instance);
        var result = await engine.ExecuteAsync(new QueryExecutionContext { Query = query });
        return result.Events.Select(e => (IDictionary<string, object?>)e).ToList();
    }

    [Fact]
    public async Task MaxAndMinOfTime_CompareTimes_NotText()
    {
        var row = (await RunAsync("find * | stats max(_time) as latest, min(_time) as earliest")).Single();

        row["latest"].Should().Be(Late);
        row["earliest"].Should().Be(Morning);
    }

    [Fact]
    public async Task MaxOfTime_ByHost_IsEachHostsNewest()
    {
        var rows = await RunAsync("find * | stats max(_time) as latest by host");

        rows.Single(r => Equals(r["host"], "l4d2-dev"))["latest"].Should().Be(Late);
        rows.Single(r => Equals(r["host"], "l4d2-prod"))["latest"].Should().Be(Morning);
    }

    [Theory]
    [InlineData("find host=l4d2-dev")]
    [InlineData("find host=\"l4d2-dev\"")]
    [InlineData("find * | where host = \"l4d2-dev\"")]
    [InlineData("find * | search host=l4d2-dev")]
    public async Task HostWithAHyphen_IsFound(string query)
    {
        (await RunAsync(query)).Should().HaveCount(2);
    }
}

public sealed class ReplaceBackreferenceTests
{
    [Theory]
    [InlineData(@"^/home/([^/]+)/.*", @"\1", "campaign")]
    [InlineData(@"^/home/([^/]+)/.*", "$1", "campaign")]
    [InlineData(@"^/home/([^/]+)/(\w+).*", @"\2-\1", "Steam-campaign")]
    [InlineData("home", @"\\", @"/\/campaign/Steam/x.log")]
    [InlineData(@"x\.log", "costs $5", "/home/campaign/Steam/costs $5")]
    public void Replace_SupportsSplunkStyleBackreferences(string pattern, string replacement, string expected)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["path"] = "/home/campaign/Steam/x.log" };
        var expression = new FunctionExpression("replace",
        [
            new FieldExpression("path"),
            new LiteralExpression(pattern),
            new LiteralExpression(replacement),
        ]);

        BqlExpressionEvaluator.Evaluate(expression, row).Should().Be(expected);
    }
}
