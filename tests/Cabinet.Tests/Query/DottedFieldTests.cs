using Cabinet.Core.Models;
using Cabinet.Query;
using Cabinet.Query.Bql;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Query;

/// <summary>
/// Field names with dots, which OpenTelemetry attributes and flattened JSON use. They used to
/// match nothing in <c>where</c> and fail to parse after <c>by</c>.
/// </summary>
public sealed class DottedFieldTests
{
    private static async Task<List<IDictionary<string, object?>>> RunAsync(string query)
    {
        var storage = new InMemoryStorage();
        var now = DateTime.UtcNow;
        var events = new[]
        {
            new LogEvent { RawLine = "a", Timestamp = now, Fields = { ["metric.name"] = "orders", ["service.name"] = "shop", ["value"] = 3 } },
            new LogEvent { RawLine = "b", Timestamp = now.AddSeconds(-1), Fields = { ["metric.name"] = "orders", ["service.name"] = "shop", ["value"] = 4 } },
            new LogEvent { RawLine = "c", Timestamp = now.AddSeconds(-2), Fields = { ["metric.name"] = "latency", ["service.name"] = "api", ["value"] = 9 } },
        };
        await storage.StoreBatchAsync(events);
        var engine = new BqlQueryEngine(storage, new BqlParser(), new BqlOptimizer(), NullLogger<BqlQueryEngine>.Instance);
        var result = await engine.ExecuteAsync(new QueryExecutionContext { Query = query });
        return result.Events.Select(e => (IDictionary<string, object?>)e).ToList();
    }

    [Fact]
    public async Task Where_OnADottedField_Matches()
    {
        (await RunAsync("find * | where metric.name = \"orders\"")).Should().HaveCount(2);
    }

    [Fact]
    public async Task StatsBy_ADottedField_Groups()
    {
        var rows = await RunAsync("find * | stats sum(value) as total by service.name");

        rows.Should().HaveCount(2);
        rows.Single(r => Equals(r["service.name"], "shop"))["total"].Should().Be(7d);
    }

    [Fact]
    public async Task Top_ADottedField_Counts()
    {
        var rows = await RunAsync("find * | top 1 metric.name");

        rows.Should().ContainSingle().Which["metric.name"].Should().Be("orders");
    }

    [Fact]
    public async Task SpacedDot_StillJoinsText()
    {
        var rows = await RunAsync("find * | head 1 | eval x = \"a\" . \"b\" | table x");

        rows.Single()["x"].Should().Be("ab");
    }
}
