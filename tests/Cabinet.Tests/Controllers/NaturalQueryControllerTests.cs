using System.Security.Claims;
using Cabinet.API.Controllers;
using Cabinet.API.Services;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query.Bql;
using Cabinet.Query.Natural;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cabinet.Tests.Controllers;

/// <summary>The search endpoints, reading plain words through the real engine and catalog.</summary>
public sealed class NaturalQueryControllerTests
{
    private readonly InMemoryStorage _storage = new();
    private readonly Mock<ISavedQueryStorage> _saved = new();
    private readonly QueryController _controller;

    public NaturalQueryControllerTests()
    {
        var now = DateTime.UtcNow;
        _storage.StoreBatchAsync(Enumerable.Range(0, 40).Select(i =>
        {
            var e = new LogEvent
            {
                RawLine = $"request {i} handled",
                Host = i % 2 == 0 ? "web-01" : "db-01",
                SourceType = "nginx",
                Severity = i % 4 == 0 ? LogSeverity.Error : LogSeverity.Info,
                Timestamp = now.AddMinutes(-i),
            };
            e.SetField("status", i % 4 == 0 ? 500L : 200L);
            e.SetField("user", i % 3 == 0 ? "alice" : "bob");
            return e;
        })).GetAwaiter().GetResult();

        var engine = new BqlQueryEngine(_storage, new BqlParser(), new BqlOptimizer(), NullLogger<BqlQueryEngine>.Instance);
        _controller = new QueryController(
            engine,
            new BqlParser(),
            _saved.Object,
            Mock.Of<IAuditLogger>(),
            NullLogger<QueryController>.Instance,
            understander: new QueryUnderstander(),
            catalogs: new FieldCatalogService(NullLogger<FieldCatalogService>.Instance),
            storage: _storage);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("ClientId", "test")], "test")) },
        };
    }

    [Fact]
    public async Task AQuestion_RunsAsTheBqlItReadsAs_AndSaysSo()
    {
        var result = await _controller.ExecuteQuery(new QueryRequest { Query = "how many errors per host" }, CancellationToken.None);

        var response = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<QueryResponse>().Subject;
        response.Query.Should().Be("find * | where severity >= error | stats count by host | sort by count desc");
        response.Understanding!.Kind.Should().Be("natural");
        response.Understanding.Input.Should().Be("how many errors per host");
        response.Understanding.Explanation.Should().Contain("for each host");
        response.Count.Should().Be(1, "every error is on web-01");
    }

    [Fact]
    public async Task Exact_RunsTheWordsAsTyped()
    {
        var result = await _controller.ExecuteQuery(new QueryRequest { Query = "how many errors per host", Exact = true }, CancellationToken.None);

        var response = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<QueryResponse>().Subject;
        response.Understanding.Should().BeNull();
        response.Count.Should().Be(0, "no line has all those words");
    }

    [Fact]
    public async Task AStrictQuery_RunsUnchanged_WithNothingToSay()
    {
        var result = await _controller.ExecuteQuery(new QueryRequest { Query = "find * | stats count by host" }, CancellationToken.None);

        var response = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<QueryResponse>().Subject;
        response.Understanding.Should().BeNull();
        response.Count.Should().Be(2);
    }

    [Fact]
    public async Task AQuestionItCantRead_IsA400_WithChoices()
    {
        var result = await _controller.ExecuteQuery(new QueryRequest { Query = "which users logged in from more than one ip" }, CancellationToken.None);

        var problem = result.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should().BeOfType<ProblemDetails>().Subject;
        var understanding = problem.Extensions["understanding"].Should().BeOfType<UnderstandResponse>().Subject;
        understanding.WillRun.Should().BeFalse();
        understanding.Alternatives.Should().NotBeEmpty();
        understanding.SuggestAssistant.Should().BeTrue();
    }

    [Fact]
    public async Task FieldsFromTheData_FixTypos()
    {
        var result = await _controller.ExecuteQuery(new QueryRequest { Query = "find * | stats count by usr" }, CancellationToken.None);

        var response = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<QueryResponse>().Subject;
        response.Query.Should().Be("find * | stats count by user");
        response.Understanding!.Corrections.Should().ContainSingle(c => c.From == "usr" && c.To == "user");
    }

    [Fact]
    public async Task Understand_SaysWhatWouldRun_WithoutRunningIt()
    {
        var result = await _controller.Understand(new UnderstandRequest { Query = "errors on web-01 in the last hour" }, CancellationToken.None);

        var understanding = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<UnderstandResponse>().Subject;
        understanding.Query.Should().Be("find * from last 1h | where severity >= error and host = \"web-01\"");
        understanding.Explanation.Should().Be("Error or worse events where host is web-01, in the last hour");
    }

    [Theory]
    [InlineData("find * | where host=", "\"web-01\"", "value")]
    [InlineData("find * | where user is a", "\"alice\"", "value")]
    [InlineData("host=", "web-01", "value")]
    [InlineData("find * | ", "stats", "command")]
    [InlineData("find * | stats count by st", "status", "field")]
    [InlineData("find * | stats ", "avg(", "function")]
    [InlineData("how m", "how many", "phrase")]
    public async Task Complete_SuggestsFromContext(string text, string expected, string kind)
    {
        var result = await _controller.Complete(text, null, CancellationToken.None);

        var completions = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeAssignableTo<List<Completion>>().Subject;
        completions.Should().Contain(c => c.Text == expected && c.Kind == kind);
        completions.Should().OnlyContain(c => c.Start + c.Length == text.Length);
    }

    [Fact]
    public async Task Examples_FitTheData_AndEachHasItsQuery()
    {
        var result = await _controller.Examples(CancellationToken.None);

        var examples = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeAssignableTo<List<QueryExample>>().Subject;
        examples.Should().Contain(e => e.Text == "what happened on db-01 in the last 15 minutes");
        examples.Should().Contain(e => e.Category == "Security", "the data has users");
        examples.Should().OnlyContain(e => e.Query.StartsWith("find ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SavingAQuestion_KeepsItsBql_AndTheWordsAsTheDescription()
    {
        SavedQuery? stored = null;
        _saved.Setup(s => s.SaveAsync(It.IsAny<SavedQuery>(), It.IsAny<CancellationToken>()))
            .Callback<SavedQuery, CancellationToken>((query, _) => stored = query)
            .ReturnsAsync((SavedQuery query, CancellationToken _) => query);

        await _controller.SaveQuery(new SaveQueryRequest { Name = "Errors by host", Query = "errors by host" });

        stored!.Query.Should().Be("find * | where severity >= error | stats count by host | sort by count desc");
        stored.Description.Should().Be("errors by host");
    }

    [Fact]
    public async Task FieldCatalog_LeavesOutFieldNamesTooLongToBeTyped()
    {
        const int MaxFieldName = 128;
        var storage = new InMemoryStorage();
        var logEvent = new LogEvent { RawLine = "padded", Host = "web-01", Timestamp = DateTime.UtcNow };
        logEvent.SetField(new string('f', MaxFieldName + 1), "x");
        logEvent.SetField(new string('g', MaxFieldName), "x");
        await storage.StoreBatchAsync([logEvent]);

        var catalog = await new FieldCatalogService(NullLogger<FieldCatalogService>.Instance).GetAsync(storage, "test");

        catalog.Fields.Should().Contain(new string('g', MaxFieldName));
        catalog.Fields.Should().NotContain(name => name.Length > MaxFieldName);
    }
}
