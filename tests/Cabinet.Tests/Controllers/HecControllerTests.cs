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

        _controller = new HecController(_storage.Object, _auditLogger.Object, logger.Object);
        SetupHttpContext(_controller);
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
