using System.Security.Claims;
using Cabinet.API.Controllers;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Cabinet.Tests.Controllers;

public class SearchJobControllerTests
{
    private readonly Mock<ISearchJobStorage> _jobStorage;
    private readonly Mock<IQueryParser> _queryParser;
    private readonly Mock<IAuditLogger> _auditLogger;
    private readonly SearchJobController _controller;

    public SearchJobControllerTests()
    {
        _jobStorage = new Mock<ISearchJobStorage>();
        _queryParser = new Mock<IQueryParser>();
        _auditLogger = new Mock<IAuditLogger>();
        var logger = new Mock<ILogger<SearchJobController>>();

        _controller = new SearchJobController(
            _jobStorage.Object,
            _queryParser.Object,
            _auditLogger.Object,
            logger.Object);

        SetupHttpContext(_controller);
    }

    [Fact]
    public async Task CreateJob_ValidQuery_Returns202Accepted()
    {
        var request = new CreateSearchJobRequest { Query = "find errors" };
        _queryParser.Setup(p => p.Parse("find errors")).Returns(new QueryPlan());
        _jobStorage.Setup(s => s.CreateAsync(It.IsAny<SearchJob>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SearchJob j, CancellationToken _) => j);

        var result = await _controller.CreateJob(request, CancellationToken.None);

        var accepted = result.Should().BeOfType<AcceptedAtActionResult>().Subject;
        accepted.StatusCode.Should().Be(202);
        var response = accepted.Value.Should().BeOfType<SearchJobResponse>().Subject;
        response.Query.Should().Be("find errors");
        response.Status.Should().Be("Queued");
    }

    [Fact]
    public async Task CreateJob_EmptyQuery_ReturnsBadRequest()
    {
        var request = new CreateSearchJobRequest { Query = "" };

        var result = await _controller.CreateJob(request, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task CreateJob_QueryTooLong_ReturnsBadRequest()
    {
        var request = new CreateSearchJobRequest { Query = new string('x', 10_001) };

        var result = await _controller.CreateJob(request, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task CreateJob_InvalidBqlSyntax_ReturnsBadRequest()
    {
        var request = new CreateSearchJobRequest { Query = "invalid | | query" };
        _queryParser.Setup(p => p.Parse("invalid | | query"))
            .Throws(new QueryParseException("Unexpected token"));

        var result = await _controller.CreateJob(request, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task CreateJob_AuditsCreation()
    {
        var request = new CreateSearchJobRequest { Query = "find *" };
        _queryParser.Setup(p => p.Parse("find *")).Returns(new QueryPlan());
        _jobStorage.Setup(s => s.CreateAsync(It.IsAny<SearchJob>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SearchJob j, CancellationToken _) => j);

        await _controller.CreateJob(request, CancellationToken.None);

        _auditLogger.Verify(a => a.LogAsync(
            It.Is<AuditEvent>(e => e.Action == AuditAction.Create && e.ResourceType == "SearchJob"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetJob_Found_ReturnsOk()
    {
        var job = new SearchJob
        {
            Query = "find errors",
            Status = SearchJobStatus.Running,
            Progress = 50
        };
        _jobStorage.Setup(s => s.GetByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        var result = await _controller.GetJob(job.Id, CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<SearchJobResponse>().Subject;
        response.Query.Should().Be("find errors");
        response.Status.Should().Be("Running");
        response.Progress.Should().Be(50);
    }

    [Fact]
    public async Task GetJob_NotFound_Returns404()
    {
        _jobStorage.Setup(s => s.GetByIdAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SearchJob?)null);

        var result = await _controller.GetJob("missing", CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task ListJobs_ReturnsJobsForOwner()
    {
        var jobs = new List<SearchJob>
        {
            new() { Query = "find a" },
            new() { Query = "find b" }
        };
        _jobStorage.Setup(s => s.GetAllAsync("test-hash", It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobs);

        var result = await _controller.ListJobs();

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task CancelJob_RunningJob_CancelsAndAudits()
    {
        var job = new SearchJob
        {
            Query = "find errors",
            Status = SearchJobStatus.Running
        };
        _jobStorage.Setup(s => s.GetByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        var result = await _controller.CancelJob(job.Id, CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<SearchJobResponse>().Subject;
        response.Status.Should().Be("Cancelled");

        _jobStorage.Verify(s => s.UpdateAsync(
            It.Is<SearchJob>(j => j.Status == SearchJobStatus.Cancelled),
            It.IsAny<CancellationToken>()), Times.Once);

        _auditLogger.Verify(a => a.LogAsync(
            It.Is<AuditEvent>(e => e.Action == AuditAction.Delete && e.ResourceType == "SearchJob"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelJob_CompletedJob_Returns409()
    {
        var job = new SearchJob
        {
            Query = "find errors",
            Status = SearchJobStatus.Completed
        };
        _jobStorage.Setup(s => s.GetByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        var result = await _controller.CancelJob(job.Id, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(409);
    }

    [Fact]
    public async Task CancelJob_NotFound_Returns404()
    {
        _jobStorage.Setup(s => s.GetByIdAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SearchJob?)null);

        var result = await _controller.CancelJob("missing", CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task DeleteJob_Found_ReturnsNoContent()
    {
        _jobStorage.Setup(s => s.DeleteAsync("job-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _controller.DeleteJob("job-123", CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task DeleteJob_NotFound_Returns404()
    {
        _jobStorage.Setup(s => s.DeleteAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _controller.DeleteJob("missing", CancellationToken.None);

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
            new Claim(ClaimTypes.NameIdentifier, "test-client")
        }, "ApiKey"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }
}
