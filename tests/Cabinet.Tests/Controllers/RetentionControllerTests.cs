using Cabinet.API.Controllers;
using Cabinet.API.Services;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Cabinet.Tests.Controllers;

public sealed class RetentionControllerTests
{
    private readonly InMemoryDocumentStore _documents = new();

    private RetentionController CreateController() => new(_documents)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
    };

    [Fact]
    public async Task SetPolicy_ThenGetIt_FromAnyInstanceOfTheController()
    {
        var saved = await CreateController().SetPolicy("web", new RetentionPolicyDto { MaxAgeDays = 7 });
        saved.Should().BeOfType<OkObjectResult>();

        // A new controller, as on the next request or another instance
        var read = await CreateController().GetPolicy("web");

        var policy = read.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<RetentionPolicyDto>().Subject;
        policy.Index.Should().Be("web");
        policy.MaxAgeDays.Should().Be(7);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3651)]
    public async Task SetPolicy_OutOfRangeAge_IsRejected(int days)
    {
        var result = await CreateController().SetPolicy("web", new RetentionPolicyDto { MaxAgeDays = days });

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task SetPolicy_EmptyIndexName_IsRejected()
    {
        var result = await CreateController().SetPolicy(" ", new RetentionPolicyDto());

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task GetPolicy_Unknown_IsNotFound()
    {
        (await CreateController().GetPolicy("nothing")).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task DeletePolicy_RemovesIt_AndAgainIsNotFound()
    {
        await CreateController().SetPolicy("web", new RetentionPolicyDto { MaxAgeDays = 7 });

        (await CreateController().DeletePolicy("web")).Should().BeOfType<NoContentResult>();
        (await CreateController().DeletePolicy("web")).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetPolicies_ReportsTotalAndPages()
    {
        var controller = CreateController();
        await controller.SetPolicy("a", new RetentionPolicyDto { MaxAgeDays = 1 });
        await controller.SetPolicy("b", new RetentionPolicyDto { MaxAgeDays = 2 });
        await controller.SetPolicy("c", new RetentionPolicyDto { MaxAgeDays = 3 });

        var result = await controller.GetPolicies(offset: 1, count: 1);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<List<RetentionPolicyDto>>().Which.Should().ContainSingle().Which.Index.Should().Be("b");
        controller.Response.Headers["X-Total-Count"].ToString().Should().Be("3");
    }
}
