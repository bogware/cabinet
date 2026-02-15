using Cabinet.API.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Cabinet.Tests.Controllers;

public class HealthControllerTests
{
    private readonly Mock<HealthCheckService> _healthCheckService;
    private readonly HealthController _controller;

    public HealthControllerTests()
    {
        _healthCheckService = new Mock<HealthCheckService>();
        var logger = new Mock<ILogger<HealthController>>();
        _controller = new HealthController(_healthCheckService.Object, logger.Object);
    }

    [Fact]
    public void GetLiveness_ReturnsOk()
    {
        var result = _controller.GetLiveness();

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.StatusCode.Should().Be(200);
    }

    [Fact]
    public void GetVersion_ReturnsVersionInfo()
    {
        var result = _controller.GetVersion();

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var version = okResult.Value.Should().BeOfType<VersionInfo>().Subject;
        version.ApiVersion.Should().Be("v1");
        version.Version.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetMetrics_ReturnsApiMetrics()
    {
        var result = _controller.GetMetrics();

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var metrics = okResult.Value.Should().BeOfType<ApiMetrics>().Subject;
        metrics.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        metrics.Uptime.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void RecordRequest_IncrementsTotalRequests()
    {
        // Record several requests
        HealthController.RecordRequest(false);
        HealthController.RecordRequest(false);
        HealthController.RecordRequest(true); // error

        // Verify via GetMetrics (metrics include all requests from this test run)
        var result = _controller.GetMetrics() as OkObjectResult;
        var metrics = result!.Value as ApiMetrics;
        metrics!.RequestsTotal.Should().BeGreaterThan(0);
    }
}
