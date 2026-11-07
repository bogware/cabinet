using Microsoft.Extensions.DependencyInjection;
using Cabinet.Parsers;
using System.Text.Json;
using System.Security.Claims;
using System.Text;
using Cabinet.API.Controllers;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Cabinet.Tests.Controllers;

public class HecControllerTests
{
    private readonly Mock<ILogStorage> _storage;
    private readonly Mock<IAuditLogger> _auditLogger;
    private readonly HecController _controller;

    public HecControllerTests()
    {
        _storage = new Mock<ILogStorage>();
        _auditLogger = new Mock<IAuditLogger>();
        var logger = new Mock<ILogger<HecController>>();

        _controller = new HecController(_storage.Object, CreateLineParser(), _auditLogger.Object, logger.Object);
        SetupHttpContext(_controller);
    }

    private static LogLineParser CreateLineParser() => new ServiceCollection()
        .AddLogging()
        .AddCabinetParsers()
        .BuildServiceProvider()
        .GetRequiredService<LogLineParser>();

    [Fact]
    public async Task Event_FromAgent_IsParsed_KeepingItsIdHostAndIndex()
    {
        const string id = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
        var line = "2026-03-04T05:06:07Z ERROR status=503 upstream failed";
        var body = JsonSerializer.Serialize(new
        {
            id,
            time = 1000.5,
            host = "agent-host",
            source = "/var/log/app.log",
            sourcetype = "app",
            index = "web",
            @event = line,
        });
        var stored = await PostEventAsync(body);

        var logEvent = stored.Should().ContainSingle().Subject;
        logEvent.Id.Should().Be(id);
        logEvent.RawLine.Should().Be(line);
        logEvent.Host.Should().Be("agent-host");
        logEvent.Index.Should().Be("web");
        logEvent.SourceFile.Should().Be("/var/log/app.log");
        logEvent.SourceType.Should().Be("app");
        logEvent.Severity.Should().Be(LogSeverity.Error);
        logEvent.Timestamp.Should().Be(new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc));
        logEvent.GetField<long>("status").Should().Be(503);
    }

    [Fact]
    public async Task Event_WithNoTimeInTheLine_UsesTheSendersTime()
    {
        var stored = await PostEventAsync("""{"time":1772600767,"event":"plain text"}""");

        stored.Should().ContainSingle().Which.Timestamp
            .Should().Be(new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Raw_LinesAreParsed()
    {
        var stored = new List<LogEvent>();
        _storage
            .Setup(s => s.StoreBatchAsync(It.IsAny<IEnumerable<LogEvent>>(), It.IsAny<CancellationToken>()))
            .Callback((IEnumerable<LogEvent> events, CancellationToken _) => stored.AddRange(events))
            .ReturnsAsync(1);
        _controller.ControllerContext.HttpContext.Request.Body = new MemoryStream(
            Encoding.UTF8.GetBytes("2026-03-04 05:06:07 WARN disk almost full\n"));

        await _controller.ReceiveRaw(host: "h1", index: "ops");

        var logEvent = stored.Should().ContainSingle().Subject;
        logEvent.Severity.Should().Be(LogSeverity.Warning);
        logEvent.Timestamp.Should().Be(new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc));
        logEvent.Host.Should().Be("h1");
        logEvent.Index.Should().Be("ops");
    }

    private async Task<List<LogEvent>> PostEventAsync(string body)
    {
        var stored = new List<LogEvent>();
        _storage
            .Setup(s => s.StoreBatchAsync(It.IsAny<IEnumerable<LogEvent>>(), It.IsAny<CancellationToken>()))
            .Callback((IEnumerable<LogEvent> events, CancellationToken _) => stored.AddRange(events))
            .ReturnsAsync(1);
        _controller.ControllerContext.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

        await _controller.ReceiveEvent(CancellationToken.None);
        return stored;
    }

    [Fact]
    public void Health_ReturnsOkWithHealthyStatus()
    {
        var result = _controller.Health();

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var health = okResult.Value.Should().BeOfType<HecHealthResponse>().Subject;
        health.Text.Should().Be("HEC is healthy");
        health.Code.Should().Be(17);
    }

    [Fact]
    public async Task ReceiveEvent_EmptyBody_ReturnsBadRequest()
    {
        SetRequestBody(_controller, "");

        var result = await _controller.ReceiveEvent(CancellationToken.None);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var response = badRequest.Value.Should().BeOfType<HecResponse>().Subject;
        response.Code.Should().Be(5);
    }

    [Fact]
    public async Task ReceiveEvent_ValidJson_StoresEvents()
    {
        var json = "{\"event\":\"test log line\",\"host\":\"web-01\",\"sourcetype\":\"syslog\"}";
        SetRequestBody(_controller, json);
        _storage.Setup(s => s.StoreBatchAsync(It.IsAny<IEnumerable<LogEvent>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await _controller.ReceiveEvent(CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<HecResponse>().Subject;
        response.Text.Should().Be("Success");
        response.Code.Should().Be(0);

        _storage.Verify(s => s.StoreBatchAsync(
            It.Is<IEnumerable<LogEvent>>(events => events.Any()),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReceiveEvent_InvalidJson_ReturnsBadRequest()
    {
        SetRequestBody(_controller, "not valid json at all");

        var result = await _controller.ReceiveEvent(CancellationToken.None);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var response = badRequest.Value.Should().BeOfType<HecResponse>().Subject;
        response.Text.Should().Be("No valid events");
    }

    [Fact]
    public async Task ReceiveEvent_MultipleEvents_StoresAll()
    {
        var json = "{\"event\":\"line 1\"}\n{\"event\":\"line 2\"}\n{\"event\":\"line 3\"}";
        SetRequestBody(_controller, json);
        _storage.Setup(s => s.StoreBatchAsync(It.IsAny<IEnumerable<LogEvent>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var result = await _controller.ReceiveEvent(CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        _storage.Verify(s => s.StoreBatchAsync(
            It.Is<IEnumerable<LogEvent>>(events => events.Count() == 3),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReceiveRaw_EmptyBody_ReturnsBadRequest()
    {
        SetRequestBody(_controller, "");

        var result = await _controller.ReceiveRaw(cancellationToken: CancellationToken.None);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var response = badRequest.Value.Should().BeOfType<HecResponse>().Subject;
        response.Code.Should().Be(5);
    }

    [Fact]
    public async Task ReceiveRaw_ValidText_StoresEvents()
    {
        SetRequestBody(_controller, "log line 1\nlog line 2\nlog line 3");
        _storage.Setup(s => s.StoreBatchAsync(It.IsAny<IEnumerable<LogEvent>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var result = await _controller.ReceiveRaw(
            host: "web-01", sourcetype: "syslog", cancellationToken: CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<HecResponse>().Subject;
        response.Text.Should().Be("Success");

        _storage.Verify(s => s.StoreBatchAsync(
            It.Is<IEnumerable<LogEvent>>(events =>
                events.Count() == 3 &&
                events.All(e => e.Host == "web-01" && e.SourceType == "syslog")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReceiveRaw_LongQueryParam_ReturnsBadRequest()
    {
        var longHost = new string('a', 257);
        SetRequestBody(_controller, "test log");

        var result = await _controller.ReceiveRaw(host: longHost, cancellationToken: CancellationToken.None);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var response = badRequest.Value.Should().BeOfType<HecResponse>().Subject;
        response.Text.Should().Contain("too long");
    }

    [Fact]
    public async Task ReceiveEvent_AuditLogsIngestion()
    {
        var json = "{\"event\":\"test\"}";
        SetRequestBody(_controller, json);
        _storage.Setup(s => s.StoreBatchAsync(It.IsAny<IEnumerable<LogEvent>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        await _controller.ReceiveEvent(CancellationToken.None);

        _auditLogger.Verify(a => a.LogAsync(
            It.Is<AuditEvent>(e => e.Action == AuditAction.Ingest && e.ResourceType == "LogEvent"),
            It.IsAny<CancellationToken>()), Times.Once);
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

    private static void SetRequestBody(ControllerBase controller, string body)
    {
        controller.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        controller.HttpContext.Request.ContentType = "application/json";
    }
}
