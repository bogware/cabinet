using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Cabinet.API.Authentication;
using Cabinet.API.Services;
using Cabinet.API.Services.Investigator;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query.Bql;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cabinet.Tests.Services;

public sealed class InvestigatorTests
{
    /// <summary>A model that runs the searches it's given, then answers with what it saw.</summary>
    private sealed class ScriptedModel(params string[] queries) : IInvestigatorModel
    {
        public List<string> Seen { get; } = [];

        public async Task<string> InvestigateAsync(string systemPrompt, string question, SearchTool search, int maxSearches, CancellationToken cancellationToken)
        {
            foreach (var query in queries.Take(maxSearches))
            {
                Seen.Add(await search(query, cancellationToken));
            }

            return "done";
        }
    }

    private static async Task<(InvestigatorService Service, Mock<IAuditLogger> Audit)> CreateAsync(IInvestigatorModel model, int maxSearches = 8)
    {
        var storage = new InMemoryStorage();
        var visible = new LogEvent { RawLine = "login alice password=hunter2", Index = "team-a" };
        visible.SetField("password", "hunter2");
        await storage.StoreBatchAsync([visible, new LogEvent { RawLine = "secret stuff", Index = "secret" }]);

        var masking = new FieldMaskingService();
        masking.AddRule(new FieldMaskingRule { FieldPattern = "password", ExemptRoles = [SystemRole.Admin, SystemRole.SuperAdmin] });

        var audit = new Mock<IAuditLogger>();
        var service = new InvestigatorService(
            new InvestigatorOptions { Enabled = true, MaxSearches = maxSearches },
            model, storage, masking, new QueryLimits(), audit.Object, NullLoggerFactory.Instance);
        return (service, audit);
    }

    private static IndexAccessScope AdminOfTeamA() => new()
    {
        ReadableIndexes = ["team-a"],
        Principal = new RbacPrincipal { Id = "alice", Role = SystemRole.Admin },
    };

    [Fact]
    public async Task Searches_RunWithTheCallersIndexes_ButMaskedAsForAViewer()
    {
        var model = new ScriptedModel("find *");
        var (service, _) = await CreateAsync(model);

        var result = await service.InvestigateAsync("what happened?", AdminOfTeamA(), null, CancellationToken.None);

        result.Steps.Should().ContainSingle().Which.Rows.Should().Be(1, "the secret index isn't the caller's");
        model.Seen.Single().Should().NotContain("hunter2", "an admin may see it, but the model only sees what a Viewer would");
        model.Seen.Single().Should().NotContain("secret stuff");
    }

    [Fact]
    public async Task EveryQuestionAndSearch_IsAudited_AndSearchesAreLimited()
    {
        var model = new ScriptedModel("find *", "find alice", "find bob");
        var (service, audit) = await CreateAsync(model, maxSearches: 2);

        var result = await service.InvestigateAsync("who logged in?", AdminOfTeamA(), "10.0.0.5", CancellationToken.None);

        result.Steps.Should().HaveCount(2);
        audit.Verify(a => a.LogAsync(It.Is<AuditEvent>(e => e.PrincipalId == "alice" && e.Description!.Contains("asked: who logged in?")), It.IsAny<CancellationToken>()), Times.Once);
        audit.Verify(a => a.LogAsync(It.Is<AuditEvent>(e => e.Description!.StartsWith("AI investigator search:")), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ABadSearch_IsReportedToTheModel_SoItCanFixIt()
    {
        var model = new ScriptedModel("find * | nosuchstage");
        var (service, _) = await CreateAsync(model);

        var result = await service.InvestigateAsync("x", AdminOfTeamA(), null, CancellationToken.None);

        result.Steps.Single().Error.Should().NotBeNullOrEmpty();
        model.Seen.Single().Should().StartWith("The search failed");
    }

    /// <summary>An OpenAI-compatible server that asks for one search, then answers.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public List<JsonObject> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = (JsonObject)JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            Requests.Add(body);
            var message = Requests.Count == 1
                ? """{"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"run_search","arguments":"{\"query\":\"find error\"}"}}]}"""
                : """{"role":"assistant","content":"Errors came from web-1."}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"choices":[{"message":{{message}}}]}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public async Task OpenAiCompatible_CallsTheTool_ThenAnswers()
    {
        var server = new FakeServer();
        var model = new OpenAiCompatibleModel(new HttpClient(server), new InvestigatorOptions { Model = "llama3.1" }, NullLogger<OpenAiCompatibleModel>.Instance);
        var searched = new List<string>();

        var answer = await model.InvestigateAsync("system", "why errors?", (query, _) => { searched.Add(query); return Task.FromResult("2 rows"); }, 5, CancellationToken.None);

        answer.Should().Be("Errors came from web-1.");
        searched.Should().Equal("find error");
        var followUp = server.Requests[1]["messages"]!.AsArray();
        followUp[^1]!["role"]!.GetValue<string>().Should().Be("tool");
        followUp[^1]!["tool_call_id"]!.GetValue<string>().Should().Be("c1");
        followUp[^1]!["content"]!.GetValue<string>().Should().Be("2 rows");
    }

    [Fact]
    public async Task OpenAiCompatible_OutOfSearches_IsAskedToAnswer_WithoutTools()
    {
        var server = new FakeServer();
        var model = new OpenAiCompatibleModel(new HttpClient(server), new InvestigatorOptions(), NullLogger<OpenAiCompatibleModel>.Instance);

        await model.InvestigateAsync("system", "q", (_, _) => Task.FromResult("rows"), 1, CancellationToken.None);

        server.Requests[0].ContainsKey("tools").Should().BeTrue();
        server.Requests[1].ContainsKey("tools").Should().BeFalse();
    }

    [Fact]
    public void RowsSentToTheModel_AreClipped()
    {
        var rows = Enumerable.Range(0, 200).Select(i => (dynamic)new Dictionary<string, object?> { ["RawLine"] = new string('x', 2000) }).ToList();

        var text = InvestigatorService.FormatRows(new Cabinet.Query.QueryResult { Events = rows }, 50);

        text.Length.Should().BeLessThan(30_000);
        text.Should().StartWith("200 rows");
    }
}
