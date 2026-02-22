using System.Security.Claims;
using Cabinet.API.Controllers;
using Cabinet.API.Services;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Cabinet.Tests.Controllers;

public class ScheduledSearchControllerTests
{
    private readonly ScheduledSearchService _scheduledSearchService;
    private readonly Mock<IQueryParser> _queryParser;
    private readonly Mock<IAuditLogger> _auditLogger;
    private readonly ScheduledSearchController _controller;

    public ScheduledSearchControllerTests()
    {
        var scopeFactory = new Mock<IServiceScopeFactory>();
        var scheduledSearchStorage = new Mock<IScheduledSearchStorage>();
        scheduledSearchStorage.Setup(s => s.SaveAsync(It.IsAny<ScheduledSearch>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ScheduledSearch ss, CancellationToken _) => ss);
        scheduledSearchStorage.Setup(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var serviceLogger = new Mock<ILogger<ScheduledSearchService>>();
        _scheduledSearchService = new ScheduledSearchService(
            scopeFactory.Object,
            scheduledSearchStorage.Object,
            serviceLogger.Object);

        _queryParser = new Mock<IQueryParser>();
        _auditLogger = new Mock<IAuditLogger>();
        var controllerLogger = new Mock<ILogger<ScheduledSearchController>>();

        _controller = new ScheduledSearchController(
            _scheduledSearchService,
            _queryParser.Object,
            _auditLogger.Object,
            controllerLogger.Object);

        SetupHttpContext(_controller);
    }

    [Fact]
    public void ListScheduledSearches_Empty_ReturnsEmptyList()
    {
        var result = _controller.ListScheduledSearches();

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var summaries = okResult.Value.Should().BeAssignableTo<List<ScheduledSearchSummaryDto>>().Subject;
        summaries.Should().BeEmpty();
    }

    [Fact]
    public async Task ListScheduledSearches_WithSearches_ReturnsAll()
    {
        await _scheduledSearchService.RegisterAsync(new ScheduledSearch
        {
            Name = "Error Monitor",
            Query = "find errors",
            CronExpression = "*/5 * * * *"
        });

        var result = _controller.ListScheduledSearches();

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var summaries = okResult.Value.Should().BeAssignableTo<List<ScheduledSearchSummaryDto>>().Subject;
        summaries.Should().HaveCount(1);
        summaries[0].Name.Should().Be("Error Monitor");
    }

    [Fact]
    public async Task GetScheduledSearch_Existing_ReturnsOk()
    {
        var search = new ScheduledSearch
        {
            Name = "Test Search",
            Query = "find *",
            CronExpression = "0 * * * *"
        };
        await _scheduledSearchService.RegisterAsync(search);

        var result = _controller.GetScheduledSearch(search.Id);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<ScheduledSearch>().Subject;
        returned.Name.Should().Be("Test Search");
    }

    [Fact]
    public void GetScheduledSearch_NotFound_Returns404()
    {
        var result = _controller.GetScheduledSearch("nonexistent");

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task CreateScheduledSearch_ValidRequest_Returns201()
    {
        _queryParser.Setup(p => p.Parse("find errors")).Returns(new QueryPlan());

        var request = new CreateScheduledSearchRequest
        {
            Name = "Error Detector",
            Query = "find errors",
            CronExpression = "*/5 * * * *",
            TimeRange = "-15m"
        };

        var result = await _controller.CreateScheduledSearch(request);

        var createdResult = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        createdResult.StatusCode.Should().Be(201);
        var search = createdResult.Value.Should().BeOfType<ScheduledSearch>().Subject;
        search.Name.Should().Be("Error Detector");
        search.Query.Should().Be("find errors");
        search.CronExpression.Should().Be("*/5 * * * *");
    }

    [Fact]
    public async Task CreateScheduledSearch_EmptyName_ReturnsBadRequest()
    {
        var request = new CreateScheduledSearchRequest
        {
            Name = "",
            Query = "find *",
            CronExpression = "* * * * *"
        };

        var result = await _controller.CreateScheduledSearch(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task CreateScheduledSearch_EmptyQuery_ReturnsBadRequest()
    {
        var request = new CreateScheduledSearchRequest
        {
            Name = "Test",
            Query = "",
            CronExpression = "* * * * *"
        };

        var result = await _controller.CreateScheduledSearch(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task CreateScheduledSearch_InvalidQuery_ReturnsBadRequest()
    {
        _queryParser.Setup(p => p.Parse("bad ||| query"))
            .Throws(new QueryParseException("Unexpected token"));

        var request = new CreateScheduledSearchRequest
        {
            Name = "Test",
            Query = "bad ||| query",
            CronExpression = "* * * * *"
        };

        var result = await _controller.CreateScheduledSearch(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task CreateScheduledSearch_InvalidCron_TooFewFields_ReturnsBadRequest()
    {
        _queryParser.Setup(p => p.Parse("find *")).Returns(new QueryPlan());

        var request = new CreateScheduledSearchRequest
        {
            Name = "Test",
            Query = "find *",
            CronExpression = "* *"
        };

        var result = await _controller.CreateScheduledSearch(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task CreateScheduledSearch_InvalidCron_BadCharacters_ReturnsBadRequest()
    {
        _queryParser.Setup(p => p.Parse("find *")).Returns(new QueryPlan());

        var request = new CreateScheduledSearchRequest
        {
            Name = "Test",
            Query = "find *",
            CronExpression = "* * * * abc"
        };

        var result = await _controller.CreateScheduledSearch(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task CreateScheduledSearch_AuditLogsCreation()
    {
        _queryParser.Setup(p => p.Parse("find *")).Returns(new QueryPlan());

        var request = new CreateScheduledSearchRequest
        {
            Name = "Audited Search",
            Query = "find *",
            CronExpression = "0 * * * *"
        };

        await _controller.CreateScheduledSearch(request);

        _auditLogger.Verify(a => a.LogAsync(
            It.Is<AuditEvent>(e =>
                e.Action == AuditAction.Create &&
                e.ResourceType == "ScheduledSearch"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnableScheduledSearch_Existing_ReturnsOk()
    {
        var search = new ScheduledSearch
        {
            Name = "Disabled Search",
            Query = "find *",
            CronExpression = "0 * * * *",
            Enabled = false
        };
        await _scheduledSearchService.RegisterAsync(search);

        var result = await _controller.EnableScheduledSearch(search.Id);

        result.Should().BeOfType<OkObjectResult>();
        var updated = _scheduledSearchService.Get(search.Id);
        updated!.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task EnableScheduledSearch_NotFound_Returns404()
    {
        var result = await _controller.EnableScheduledSearch("nonexistent");

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task DisableScheduledSearch_Existing_ReturnsOk()
    {
        var search = new ScheduledSearch
        {
            Name = "Active Search",
            Query = "find *",
            CronExpression = "0 * * * *",
            Enabled = true
        };
        await _scheduledSearchService.RegisterAsync(search);

        var result = await _controller.DisableScheduledSearch(search.Id);

        result.Should().BeOfType<OkObjectResult>();
        var updated = _scheduledSearchService.Get(search.Id);
        updated!.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task DisableScheduledSearch_NotFound_Returns404()
    {
        var result = await _controller.DisableScheduledSearch("nonexistent");

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task DeleteScheduledSearch_Existing_ReturnsNoContent()
    {
        var search = new ScheduledSearch
        {
            Name = "To Delete",
            Query = "find *",
            CronExpression = "0 * * * *"
        };
        await _scheduledSearchService.RegisterAsync(search);

        var result = await _controller.DeleteScheduledSearch(search.Id);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task DeleteScheduledSearch_NotFound_Returns404()
    {
        var result = await _controller.DeleteScheduledSearch("nonexistent");

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(404);
    }

    private static void SetupHttpContext(ControllerBase controller)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("127.0.0.1");
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("ClientId", "test-hash"),
            new Claim(ClaimTypes.NameIdentifier, "test-client"),
            new Claim(ClaimTypes.Name, "test-user")
        }, "ApiKey"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }
}
