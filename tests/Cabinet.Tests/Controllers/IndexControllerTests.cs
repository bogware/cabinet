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

public class IndexControllerTests
{
    private readonly Mock<ILogStorage> _storage;
    private readonly Mock<IAuditLogger> _auditLogger;
    private readonly IndexController _controller;

    public IndexControllerTests()
    {
        _storage = new Mock<ILogStorage>();
        _auditLogger = new Mock<IAuditLogger>();
        var logger = new Mock<ILogger<IndexController>>();

        _controller = new IndexController(_storage.Object, _auditLogger.Object, logger.Object);
        SetupHttpContext(_controller);
    }

    [Fact]
    public async Task ListIndexes_WithEventsByIndex_ReturnsIndexList()
    {
        var stats = new StorageStats
        {
            TotalEvents = 500,
            EventsByIndex = new Dictionary<string, long>
            {
                ["main"] = 300,
                ["security"] = 200
            }
        };
        _storage.Setup(s => s.GetStatsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(stats);

        var result = await _controller.ListIndexes();

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var indexes = okResult.Value.Should().BeAssignableTo<List<IndexInfo>>().Subject;
        indexes.Should().HaveCount(2);
        indexes.Should().Contain(i => i.Name == "main" && i.EventCount == 300);
        indexes.Should().Contain(i => i.Name == "security" && i.EventCount == 200);
    }

    [Fact]
    public async Task ListIndexes_NoIndexes_ReturnsMainWithTotalCount()
    {
        var stats = new StorageStats
        {
            TotalEvents = 42,
            EventsByIndex = new Dictionary<string, long>()
        };
        _storage.Setup(s => s.GetStatsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(stats);
        _storage.Setup(s => s.GetCountAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(42);

        var result = await _controller.ListIndexes();

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var indexes = okResult.Value.Should().BeAssignableTo<List<IndexInfo>>().Subject;
        indexes.Should().HaveCount(1);
        indexes[0].Name.Should().Be("main");
        indexes[0].EventCount.Should().Be(42);
    }

    [Fact]
    public async Task GetIndex_ValidName_ReturnsIndexDetail()
    {
        _storage.Setup(s => s.GetCountAsync(It.IsAny<QueryContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(150);

        var result = await _controller.GetIndex("security");

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var detail = okResult.Value.Should().BeOfType<IndexDetail>().Subject;
        detail.Name.Should().Be("security");
        detail.EventCount.Should().Be(150);
        detail.IsDefault.Should().BeFalse();
    }

    [Fact]
    public async Task GetIndex_MainIndex_IsDefaultTrue()
    {
        _storage.Setup(s => s.GetCountAsync(It.IsAny<QueryContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(100);

        var result = await _controller.GetIndex("main");

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var detail = okResult.Value.Should().BeOfType<IndexDetail>().Subject;
        detail.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task GetIndex_EmptyName_ReturnsBadRequest()
    {
        var result = await _controller.GetIndex("");

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task GetIndex_TooLongName_ReturnsBadRequest()
    {
        var longName = new string('a', 257);

        var result = await _controller.GetIndex(longName);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task ClearIndex_EmptyIndex_ReturnsBadRequest()
    {
        var result = await _controller.ClearIndex("");

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task ClearIndex_NonExistentIndex_ReturnsOkWithZeroDeleted()
    {
        _storage.Setup(s => s.GetCountAsync(It.IsAny<QueryContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var result = await _controller.ClearIndex("empty-index");

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().NotBeNull();
        // Verify no audit log for zero-count delete
        _auditLogger.Verify(a => a.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ClearIndex_ExistingIndex_DeletesAndAudits()
    {
        _storage.Setup(s => s.GetCountAsync(It.IsAny<QueryContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(500);
        _storage.Setup(s => s.DeleteByIndexAsync("test-index", It.IsAny<CancellationToken>()))
            .ReturnsAsync(500);

        var result = await _controller.ClearIndex("test-index");

        result.Should().BeOfType<OkObjectResult>();
        _storage.Verify(s => s.DeleteByIndexAsync("test-index", It.IsAny<CancellationToken>()), Times.Once);
        _auditLogger.Verify(a => a.LogAsync(
            It.Is<AuditEvent>(e => e.Action == AuditAction.Delete && e.ResourceType == "Index" && e.ResourceId == "test-index"),
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
}
