using System.Security.Claims;
using Cabinet.API.Controllers;
using Cabinet.API.Services;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Cabinet.Tests.Controllers;

public class DataModelControllerTests
{
    private readonly DataModelAccelerationService _accelerationService;
    private readonly DataModelController _controller;

    public DataModelControllerTests()
    {
        var scopeFactory = new Mock<IServiceScopeFactory>();
        var dataModelStorage = new Mock<IDataModelStorage>();
        dataModelStorage.Setup(s => s.SaveModelAsync(It.IsAny<DataModel>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        dataModelStorage.Setup(s => s.DeleteModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var serviceLogger = new Mock<ILogger<DataModelAccelerationService>>();
        _accelerationService = new DataModelAccelerationService(
            scopeFactory.Object,
            dataModelStorage.Object,
            serviceLogger.Object);

        var controllerLogger = new Mock<ILogger<DataModelController>>();
        _controller = new DataModelController(_accelerationService, controllerLogger.Object);
        SetupHttpContext(_controller);
    }

    [Fact]
    public void ListModels_Empty_ReturnsEmptyList()
    {
        var result = _controller.ListModels();

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var models = okResult.Value.Should().BeAssignableTo<IEnumerable<DataModelResponse>>().Subject;
        models.Should().BeEmpty();
    }

    [Fact]
    public async Task ListModels_WithModels_ReturnsAll()
    {
        await _accelerationService.RegisterModelAsync(new DataModel
        {
            Name = "Test Model",
            BaseQuery = "find *",
            Fields = new List<string> { "host" }
        });

        var result = _controller.ListModels();

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var models = okResult.Value.Should().BeAssignableTo<IEnumerable<DataModelResponse>>().Subject;
        models.Should().HaveCount(1);
        models.First().Name.Should().Be("Test Model");
    }

    [Fact]
    public async Task CreateModel_ValidRequest_Returns201()
    {
        var request = new CreateDataModelRequest
        {
            Name = "Web Errors",
            BaseQuery = "find * | where severity >= 4",
            Fields = new List<string> { "host", "status" },
            BucketSpanMinutes = 60,
            RetentionDays = 30
        };

        var result = await _controller.CreateModel(request, CancellationToken.None);

        var createdResult = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        createdResult.StatusCode.Should().Be(201);
        var response = createdResult.Value.Should().BeOfType<DataModelResponse>().Subject;
        response.Name.Should().Be("Web Errors");
        response.BaseQuery.Should().Be("find * | where severity >= 4");
        response.Fields.Should().Contain("host");
        response.BucketSpanMinutes.Should().Be(60);
        response.RetentionDays.Should().Be(30);
    }

    [Fact]
    public async Task CreateModel_EmptyName_ReturnsBadRequest()
    {
        var request = new CreateDataModelRequest
        {
            Name = "",
            BaseQuery = "find *",
            Fields = new List<string> { "host" }
        };

        var result = await _controller.CreateModel(request, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task CreateModel_NoFields_ReturnsBadRequest()
    {
        var request = new CreateDataModelRequest
        {
            Name = "Test",
            BaseQuery = "find *",
            Fields = new List<string>()
        };

        var result = await _controller.CreateModel(request, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task CreateModel_TooManyFields_ReturnsBadRequest()
    {
        var request = new CreateDataModelRequest
        {
            Name = "Test",
            BaseQuery = "find *",
            Fields = Enumerable.Range(0, 51).Select(i => $"field{i}").ToList()
        };

        var result = await _controller.CreateModel(request, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task GetModel_Existing_ReturnsOk()
    {
        var model = await _accelerationService.RegisterModelAsync(new DataModel
        {
            Name = "My Model",
            BaseQuery = "find *",
            Fields = new List<string> { "host" }
        });

        var result = _controller.GetModel(model.Id);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<DataModelResponse>().Subject;
        response.Name.Should().Be("My Model");
        response.Id.Should().Be(model.Id);
    }

    [Fact]
    public void GetModel_NotFound_Returns404()
    {
        var result = _controller.GetModel("nonexistent-id");

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task DeleteModel_Existing_ReturnsNoContent()
    {
        var model = await _accelerationService.RegisterModelAsync(new DataModel
        {
            Name = "To Delete",
            BaseQuery = "find *",
            Fields = new List<string> { "host" }
        });

        var result = await _controller.DeleteModelAsync(model.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task DeleteModel_NotFound_Returns404()
    {
        var result = await _controller.DeleteModelAsync("nonexistent-id", CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task ExecuteTstats_ModelNotFound_Returns404()
    {
        var request = new TstatsRequest();

        var result = await _controller.ExecuteTstats("nonexistent", request, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task ExecuteTstats_ValidModel_ReturnsResults()
    {
        var model = await _accelerationService.RegisterModelAsync(new DataModel
        {
            Name = "Tstats Test",
            BaseQuery = "find *",
            Fields = new List<string> { "host" }
        });

        var request = new TstatsRequest
        {
            GroupBy = new List<string> { "host" }
        };

        var result = await _controller.ExecuteTstats(model.Id, request, CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<TstatsResponse>().Subject;
        response.ModelId.Should().Be(model.Id);
        response.ModelName.Should().Be("Tstats Test");
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
