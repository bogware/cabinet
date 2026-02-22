using Cabinet.API.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Cabinet.Tests.Controllers;

public class RetentionControllerTests : IDisposable
{
    private readonly RetentionController _controller;

    public RetentionControllerTests()
    {
        _controller = new RetentionController
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    public void Dispose()
    {
        // Clean up the static dictionary between tests to avoid cross-test pollution.
        // We do this by deleting all policies we may have created.
        var result = _controller.GetPolicies();
        if (result is OkObjectResult okResult && okResult.Value is List<RetentionPolicyDto> policies)
        {
            foreach (var policy in policies)
            {
                _controller.DeletePolicy(policy.Index);
            }
        }
    }

    [Fact]
    public void GetPolicies_Empty_ReturnsEmptyList()
    {
        var result = _controller.GetPolicies();

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var policies = okResult.Value.Should().BeAssignableTo<List<RetentionPolicyDto>>().Subject;
        policies.Should().BeEmpty();
    }

    [Fact]
    public void SetPolicy_ValidPolicy_ReturnsOk()
    {
        var policy = new RetentionPolicyDto { MaxAgeDays = 30, ArchiveBeforeDelete = true };

        var result = _controller.SetPolicy("main", policy);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<RetentionPolicyDto>().Subject;
        returned.Index.Should().Be("main");
        returned.MaxAgeDays.Should().Be(30);
        returned.ArchiveBeforeDelete.Should().BeTrue();
    }

    [Fact]
    public void SetPolicy_EmptyIndex_ReturnsBadRequest()
    {
        var policy = new RetentionPolicyDto { MaxAgeDays = 30 };

        var result = _controller.SetPolicy("", policy);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public void SetPolicy_IndexTooLong_ReturnsBadRequest()
    {
        var policy = new RetentionPolicyDto { MaxAgeDays = 30 };

        var result = _controller.SetPolicy(new string('x', 257), policy);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public void SetPolicy_MaxAgeDaysTooLow_ReturnsBadRequest()
    {
        var policy = new RetentionPolicyDto { MaxAgeDays = 0 };

        var result = _controller.SetPolicy("main", policy);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public void SetPolicy_MaxAgeDaysTooHigh_ReturnsBadRequest()
    {
        var policy = new RetentionPolicyDto { MaxAgeDays = 3651 };

        var result = _controller.SetPolicy("main", policy);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public void GetPolicy_Found_ReturnsOk()
    {
        var policy = new RetentionPolicyDto { MaxAgeDays = 60 };
        _controller.SetPolicy("security", policy);

        var result = _controller.GetPolicy("security");

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<RetentionPolicyDto>().Subject;
        returned.Index.Should().Be("security");
        returned.MaxAgeDays.Should().Be(60);
    }

    [Fact]
    public void GetPolicy_NotFound_Returns404()
    {
        var result = _controller.GetPolicy("nonexistent");

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void DeletePolicy_Existing_ReturnsNoContent()
    {
        var policy = new RetentionPolicyDto { MaxAgeDays = 30 };
        _controller.SetPolicy("temp", policy);

        var result = _controller.DeletePolicy("temp");

        result.Should().BeOfType<NoContentResult>();

        // Verify it is actually gone
        _controller.GetPolicy("temp").Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void DeletePolicy_NotFound_Returns404()
    {
        var result = _controller.DeletePolicy("nonexistent");

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void GetPolicies_AfterMultipleSets_ReturnsAll()
    {
        _controller.SetPolicy("idx1", new RetentionPolicyDto { MaxAgeDays = 30 });
        _controller.SetPolicy("idx2", new RetentionPolicyDto { MaxAgeDays = 90 });

        var result = _controller.GetPolicies();

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var policies = okResult.Value.Should().BeAssignableTo<List<RetentionPolicyDto>>().Subject;
        policies.Should().HaveCount(2);
    }
}
