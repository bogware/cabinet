using System.Security.Claims;
using Cabinet.API.Controllers;
using Cabinet.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Cabinet.Tests.Controllers;

public class AlertsControllerTests
{
    private readonly Mock<IAlertHistoryStorage> _storage;
    private readonly AlertsController _controller;

    public AlertsControllerTests()
    {
        _storage = new Mock<IAlertHistoryStorage>();
        _controller = new AlertsController(_storage.Object);
        SetupHttpContext(_controller);
    }

    [Fact]
    public async Task GetAlerts_DefaultPagination_ReturnsOkWithTotalCount()
    {
        var alerts = new List<AlertRecord>
        {
            new() { Id = "a1", SearchName = "Error spike", Severity = "Warning" },
            new() { Id = "a2", SearchName = "High latency", Severity = "Critical" }
        };
        _storage.Setup(s => s.GetAlertsAsync(0, 100, null, null))
            .ReturnsAsync(alerts);
        _storage.Setup(s => s.GetAlertCountAsync(null, null))
            .ReturnsAsync(2);

        var result = await _controller.GetAlerts();

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeAssignableTo<List<AlertRecord>>().Subject;
        returned.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAlerts_WithSeverityFilter_PassesFilterToStorage()
    {
        _storage.Setup(s => s.GetAlertsAsync(0, 100, "Critical", null))
            .ReturnsAsync(new List<AlertRecord>());
        _storage.Setup(s => s.GetAlertCountAsync("Critical", null))
            .ReturnsAsync(0);

        await _controller.GetAlerts(severity: "Critical");

        _storage.Verify(s => s.GetAlertsAsync(0, 100, "Critical", null), Times.Once);
        _storage.Verify(s => s.GetAlertCountAsync("Critical", null), Times.Once);
    }

    [Fact]
    public async Task GetAlerts_WithAcknowledgedFilter_PassesFilterToStorage()
    {
        _storage.Setup(s => s.GetAlertsAsync(0, 100, null, false))
            .ReturnsAsync(new List<AlertRecord>());
        _storage.Setup(s => s.GetAlertCountAsync(null, false))
            .ReturnsAsync(0);

        await _controller.GetAlerts(acknowledged: false);

        _storage.Verify(s => s.GetAlertsAsync(0, 100, null, false), Times.Once);
    }

    [Fact]
    public async Task GetAlerts_PaginationClamped_CountExceedsMax()
    {
        _storage.Setup(s => s.GetAlertsAsync(0, 1000, null, null))
            .ReturnsAsync(new List<AlertRecord>());
        _storage.Setup(s => s.GetAlertCountAsync(null, null))
            .ReturnsAsync(0);

        // count=5000 should be clamped to 1000
        await _controller.GetAlerts(count: 5000);

        _storage.Verify(s => s.GetAlertsAsync(0, 1000, null, null), Times.Once);
    }

    [Fact]
    public async Task GetAlert_Found_ReturnsOk()
    {
        var alert = new AlertRecord
        {
            Id = "alert-1",
            SearchName = "Error detector",
            Severity = "Warning",
            ResultCount = 5
        };
        _storage.Setup(s => s.GetAlertByIdAsync("alert-1"))
            .ReturnsAsync(alert);

        var result = await _controller.GetAlert("alert-1");

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<AlertRecord>().Subject;
        returned.Id.Should().Be("alert-1");
        returned.SearchName.Should().Be("Error detector");
    }

    [Fact]
    public async Task GetAlert_NotFound_Returns404()
    {
        _storage.Setup(s => s.GetAlertByIdAsync("missing"))
            .ReturnsAsync((AlertRecord?)null);

        var result = await _controller.GetAlert("missing");

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task AcknowledgeAlert_Found_ReturnsAcknowledgedAlert()
    {
        var alert = new AlertRecord
        {
            Id = "alert-1",
            Acknowledged = true,
            AcknowledgedBy = "test-hash",
            AcknowledgedAt = DateTime.UtcNow
        };
        _storage.Setup(s => s.AcknowledgeAlertAsync("alert-1", "test-hash"))
            .ReturnsAsync(alert);

        var result = await _controller.AcknowledgeAlert("alert-1");

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<AlertRecord>().Subject;
        returned.Acknowledged.Should().BeTrue();
        returned.AcknowledgedBy.Should().Be("test-hash");
    }

    [Fact]
    public async Task AcknowledgeAlert_NotFound_Returns404()
    {
        _storage.Setup(s => s.AcknowledgeAlertAsync("missing", It.IsAny<string>()))
            .ReturnsAsync((AlertRecord?)null);

        var result = await _controller.AcknowledgeAlert("missing");

        result.Should().BeOfType<NotFoundResult>();
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
