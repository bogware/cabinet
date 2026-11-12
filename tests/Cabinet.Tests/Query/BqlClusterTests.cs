using Cabinet.Core.Models;
using Cabinet.Query;
using Cabinet.Query.Bql;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Query;

public sealed class BqlClusterTests
{
    [Fact]
    public void Drain_GroupsLinesThatDifferOnlyInValues()
    {
        var drain = new BqlDrain();
        drain.Add("user alice logged in from 10.0.0.1 in 35ms");
        drain.Add("user bob logged in from 10.0.0.2 in 41ms");
        drain.Add("disk /dev/sda1 is 91% full");
        drain.Add("disk /dev/sdb2 is 97% full");
        drain.Add("connection reset by peer");

        var patterns = drain.Patterns.OrderByDescending(p => p.Count).ThenBy(p => p.Id).ToList();

        patterns.Should().HaveCount(3);
        patterns[0].Template.Should().Be("user <*> logged in from <*> in <*>");
        patterns[0].Count.Should().Be(2);
        patterns[1].Template.Should().Be("disk <*> is <*> full");
    }

    [Theory]
    [InlineData("2026-03-04T05:06:07Z")]
    [InlineData("0xdeadbeef")]
    [InlineData("3f1e2d6c-0000-4000-8000-000000000001")]
    [InlineData("status=503")]
    [InlineData("192.168.1.10:8080")]
    public void Values_AreWildcards_FromTheStart(string word)
    {
        BqlDrain.Words($"got {word} back").Should().Equal("got", BqlDrain.Wildcard, "back");
    }

    private static async Task<List<IDictionary<string, object?>>> RunAsync(string query)
    {
        var storage = new InMemoryStorage();
        await storage.StoreBatchAsync(Enumerable.Range(0, 30).Select(i => new LogEvent
        {
            RawLine = i % 3 == 0 ? $"request {i} failed with status {500 + i % 4}" : $"request {i} served in {i * 3}ms",
        }));
        var engine = new BqlQueryEngine(storage, new BqlParser(), new BqlOptimizer(), NullLogger<BqlQueryEngine>.Instance);
        return (await engine.ExecuteAsync(new QueryExecutionContext { Query = query })).Events.Select(e => (IDictionary<string, object?>)e).ToList();
    }

    [Fact]
    public async Task Cluster_GivesOneRowPerPattern_MostCommonFirst()
    {
        var rows = await RunAsync("find * | cluster");

        rows.Select(r => r["cluster_pattern"]).Should().Equal("request <*> served in <*>", "request <*> failed with status <*>");
        rows.Select(r => Convert.ToInt64(r["cluster_count"])).Should().Equal(20, 10);
        ((string)rows[0]["sample"]!).Should().StartWith("request");
    }

    [Fact]
    public async Task Cluster_LabelOnly_LabelsEveryEvent()
    {
        var rows = await RunAsync("find * | cluster labelonly=true | stats count by cluster_pattern");

        rows.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("find * | cluster t=2")]
    [InlineData("find * | cluster colour=red")]
    public async Task BadOptions_AreParseErrors(string query)
    {
        var act = () => RunAsync(query);

        await act.Should().ThrowAsync<QueryParseException>();
    }
}
