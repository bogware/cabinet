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

public class RolesControllerTests
{
    private readonly Mock<IRbacService> _rbacService;
    private readonly Mock<IAuditLogger> _auditLogger;
    private readonly RolesController _controller;

    public RolesControllerTests()
    {
        _rbacService = new Mock<IRbacService>();
        _auditLogger = new Mock<IAuditLogger>();
        var logger = new Mock<ILogger<RolesController>>();

        _controller = new RolesController(_rbacService.Object, _auditLogger.Object, logger.Object);
        SetupHttpContext(_controller);
    }

    [Fact]
    public async Task ListPrincipals_ReturnsPaginatedList()
    {
        var principals = new List<RbacPrincipal>
        {
            new() { Id = "hash-1", Role = SystemRole.Viewer },
            new() { Id = "hash-2", Role = SystemRole.Admin },
            new() { Id = "hash-3", Role = SystemRole.SuperAdmin }
        };
        _rbacService.Setup(s => s.ListPrincipalsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(principals);

        var result = await _controller.ListPrincipals(offset: 0, count: 2);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var dtos = okResult.Value.Should().BeAssignableTo<List<RbacPrincipalDto>>().Subject;
        dtos.Should().HaveCount(2);
        dtos[0].ClientId.Should().Be("hash-1");
        dtos[1].ClientId.Should().Be("hash-2");
    }

    [Fact]
    public async Task ListPrincipals_WithOffset_SkipsEntries()
    {
        var principals = new List<RbacPrincipal>
        {
            new() { Id = "hash-1", Role = SystemRole.Viewer },
            new() { Id = "hash-2", Role = SystemRole.Admin },
            new() { Id = "hash-3", Role = SystemRole.SuperAdmin }
        };
        _rbacService.Setup(s => s.ListPrincipalsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(principals);

        var result = await _controller.ListPrincipals(offset: 1, count: 100);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var dtos = okResult.Value.Should().BeAssignableTo<List<RbacPrincipalDto>>().Subject;
        dtos.Should().HaveCount(2);
        dtos[0].ClientId.Should().Be("hash-2");
    }

    [Fact]
    public async Task GetPrincipal_Found_ReturnsOk()
    {
        var principal = new RbacPrincipal
        {
            Id = "test-hash",
            Role = SystemRole.Admin,
            AdditionalCapabilities = Capability.Export
        };
        _rbacService.Setup(s => s.GetPrincipalAsync("test-hash", It.IsAny<CancellationToken>()))
            .ReturnsAsync(principal);

        var result = await _controller.GetPrincipal("test-hash");

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = okResult.Value.Should().BeOfType<RbacPrincipalDto>().Subject;
        dto.ClientId.Should().Be("test-hash");
        dto.Role.Should().Be("Admin");
        dto.AdditionalCapabilities.Should().Contain("Export");
    }

    [Fact]
    public async Task GetPrincipal_NotFound_Returns404()
    {
        _rbacService.Setup(s => s.GetPrincipalAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((RbacPrincipal?)null);

        var result = await _controller.GetPrincipal("missing");

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task SavePrincipal_ValidRequest_ReturnsOkAndAudits()
    {
        _rbacService.Setup(s => s.SavePrincipalAsync(It.IsAny<RbacPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var request = new SavePrincipalRequest
        {
            Role = "Admin",
            AdditionalCapabilities = new List<string> { "Export" }
        };

        var result = await _controller.SavePrincipal("new-hash", request);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = okResult.Value.Should().BeOfType<RbacPrincipalDto>().Subject;
        dto.ClientId.Should().Be("new-hash");
        dto.Role.Should().Be("Admin");

        _auditLogger.Verify(a => a.LogAsync(
            It.Is<AuditEvent>(e => e.Action == AuditAction.Update && e.ResourceType == "RbacPrincipal"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SavePrincipal_InvalidRole_ReturnsBadRequest()
    {
        var request = new SavePrincipalRequest { Role = "InvalidRole" };

        var result = await _controller.SavePrincipal("some-hash", request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task SavePrincipal_EmptyApiKeyHash_ReturnsBadRequest()
    {
        var request = new SavePrincipalRequest { Role = "Viewer" };

        var result = await _controller.SavePrincipal("", request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task SavePrincipal_ServiceDoesNotSupportSave_Returns501()
    {
        _rbacService.Setup(s => s.SavePrincipalAsync(It.IsAny<RbacPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var request = new SavePrincipalRequest { Role = "Viewer" };

        var result = await _controller.SavePrincipal("some-hash", request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(501);
    }

    [Fact]
    public async Task DeletePrincipal_Found_ReturnsNoContent()
    {
        _rbacService.Setup(s => s.DeletePrincipalAsync("target-hash", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _controller.DeletePrincipal("target-hash");

        result.Should().BeOfType<NoContentResult>();
        _auditLogger.Verify(a => a.LogAsync(
            It.Is<AuditEvent>(e => e.Action == AuditAction.Delete && e.ResourceId == "target-hash"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeletePrincipal_NotFound_Returns404()
    {
        _rbacService.Setup(s => s.DeletePrincipalAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _controller.DeletePrincipal("missing");

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
