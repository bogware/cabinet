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

public class KnowledgeControllerTests
{
    private readonly Mock<IKnowledgeStorage> _storage;
    private readonly Mock<IAuditLogger> _auditLogger;
    private readonly KnowledgeController _controller;

    public KnowledgeControllerTests()
    {
        _storage = new Mock<IKnowledgeStorage>();
        _auditLogger = new Mock<IAuditLogger>();
        var logger = new Mock<ILogger<KnowledgeController>>();

        _controller = new KnowledgeController(_storage.Object, _auditLogger.Object, logger.Object);
        SetupHttpContext(_controller);
    }

    // ===== Lookup Tests =====

    [Fact]
    public async Task ListLookups_ReturnsPaginatedResults()
    {
        var lookups = new List<LookupTable>
        {
            new() { Name = "Lookup 1" },
            new() { Name = "Lookup 2" }
        };
        _storage.Setup(s => s.GetAllLookupsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(lookups);

        var result = await _controller.ListLookups();

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task GetLookup_Existing_ReturnsOk()
    {
        var lookup = new LookupTable { Name = "Users" };
        _storage.Setup(s => s.GetLookupByIdAsync(lookup.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(lookup);

        var result = await _controller.GetLookup(lookup.Id);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<LookupTable>().Subject;
        returned.Name.Should().Be("Users");
    }

    [Fact]
    public async Task GetLookup_NotFound_Returns404()
    {
        _storage.Setup(s => s.GetLookupByIdAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((LookupTable?)null);

        var result = await _controller.GetLookup("missing");

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task CreateLookup_ValidRequest_Returns201()
    {
        var request = new CreateLookupRequest
        {
            Name = "IP Geolocation",
            Description = "Maps IPs to countries",
            Columns = new List<string> { "ip", "country" },
            Rows = new List<Dictionary<string, string>>
            {
                new() { { "ip", "1.1.1.1" }, { "country", "US" } }
            }
        };
        _storage.Setup(s => s.SaveLookupAsync(It.IsAny<LookupTable>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LookupTable l, CancellationToken _) => l);

        var result = await _controller.CreateLookup(request);

        var createdResult = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        createdResult.StatusCode.Should().Be(201);
        var lookup = createdResult.Value.Should().BeOfType<LookupTable>().Subject;
        lookup.Name.Should().Be("IP Geolocation");
        lookup.Columns.Should().Contain("ip");
    }

    [Fact]
    public async Task CreateLookup_EmptyName_ReturnsBadRequest()
    {
        var request = new CreateLookupRequest { Name = "" };

        var result = await _controller.CreateLookup(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task CreateLookup_NameTooLong_ReturnsBadRequest()
    {
        var request = new CreateLookupRequest
        {
            Name = new string('x', 201)
        };

        var result = await _controller.CreateLookup(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task CreateLookup_TooManyRows_ReturnsBadRequest()
    {
        var request = new CreateLookupRequest
        {
            Name = "Big Lookup",
            Rows = Enumerable.Range(0, 10_001)
                .Select(_ => new Dictionary<string, string> { { "key", "val" } })
                .ToList()
        };

        var result = await _controller.CreateLookup(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task DeleteLookup_Existing_ReturnsNoContent()
    {
        _storage.Setup(s => s.DeleteLookupAsync("lookup-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _controller.DeleteLookup("lookup-1");

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task DeleteLookup_NotFound_Returns404()
    {
        _storage.Setup(s => s.DeleteLookupAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _controller.DeleteLookup("missing");

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(404);
    }

    // ===== Macro Tests =====

    [Fact]
    public async Task ListMacros_ReturnsAll()
    {
        var macros = new List<QueryMacro>
        {
            new() { Name = "error_filter", Definition = "where severity >= 4" }
        };
        _storage.Setup(s => s.GetAllMacrosAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(macros);

        var result = await _controller.ListMacros();

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateMacro_ValidRequest_Returns201()
    {
        var request = new CreateMacroRequest
        {
            Name = "error_filter",
            Definition = "where severity >= 4",
            Description = "Filters for errors"
        };
        _storage.Setup(s => s.SaveMacroAsync(It.IsAny<QueryMacro>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((QueryMacro m, CancellationToken _) => m);

        var result = await _controller.CreateMacro(request);

        var createdResult = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        createdResult.StatusCode.Should().Be(201);
        var macro = createdResult.Value.Should().BeOfType<QueryMacro>().Subject;
        macro.Name.Should().Be("error_filter");
        macro.Definition.Should().Be("where severity >= 4");
    }

    [Fact]
    public async Task CreateMacro_EmptyName_ReturnsBadRequest()
    {
        var request = new CreateMacroRequest { Name = "", Definition = "test" };

        var result = await _controller.CreateMacro(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task CreateMacro_EmptyDefinition_ReturnsBadRequest()
    {
        var request = new CreateMacroRequest { Name = "test", Definition = "" };

        var result = await _controller.CreateMacro(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task CreateMacro_InvalidValidationRegex_ReturnsBadRequest()
    {
        var request = new CreateMacroRequest
        {
            Name = "test",
            Definition = "where x > 1",
            ValidationRegex = "[invalid("
        };

        var result = await _controller.CreateMacro(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    // ===== Alias Tests =====

    [Fact]
    public async Task ListAliases_ReturnsAll()
    {
        var aliases = new List<FieldAlias>
        {
            new() { OriginalField = "host", AliasField = "server" }
        };
        _storage.Setup(s => s.GetAllAliasesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(aliases);

        var result = await _controller.ListAliases();

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateAlias_ValidRequest_Returns201()
    {
        var request = new CreateAliasRequest
        {
            OriginalField = "host",
            AliasField = "server"
        };
        _storage.Setup(s => s.SaveAliasAsync(It.IsAny<FieldAlias>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FieldAlias a, CancellationToken _) => a);

        var result = await _controller.CreateAlias(request);

        var createdResult = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        createdResult.StatusCode.Should().Be(201);
        var alias = createdResult.Value.Should().BeOfType<FieldAlias>().Subject;
        alias.OriginalField.Should().Be("host");
        alias.AliasField.Should().Be("server");
    }

    [Fact]
    public async Task CreateAlias_EmptyOriginalField_ReturnsBadRequest()
    {
        var request = new CreateAliasRequest
        {
            OriginalField = "",
            AliasField = "server"
        };

        var result = await _controller.CreateAlias(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task CreateAlias_SelfReferential_ReturnsBadRequest()
    {
        var request = new CreateAliasRequest
        {
            OriginalField = "host",
            AliasField = "host"
        };

        var result = await _controller.CreateAlias(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task CreateAlias_AuditLogsCreation()
    {
        var request = new CreateAliasRequest
        {
            OriginalField = "host",
            AliasField = "server"
        };
        _storage.Setup(s => s.SaveAliasAsync(It.IsAny<FieldAlias>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FieldAlias a, CancellationToken _) => a);

        await _controller.CreateAlias(request);

        _auditLogger.Verify(a => a.LogAsync(
            It.Is<AuditEvent>(e =>
                e.Action == AuditAction.Create &&
                e.ResourceType == "FieldAlias"),
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
