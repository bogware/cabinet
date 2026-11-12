using System.Security.Claims;
using Cabinet.API.Authentication;
using Cabinet.API.Controllers;
using Cabinet.API.Middleware;
using Cabinet.API.Services;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query;
using Cabinet.Query.Bql;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cabinet.Tests.Services;

/// <summary>Regression tests for the findings of the pre-release security review.</summary>
public sealed class SecurityReviewTests
{
    private static RbacPrincipal Limited(Capability capabilities) => new()
    {
        Id = "team",
        Role = SystemRole.Admin,
        IndexPermissions = [new IndexPermission { IndexPattern = "team-*", Capabilities = capabilities }],
    };

    private static IndexAccessScope Scope(RbacPrincipal principal) => new()
    {
        Principal = principal,
        ReadableIndexes = principal.IndexesWith(Capability.Search),
    };

    private static async Task<IActionResult?> RunEveryIndexFilterAsync(RbacPrincipal? principal, Capability capability)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new IndexAccessScope { Principal = principal });
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider().CreateScope().ServiceProvider };
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var context = new ActionExecutingContext(action, [], new Dictionary<string, object?>(), new object());

        await new RequireEveryIndexAttribute(capability).OnActionExecutionAsync(context, () =>
            Task.FromResult(new ActionExecutedContext(action, [], new object())));
        return context.Result;
    }

    [Fact]
    public async Task EveryIndexFilter_RefusesCallersLimitedToSomeIndexes()
    {
        var result = await RunEveryIndexFilterAsync(Limited(Capability.Search | Capability.IndexManagement), Capability.Search);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task EveryIndexFilter_AllowsCallersWithEveryIndex()
    {
        var result = await RunEveryIndexFilterAsync(new RbacPrincipal { Id = "admin", Role = SystemRole.Admin }, Capability.Search);

        result.Should().BeNull();
    }

    [Fact]
    public async Task EveryIndexFilter_RefusesWithoutAPrincipal()
    {
        (await RunEveryIndexFilterAsync(null, Capability.Search)).Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
    }

    [Theory]
    [InlineData(typeof(DetectionRulesController), "Create")]
    [InlineData(typeof(DetectionRulesController), "Update")]
    [InlineData(typeof(DetectionRulesController), "Enable")]
    [InlineData(typeof(DetectionRulesController), "Disable")]
    [InlineData(typeof(DetectionRulesController), "Delete")]
    [InlineData(typeof(BackupController), "Create")]
    public void ActionsOverEveryIndex_RequireEveryIndex(Type controller, string action)
    {
        controller.GetMethod(action)!.GetCustomAttributes(typeof(RequireEveryIndexAttribute), true).Should().NotBeEmpty();
    }

    [Fact]
    public void WritesThatActAcrossIndexes_RequireEveryIndex()
    {
        // Data models, knowledge objects and field extractions run in everyone's searches
        foreach (var controller in new[] { typeof(DataModelController), typeof(KnowledgeController), typeof(FieldExtractionsController) })
        {
            var writes = controller.GetMethods()
                .Where(method => method.GetCustomAttributes(true).Any(attribute => attribute is HttpPostAttribute or HttpPutAttribute or HttpDeleteAttribute))
                .ToList();

            writes.Should().NotBeEmpty();
            writes.Should().OnlyContain(method => method.GetCustomAttributes(typeof(RequireEveryIndexAttribute), true).Length > 0, controller.Name);
        }
    }

    [Fact]
    public async Task Alerts_AreLimitedToIndexesTheCallerReads()
    {
        var history = new InMemoryAlertHistoryStorage();
        await history.SaveAlertAsync(new AlertRecord { Id = "mine", Index = "team-a", SearchName = "r", Severity = "Warning" });
        await history.SaveAlertAsync(new AlertRecord { Id = "theirs", Index = "secret", SearchName = "r", Severity = "Warning" });
        await history.SaveAlertAsync(new AlertRecord { Id = "global", SearchName = "r", Severity = "Warning" });
        var controller = new AlertsController(history, Scope(Limited(Capability.Search | Capability.Alerts)))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var listed = (await controller.GetAlerts()).Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeAssignableTo<List<AlertRecord>>().Subject;

        listed.Select(alert => alert.Id).Should().Equal("mine");
        controller.Response.Headers["X-Total-Count"].ToString().Should().Be("1");
        (await controller.GetAlert("theirs")).Should().BeOfType<NotFoundResult>();
        (await controller.AcknowledgeAlert("theirs")).Should().BeOfType<NotFoundResult>();
        (await history.GetAlertByIdAsync("theirs"))!.Acknowledged.Should().BeFalse();
    }

    [Fact]
    public async Task AlertHistory_KeepsNoLogContent()
    {
        var history = new InMemoryAlertHistoryStorage();
        var rules = new DetectionRuleStore(new InMemoryDocumentStore(), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        var executor = new DetectionActionExecutor(history, Mock.Of<Cabinet.Alerts.IAlertService>(), new Cabinet.Storage.InMemoryStorage(), new HttpClient(), NullLogger<DetectionActionExecutor>.Instance);
        var engine = new DetectionEngine(rules, executor, NullLogger<DetectionEngine>.Instance);
        await rules.SaveAsync(new DetectionRule { Name = "Password", Pattern = "password", Severity = "Warning" });
        engine.InvalidateRules();

        await engine.CheckAsync([new LogEvent { RawLine = "password=hunter2 for alice", Index = "team-a", Timestamp = DateTime.UtcNow }]);

        var alert = (await history.GetAlertsAsync()).Should().ContainSingle().Subject;
        alert.Index.Should().Be("team-a");
        alert.Message.Should().NotContain("hunter2");
    }

    [Fact]
    public async Task Retention_NeedsIndexManagementOnThatIndex()
    {
        var documents = new InMemoryDocumentStore();
        var controller = new RetentionController(documents, null, Scope(Limited(Capability.Search | Capability.IndexManagement)))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        (await controller.SetPolicy("secret", new RetentionPolicyDto { MaxAgeDays = 1 })).Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        (await controller.DeletePolicy("secret")).Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        (await controller.SetPolicy("team-a", new RetentionPolicyDto { MaxAgeDays = 1 })).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public void Otlp_DeeplyNestedValues_AreRefused()
    {
        // A string, wrapped in arrays far deeper than any real attribute
        var value = Message(1, "x"u8.ToArray());
        for (var i = 0; i < 1000; i++)
        {
            value = Message(5, Message(1, value));
        }

        var act = () => OtlpLogDecoder.ReadAnyValue(value);

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Otlp_ShallowNesting_StillDecodes()
    {
        var value = Message(5, Message(1, Message(1, "x"u8.ToArray())));

        OtlpLogDecoder.ReadAnyValue(value).Should().Be("x");
    }

    [Fact]
    public void Bql_DeeplyNestedFunctionCalls_AreRefused()
    {
        var expression = string.Concat(Enumerable.Repeat("abs(", 1000)) + "1" + new string(')', 1000);

        var act = () => new BqlParser().ParseBql($"find * | eval x = {expression}");

        act.Should().Throw<QueryParseException>().WithMessage("*deeply nested*");
    }

    [Fact]
    public void Bql_NestedFunctionCalls_WithinTheLimit_Parse()
    {
        var act = () => new BqlParser().ParseBql("find * | eval x = abs(round(abs(1.5), 0))");

        act.Should().NotThrow();
    }

    [Fact]
    public void Oidc_TokenCannotSupplyCabinetsOwnClaims()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim("sub", "someone"),
            new Claim(PrincipalResolver.RoleClaim, nameof(SystemRole.Admin)),
            new Claim("Tier", "Enterprise"),
            new Claim("ClientId", "admin"),
            new Claim(PrincipalResolver.IndexPermissionClaim, "*:Search"),
        ], "test");

        new ClaimRoleMapper(NullLogger<ClaimRoleMapper>.Instance).MapClaims(identity, new SsoConfiguration { Enabled = true, AutoProvision = false });

        identity.FindFirst(PrincipalResolver.RoleClaim).Should().BeNull();
        identity.FindFirst("Tier").Should().BeNull();
        identity.FindFirst(PrincipalResolver.IndexPermissionClaim).Should().BeNull();
        identity.FindAll("ClientId").Select(claim => claim.Value).Should().Equal("oidc-someone");
    }

    [Fact]
    public async Task IngestTier_CanSendButNotRead()
    {
        var keys = new Mock<IApiKeyService>();
        keys.Setup(k => k.GetActiveKeysAsync()).ReturnsAsync([new ApiKeyInfo { Id = "agent", Tier = "Ingest", ClientName = "Agent" }]);

        var principal = await new ConfigurationRbacService(keys.Object).GetPrincipalAsync("agent");

        principal!.HasCapability(Capability.Ingest, "main").Should().BeTrue();
        principal.HasCapability(Capability.Search, "main").Should().BeFalse();
        principal.HasCapabilityOnAnyIndex(Capability.Search).Should().BeFalse();
        principal.HasCapability(Capability.IndexManagement).Should().BeFalse();
    }

    [Fact]
    public async Task Metrics_OnlyAnswersTheExactPath()
    {
        var reachedNext = false;
        var middleware = new MetricsMiddleware(_ =>
        {
            reachedNext = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = "/METRICS";

        await middleware.InvokeAsync(context);

        reachedNext.Should().BeTrue("a proxy blocking /metrics wouldn't block /METRICS");
    }

    [Fact]
    public async Task Search_UnderMasking_MatchesOnlyWhatTheCallerSees()
    {
        var inner = new InMemoryStorage();
        await inner.StoreBatchAsync(
        [
            new LogEvent { RawLine = "login password=hunter2 user=alice", Index = "main", Timestamp = DateTime.UtcNow.AddSeconds(-1), Fields = { ["password"] = "hunter2" } },
            new LogEvent { RawLine = "login user=bob", Index = "main", Timestamp = DateTime.UtcNow },
        ]);
        var masking = new FieldMaskingService();
        masking.AddRule(new FieldMaskingRule { FieldPattern = "password", Strategy = MaskingStrategy.Redact });
        var storage = new IndexRestrictedLogStorage(inner, new IndexAccessScope { Principal = new RbacPrincipal { Id = "v", Role = SystemRole.Viewer } }, masking);

        async Task<List<LogEvent>> SearchAsync(string text)
        {
            var found = new List<LogEvent>();
            await foreach (var logEvent in storage.QueryAsync(new QueryContext { SearchText = text }))
            {
                found.Add(logEvent);
            }

            return found;
        }

        (await SearchAsync("hunter2")).Should().BeEmpty("the caller can't see that value, so mustn't learn it's there");
        (await storage.GetCountAsync(new QueryContext { SearchText = "hunter2" })).Should().Be(0);
        (await SearchAsync("login")).Should().HaveCount(2);
        (await SearchAsync("alice")).Should().ContainSingle().Which.RawLine.Should().NotContain("hunter2");
    }

    private static byte[] Message(int field, byte[] payload)
    {
        var bytes = new List<byte> { (byte)((field << 3) | 2) };
        var length = (uint)payload.Length;
        while (length >= 0x80)
        {
            bytes.Add((byte)(length | 0x80));
            length >>= 7;
        }

        bytes.Add((byte)length);
        bytes.AddRange(payload);
        return bytes.ToArray();
    }
}
