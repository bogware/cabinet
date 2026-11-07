using System.Security.Claims;
using Cabinet.API.Controllers;
using Cabinet.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Cabinet.Tests.Controllers;

public class FieldExtractionsControllerTests
{
    private readonly Mock<IFieldExtractionStorage> _storage;
    private readonly FieldExtractionsController _controller;

    public FieldExtractionsControllerTests()
    {
        _storage = new Mock<IFieldExtractionStorage>();
        _controller = new FieldExtractionsController(_storage.Object);
        SetupHttpContext(_controller);
    }

    [Fact]
    public async Task GetAll_ReturnsAllExtractions()
    {
        var extractions = new List<FieldExtraction>
        {
            new() { Name = "IP Extractor", Pattern = @"(?<ip>\d+\.\d+\.\d+\.\d+)" },
            new() { Name = "User Extractor", Pattern = @"user=(?<user>\w+)" }
        };
        _storage.Setup(s => s.GetAllAsync()).ReturnsAsync(extractions);

        var result = await _controller.GetAll();

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeAssignableTo<List<FieldExtraction>>().Subject;
        returned.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetById_Existing_ReturnsOk()
    {
        var extraction = new FieldExtraction { Id = "ext-1", Name = "IP Extractor", Pattern = @"\d+" };
        _storage.Setup(s => s.GetByIdAsync("ext-1")).ReturnsAsync(extraction);

        var result = await _controller.GetById("ext-1");

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<FieldExtraction>().Subject;
        returned.Name.Should().Be("IP Extractor");
    }

    [Fact]
    public async Task GetById_NotFound_Returns404()
    {
        _storage.Setup(s => s.GetByIdAsync("missing")).ReturnsAsync((FieldExtraction?)null);

        var result = await _controller.GetById("missing");

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Create_ValidExtraction_Returns201()
    {
        var extraction = new FieldExtraction
        {
            Name = "Error Extractor",
            Pattern = @"error:\s+(?<message>.+)"
        };
        _storage.Setup(s => s.SaveAsync(It.IsAny<FieldExtraction>()))
            .ReturnsAsync((FieldExtraction e) => e);

        var result = await _controller.Create(extraction);

        var createdResult = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        createdResult.StatusCode.Should().Be(201);
        var saved = createdResult.Value.Should().BeOfType<FieldExtraction>().Subject;
        saved.Name.Should().Be("Error Extractor");
        saved.Id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Create_EmptyName_ReturnsBadRequest()
    {
        var extraction = new FieldExtraction
        {
            Name = "",
            Pattern = @"\d+"
        };

        var result = await _controller.Create(extraction);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Create_EmptyPattern_ReturnsBadRequest()
    {
        var extraction = new FieldExtraction
        {
            Name = "Test",
            Pattern = ""
        };

        var result = await _controller.Create(extraction);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Create_InvalidRegex_ReturnsBadRequest()
    {
        var extraction = new FieldExtraction
        {
            Name = "Bad Regex",
            Pattern = @"[invalid("
        };

        var result = await _controller.Create(extraction);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Update_Existing_ReturnsOk()
    {
        var existing = new FieldExtraction
        {
            Id = "ext-1",
            Name = "Old Name",
            Pattern = @"\d+",
            CreatedAt = DateTime.UtcNow.AddDays(-1)
        };
        _storage.Setup(s => s.GetByIdAsync("ext-1")).ReturnsAsync(existing);
        _storage.Setup(s => s.SaveAsync(It.IsAny<FieldExtraction>()))
            .ReturnsAsync((FieldExtraction e) => e);

        var updated = new FieldExtraction
        {
            Name = "Updated Name",
            Pattern = @"(?<word>\w+)"
        };

        var result = await _controller.Update("ext-1", updated);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<FieldExtraction>().Subject;
        returned.Id.Should().Be("ext-1");
        returned.CreatedAt.Should().Be(existing.CreatedAt);
    }

    [Fact]
    public async Task Update_NotFound_Returns404()
    {
        _storage.Setup(s => s.GetByIdAsync("missing")).ReturnsAsync((FieldExtraction?)null);

        var result = await _controller.Update("missing", new FieldExtraction { Pattern = @"\d+" });

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Delete_Existing_ReturnsNoContent()
    {
        _storage.Setup(s => s.DeleteAsync("ext-1")).ReturnsAsync(true);

        var result = await _controller.Delete("ext-1");

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Delete_NotFound_Returns404()
    {
        _storage.Setup(s => s.DeleteAsync("missing")).ReturnsAsync(false);

        var result = await _controller.Delete("missing");

        result.Should().BeOfType<NotFoundResult>();
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
