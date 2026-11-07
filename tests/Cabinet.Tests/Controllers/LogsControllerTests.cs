using System.Security.Claims;
using Cabinet.API.Controllers;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Parsers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Cabinet.Tests.Controllers;

public class LogsControllerTests
{
    private readonly Mock<ILogStorage> _storage;
    private readonly Mock<ILogParserProvider> _parserProvider;
    private readonly Mock<IAuditLogger> _auditLogger;
    private readonly LogsController _controller;

    public LogsControllerTests()
    {
        _storage = new Mock<ILogStorage>();
        _parserProvider = new Mock<ILogParserProvider>();
        _auditLogger = new Mock<IAuditLogger>();
        var multiLineMerger = new MultiLineLogMerger(MultiLinePatterns.TimestampPrefixed);
        var logger = new Mock<ILogger<LogsController>>();

        _controller = new LogsController(
            _storage.Object,
            new LogLineParser(new ParserRegistry(), _parserProvider.Object),
            _auditLogger.Object,
            multiLineMerger,
            logger.Object);

        SetupHttpContext(_controller);
    }

    [Fact]
    public async Task IngestLogs_EmptyRequest_ReturnsBadRequest()
    {
        var request = new IngestRequest { Logs = new List<string>() };

        var result = await _controller.IngestLogs(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task IngestLogs_NullLogs_ReturnsBadRequest()
    {
        var request = new IngestRequest { Logs = null! };

        var result = await _controller.IngestLogs(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task IngestLogs_TooManyLogs_ReturnsBadRequest()
    {
        var request = new IngestRequest
        {
            Logs = Enumerable.Range(0, 10_001).Select(i => $"log {i}").ToList()
        };

        var result = await _controller.IngestLogs(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task IngestLogs_ValidLogs_StoresAndReturnsCount()
    {
        var request = new IngestRequest
        {
            Logs = new List<string> { "2024-01-01 00:00:00 log line 1", "2024-01-01 00:00:01 log line 2" }
        };

        // No parser found — falls through to generic
        _parserProvider.Setup(p => p.GetParser(It.IsAny<string>())).Returns((ILogParser?)null);
        _storage.Setup(s => s.StoreBatchAsync(It.IsAny<IEnumerable<LogEvent>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        var result = await _controller.IngestLogs(request);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<IngestResponse>().Subject;
        response.TotalReceived.Should().Be(2);
        response.TotalIngested.Should().Be(2);
        response.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task IngestLogs_AuditLogsIngestion()
    {
        var request = new IngestRequest
        {
            Logs = new List<string> { "test log" }
        };
        _parserProvider.Setup(p => p.GetParser(It.IsAny<string>())).Returns((ILogParser?)null);
        _storage.Setup(s => s.StoreBatchAsync(It.IsAny<IEnumerable<LogEvent>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        await _controller.IngestLogs(request);

        _auditLogger.Verify(a => a.LogAsync(
            It.Is<AuditEvent>(e => e.Action == AuditAction.Ingest),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetLog_Existing_ReturnsOk()
    {
        var evt = new LogEvent { Id = "test-id", RawLine = "test line" };
        _storage.Setup(s => s.GetByIdAsync("test-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(evt);

        var result = await _controller.GetLog("test-id");

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().Be(evt);
    }

    [Fact]
    public async Task GetLog_NonExistent_Returns404()
    {
        _storage.Setup(s => s.GetByIdAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((LogEvent?)null);

        var result = await _controller.GetLog("missing");

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task DeleteLogs_NoFilters_ReturnsBadRequest()
    {
        var result = await _controller.DeleteLogs();

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task GetStatistics_ReturnsStats()
    {
        _storage.Setup(s => s.GetStatsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageStats { TotalEvents = 100 });

        var result = await _controller.GetStatistics();

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().NotBeNull();
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
