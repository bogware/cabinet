using System.Security.Claims;
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

public class DashboardControllerTests
{
    private readonly Mock<IDashboardStorage> _storage;
    private readonly Mock<IAuditLogger> _auditLogger;
    private readonly DashboardController _controller;

    public DashboardControllerTests()
    {
        _storage = new Mock<IDashboardStorage>();
        _auditLogger = new Mock<IAuditLogger>();
        var logger = new Mock<ILogger<DashboardController>>();

        _controller = new DashboardController(_storage.Object, _auditLogger.Object, logger.Object);
        SetupHttpContext(_controller);
    }

    [Fact]
    public async Task ListDashboards_ReturnsPaginatedSummaries()
    {
        var dashboards = new List<Dashboard>
        {
            new() { Title = "Dashboard 1", Panels = new List<DashboardPanel>() },
            new() { Title = "Dashboard 2", Panels = new List<DashboardPanel>() },
            new() { Title = "Dashboard 3", Panels = new List<DashboardPanel>() }
        };
        _storage.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(dashboards);

        var result = await _controller.ListDashboards(offset: 0, count: 2);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var summaries = okResult.Value.Should().BeAssignableTo<List<DashboardSummary>>().Subject;
        summaries.Should().HaveCount(2);
        summaries[0].Title.Should().Be("Dashboard 1");
        summaries[1].Title.Should().Be("Dashboard 2");
    }

    [Fact]
    public async Task ListDashboards_WithOffset_SkipsItems()
    {
        var dashboards = new List<Dashboard>
        {
            new() { Title = "Dashboard 1", Panels = new List<DashboardPanel>() },
            new() { Title = "Dashboard 2", Panels = new List<DashboardPanel>() },
            new() { Title = "Dashboard 3", Panels = new List<DashboardPanel>() }
        };
        _storage.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(dashboards);

        var result = await _controller.ListDashboards(offset: 2, count: 100);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var summaries = okResult.Value.Should().BeAssignableTo<List<DashboardSummary>>().Subject;
        summaries.Should().HaveCount(1);
        summaries[0].Title.Should().Be("Dashboard 3");
    }

    [Fact]
    public async Task GetDashboard_Existing_ReturnsOk()
    {
        var dashboard = new Dashboard { Title = "My Dashboard" };
        _storage.Setup(s => s.GetByIdAsync(dashboard.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dashboard);

        var result = await _controller.GetDashboard(dashboard.Id);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<Dashboard>().Subject;
        returned.Title.Should().Be("My Dashboard");
    }

    [Fact]
    public async Task GetDashboard_NotFound_Returns404()
    {
        _storage.Setup(s => s.GetByIdAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Dashboard?)null);

        var result = await _controller.GetDashboard("missing");

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task CreateDashboard_ValidRequest_Returns201()
    {
        var request = new CreateDashboardRequest
        {
            Title = "New Dashboard",
            Description = "A test dashboard",
            IsShared = true,
            RefreshIntervalSeconds = 30
        };
        _storage.Setup(s => s.SaveAsync(It.IsAny<Dashboard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Dashboard d, CancellationToken _) => d);

        var result = await _controller.CreateDashboard(request);

        var createdResult = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        createdResult.StatusCode.Should().Be(201);
        var dashboard = createdResult.Value.Should().BeOfType<Dashboard>().Subject;
        dashboard.Title.Should().Be("New Dashboard");
        dashboard.IsShared.Should().BeTrue();
        dashboard.RefreshIntervalSeconds.Should().Be(30);
    }

    [Fact]
    public async Task CreateDashboard_EmptyTitle_ReturnsBadRequest()
    {
        var request = new CreateDashboardRequest { Title = "" };

        var result = await _controller.CreateDashboard(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task CreateDashboard_TitleTooLong_ReturnsBadRequest()
    {
        var request = new CreateDashboardRequest
        {
            Title = new string('x', 201)
        };

        var result = await _controller.CreateDashboard(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task CreateDashboard_InvalidRefreshInterval_ReturnsBadRequest()
    {
        var request = new CreateDashboardRequest
        {
            Title = "Test",
            RefreshIntervalSeconds = -1
        };

        var result = await _controller.CreateDashboard(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task CreateDashboard_TooManyPanels_ReturnsBadRequest()
    {
        var request = new CreateDashboardRequest
        {
            Title = "Test",
            Panels = Enumerable.Range(0, 51).Select(i => new CreatePanelRequest
            {
                Title = $"Panel {i}",
                Query = "find *"
            }).ToList()
        };

        var result = await _controller.CreateDashboard(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task CreateDashboard_AuditLogsCreation()
    {
        var request = new CreateDashboardRequest
        {
            Title = "Audited Dashboard"
        };
        _storage.Setup(s => s.SaveAsync(It.IsAny<Dashboard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Dashboard d, CancellationToken _) => d);

        await _controller.CreateDashboard(request);

        _auditLogger.Verify(a => a.LogAsync(
            It.Is<AuditEvent>(e =>
                e.Action == AuditAction.Create &&
                e.ResourceType == "Dashboard"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateDashboard_NotFound_Returns404()
    {
        _storage.Setup(s => s.GetByIdAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Dashboard?)null);

        var result = await _controller.UpdateDashboard("missing", new UpdateDashboardRequest());

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task UpdateDashboard_ValidRequest_ReturnsUpdated()
    {
        var dashboard = new Dashboard { Title = "Old Title" };
        _storage.Setup(s => s.GetByIdAsync(dashboard.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dashboard);
        _storage.Setup(s => s.SaveAsync(It.IsAny<Dashboard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Dashboard d, CancellationToken _) => d);

        var request = new UpdateDashboardRequest { Title = "New Title" };

        var result = await _controller.UpdateDashboard(dashboard.Id, request);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var updated = okResult.Value.Should().BeOfType<Dashboard>().Subject;
        updated.Title.Should().Be("New Title");
    }

    [Fact]
    public async Task UpdateDashboard_KeepsEachPanelsDescription_AndStackedBars()
    {
        var dashboard = new Dashboard { Title = "Web traffic" };
        _storage.Setup(s => s.GetByIdAsync(dashboard.Id, It.IsAny<CancellationToken>())).ReturnsAsync(dashboard);
        _storage.Setup(s => s.SaveAsync(It.IsAny<Dashboard>(), It.IsAny<CancellationToken>())).ReturnsAsync((Dashboard d, CancellationToken _) => d);

        var result = await _controller.UpdateDashboard(dashboard.Id, new UpdateDashboardRequest
        {
            Panels = [new UpdatePanelRequest { Title = "Status codes", Query = "find * | stats count by status", Type = VisualizationType.StackedBar, Description = "  Share of all responses " }],
        });

        var panel = ((Dashboard)((OkObjectResult)result).Value!).Panels.Single();
        panel.Description.Should().Be("Share of all responses");
        panel.Type.Should().Be(VisualizationType.StackedBar);
    }

    [Fact]
    public async Task UpdateDashboard_RefusesALongPanelDescription()
    {
        var dashboard = new Dashboard { Title = "Web traffic" };
        _storage.Setup(s => s.GetByIdAsync(dashboard.Id, It.IsAny<CancellationToken>())).ReturnsAsync(dashboard);

        var result = await _controller.UpdateDashboard(dashboard.Id, new UpdateDashboardRequest
        {
            Panels = [new UpdatePanelRequest { Title = "x", Query = "find *", Description = new string('a', 501) }],
        });

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task DeleteDashboard_Existing_ReturnsNoContent()
    {
        _storage.Setup(s => s.DeleteAsync("dash-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _controller.DeleteDashboard("dash-1");

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task DeleteDashboard_NotFound_Returns404()
    {
        _storage.Setup(s => s.DeleteAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _controller.DeleteDashboard("missing");

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task DeleteDashboard_AuditLogsDeletion()
    {
        _storage.Setup(s => s.DeleteAsync("dash-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _controller.DeleteDashboard("dash-1");

        _auditLogger.Verify(a => a.LogAsync(
            It.Is<AuditEvent>(e =>
                e.Action == AuditAction.Delete &&
                e.ResourceType == "Dashboard" &&
                e.ResourceId == "dash-1"),
            It.IsAny<CancellationToken>()), Times.Once);
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
