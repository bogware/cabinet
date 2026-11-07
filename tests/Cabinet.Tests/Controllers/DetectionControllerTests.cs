using System.Security.Claims;
using Cabinet.API.Controllers;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Extras.Gaming.Detection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Cabinet.Tests.Controllers;

public class DetectionControllerTests
{
    private readonly Mock<IScumscanDetector> _scumscanDetector;
    private readonly Mock<IHackwatchDetector> _hackwatchDetector;
    private readonly Mock<IAdminabuseDetector> _adminabuseDetector;
    private readonly Mock<IAuditLogger> _auditLogger;
    private readonly DetectionController _controller;

    public DetectionControllerTests()
    {
        _scumscanDetector = new Mock<IScumscanDetector>();
        _hackwatchDetector = new Mock<IHackwatchDetector>();
        _adminabuseDetector = new Mock<IAdminabuseDetector>();
        _auditLogger = new Mock<IAuditLogger>();
        var logger = new Mock<ILogger<DetectionController>>();

        _controller = new DetectionController(
            _scumscanDetector.Object,
            _hackwatchDetector.Object,
            _adminabuseDetector.Object,
            _auditLogger.Object,
            logger.Object);

        SetupHttpContext(_controller);
    }

    [Fact]
    public async Task RunScumscan_EmptyEvents_ReturnsZeroDetections()
    {
        var request = new DetectionRequest { Events = new List<LogEvent>() };

        var result = await _controller.RunScumscan(request);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<DetectionResponse>().Subject;
        response.DetectionType.Should().Be("Scumscan");
        response.TotalAnalyzed.Should().Be(0);
        response.TotalDetections.Should().Be(0);
        response.Results.Should().BeEmpty();
    }

    [Fact]
    public async Task RunScumscan_WithDetections_ReturnsResults()
    {
        var evt = new LogEvent { RawLine = "player said bad word" };
        var detection = new DetectionResult
        {
            DetectionType = "Scumscan",
            PlayerId = "STEAM_0:1:123",
            PlayerName = "BadPlayer",
            Severity = DetectionSeverity.Medium,
            Description = "Toxic chat detected"
        };

        _scumscanDetector.Setup(d => d.AnalyzeAsync(evt))
            .ReturnsAsync(detection);

        var request = new DetectionRequest { Events = new List<LogEvent> { evt } };

        var result = await _controller.RunScumscan(request);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<DetectionResponse>().Subject;
        response.TotalAnalyzed.Should().Be(1);
        response.TotalDetections.Should().Be(1);
        response.Results.Should().HaveCount(1);
        response.Results[0].PlayerId.Should().Be("STEAM_0:1:123");
    }

    [Fact]
    public async Task RunScumscan_NullEvents_TreatsAsEmpty()
    {
        var request = new DetectionRequest { Events = null };

        var result = await _controller.RunScumscan(request);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<DetectionResponse>().Subject;
        response.TotalAnalyzed.Should().Be(0);
    }

    [Fact]
    public async Task RunHackwatch_EmptyEvents_ReturnsZeroDetections()
    {
        var request = new DetectionRequest { Events = new List<LogEvent>() };

        var result = await _controller.RunHackwatch(request);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<DetectionResponse>().Subject;
        response.DetectionType.Should().Be("Hackwatch");
        response.TotalAnalyzed.Should().Be(0);
        response.TotalDetections.Should().Be(0);
    }

    [Fact]
    public async Task RunHackwatch_WithDetections_ReturnsResults()
    {
        var evt = new LogEvent { RawLine = "player teleported suspiciously" };
        var detection = new DetectionResult
        {
            DetectionType = "Hackwatch",
            PlayerId = "STEAM_0:1:456",
            PlayerName = "Cheater",
            Severity = DetectionSeverity.High,
            Description = "Suspicious movement detected"
        };

        _hackwatchDetector.Setup(d => d.AnalyzeAsync(evt))
            .ReturnsAsync(detection);

        var request = new DetectionRequest { Events = new List<LogEvent> { evt } };

        var result = await _controller.RunHackwatch(request);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<DetectionResponse>().Subject;
        response.TotalDetections.Should().Be(1);
        response.Results[0].PlayerName.Should().Be("Cheater");
    }

    [Fact]
    public async Task RunAdminabuse_EmptyEvents_ReturnsZeroDetections()
    {
        var request = new DetectionRequest { Events = new List<LogEvent>() };

        var result = await _controller.RunAdminabuse(request);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<DetectionResponse>().Subject;
        response.DetectionType.Should().Be("Adminabuse");
        response.TotalAnalyzed.Should().Be(0);
    }

    [Fact]
    public async Task RunAdminabuse_WithDetections_ReturnsResults()
    {
        var evt = new LogEvent { RawLine = "admin kicked player without reason" };
        var detection = new DetectionResult
        {
            DetectionType = "Adminabuse",
            PlayerId = "STEAM_0:1:789",
            PlayerName = "BadAdmin",
            Severity = DetectionSeverity.Critical,
            Description = "Unauthorized admin action"
        };

        _adminabuseDetector.Setup(d => d.AnalyzeAsync(evt))
            .ReturnsAsync(detection);

        var request = new DetectionRequest { Events = new List<LogEvent> { evt } };

        var result = await _controller.RunAdminabuse(request);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<DetectionResponse>().Subject;
        response.TotalDetections.Should().Be(1);
        response.Results[0].Severity.Should().Be(DetectionSeverity.Critical);
    }

    [Fact]
    public async Task RunScumscan_AnalyzerReturnsNull_FiltersOutNullResults()
    {
        var evt1 = new LogEvent { RawLine = "normal chat" };
        var evt2 = new LogEvent { RawLine = "bad word" };
        var detection = new DetectionResult
        {
            DetectionType = "Scumscan",
            Severity = DetectionSeverity.Low
        };

        _scumscanDetector.Setup(d => d.AnalyzeAsync(evt1))
            .ReturnsAsync((DetectionResult?)null);
        _scumscanDetector.Setup(d => d.AnalyzeAsync(evt2))
            .ReturnsAsync(detection);

        var request = new DetectionRequest { Events = new List<LogEvent> { evt1, evt2 } };

        var result = await _controller.RunScumscan(request);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<DetectionResponse>().Subject;
        response.TotalAnalyzed.Should().Be(2);
        response.TotalDetections.Should().Be(1);
    }

    [Fact]
    public async Task GetStatistics_ReturnsEmptyStats()
    {
        var result = await _controller.GetStatistics();

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var stats = okResult.Value.Should().BeOfType<DetectionStatistics>().Subject;
        stats.TotalEventsAnalyzed.Should().Be(0);
        stats.DetectionCounts.Should().BeEmpty();
        stats.TopOffenders.Should().BeEmpty();
    }

    [Fact]
    public async Task GetStatistics_WithTimeRange_UsesProvidedRange()
    {
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2024, 1, 31, 0, 0, 0, DateTimeKind.Utc);

        var result = await _controller.GetStatistics(start, end);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var stats = okResult.Value.Should().BeOfType<DetectionStatistics>().Subject;
        stats.Period.Start.Should().Be(start);
        stats.Period.End.Should().Be(end);
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
