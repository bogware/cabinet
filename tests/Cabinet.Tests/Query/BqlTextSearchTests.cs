using Cabinet.Core.Models;
using Cabinet.Query;
using Cabinet.Query.Bql;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Query;

public sealed class BqlTextSearchTests
{
    private static readonly string[] Lines =
    [
        "ERROR connection failed to db-1",
        "WARN connection slow to db-2",
        "INFO user alice logged in",
        "ERROR disk full on web-1",
        "unfailed retry ok",
    ];

    private static async Task<List<string>> FindAsync(string query)
    {
        var storage = new InMemoryStorage();
        var start = new DateTime(2026, 3, 4, 5, 0, 0, DateTimeKind.Utc);
        await storage.StoreBatchAsync(Lines.Select((line, i) => new LogEvent
        {
            RawLine = line,
            Host = line.Contains("web") ? "web-1" : "db",
            Timestamp = start.AddMinutes(i),
        }));
        var engine = new BqlQueryEngine(storage, new BqlParser(), new BqlOptimizer(), NullLogger<BqlQueryEngine>.Instance);
        var result = await engine.ExecuteAsync(new QueryExecutionContext { Query = query });
        return result.Events.Select(e => (string)((IDictionary<string, object?>)e)["RawLine"]!).Order().ToList();
    }

    [Theory]
    [InlineData("find error OR warn", 3)]
    [InlineData("find error NOT disk", 1)]
    [InlineData("find error -disk", 1)]
    [InlineData("find connection AND (error OR warn)", 2)]
    [InlineData("find (alice OR disk) error", 1)]
    [InlineData("find \"connection failed\"", 1)]
    [InlineData("find \"failed connection\"", 0)]
    [InlineData("find fail*", 1)]
    [InlineData("find *fail*", 2)]
    [InlineData("find db-*", 2)]
    [InlineData("find NOT error", 3)]
    [InlineData("find error OR host=web-1", 2)]
    [InlineData("find * | search error OR alice", 3)]
    [InlineData("find * | search NOT (error OR warn)", 2)]
    [InlineData("find connection or", 0)]
    public async Task Search_FollowsSplSyntax(string query, int expected)
    {
        (await FindAsync(query)).Should().HaveCount(expected);
    }

    [Fact]
    public async Task Wildcards_StartAtAWord()
    {
        (await FindAsync("find fail*")).Should().Equal("ERROR connection failed to db-1");
    }

    [Theory]
    [InlineData("a*a*a*a*b", true)]
    [InlineData("*a*a*a*a*b", true)]
    [InlineData("\"aaaab\" a*a*a*a*b", true)]
    [InlineData("a*a*a*a*z", false)]
    public void AWildcard_FindsItsWord_HoweverTheLineIsPadded(string text, bool expected)
    {
        // Backtracking ran out of time at the start of this line, which counted as no match
        var row = new Dictionary<string, object?> { ["RawLine"] = new string('a', 3_000) + "c aaaab" };

        BqlTextSearch.Parse(text).Matches(row).Should().Be(expected);
    }

    [Theory]
    [InlineData("error warn", "error warn", true)]
    [InlineData("error OR warn", null, false)]
    [InlineData("\"connection failed\" db", "connection failed db", false)]
    [InlineData("fail* error", "error", false)]
    [InlineData("*", null, true)]
    public void OnlyRequiredWords_ArePushedDown(string text, string? pushed, bool whole)
    {
        var search = BqlTextSearch.Parse(text);

        search.RequiredWords.Should().Be(pushed);
        search.IsPlainWords.Should().Be(whole);
    }

    [Theory]
    [InlineData("\"unclosed")]
    [InlineData("(error")]
    [InlineData("error OR")]
    public void BadSearches_AreParseErrors(string text)
    {
        var act = () => BqlTextSearch.Parse(text);

        act.Should().Throw<QueryParseException>();
    }

    [Fact]
    public void Optimizer_PushesTheRequiredWords_AndKeepsTheStage()
    {
        var query = new BqlParser().ParseBql("find \"connection failed\" db");

        var optimized = new BqlOptimizer().Optimize(query);

        optimized.StorageContext.SearchText.Should().Be("connection failed db");
        optimized.RemainingStages.Should().ContainSingle().Which.Should().BeOfType<SearchStage>();
    }
}
