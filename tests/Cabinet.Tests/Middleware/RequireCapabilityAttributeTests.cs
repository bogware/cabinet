using System.Security.Claims;
using Cabinet.API.Middleware;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Cabinet.Tests.Middleware;

public class RequireCapabilityAttributeTests
{
    private readonly Mock<IRbacService> _rbac = new();

    private AuthorizationFilterContext CreateContext(ClaimsPrincipal user)
    {
        var services = new ServiceCollection().AddSingleton(_rbac.Object).BuildServiceProvider();
        var httpContext = new DefaultHttpContext { User = user, RequestServices = services };
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());
    }

    private static ClaimsPrincipal Authenticated(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "Test"));

    private static int? StatusOf(AuthorizationFilterContext context) =>
        (context.Result as JsonResult)?.StatusCode;

    [Fact]
    public async Task Authenticated_WithoutClientId_IsForbidden()
    {
        var context = CreateContext(Authenticated(new Claim("Tier", "Enterprise")));

        await new RequireCapabilityAttribute(Capability.IndexManagement).OnAuthorizationAsync(context);

        StatusOf(context).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Authenticated_WithEmptyClientId_IsForbidden()
    {
        var context = CreateContext(Authenticated(new Claim("ClientId", string.Empty)));

        await new RequireCapabilityAttribute(Capability.IndexManagement).OnAuthorizationAsync(context);

        StatusOf(context).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Unauthenticated_IsUnauthorized()
    {
        var context = CreateContext(new ClaimsPrincipal(new ClaimsIdentity()));

        await new RequireCapabilityAttribute(Capability.Search).OnAuthorizationAsync(context);

        StatusOf(context).Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task ClientWithoutCapability_IsForbidden()
    {
        _rbac.Setup(r => r.HasCapabilityAsync("client", Capability.Export, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var context = CreateContext(Authenticated(new Claim("ClientId", "client")));

        await new RequireCapabilityAttribute(Capability.Export).OnAuthorizationAsync(context);

        StatusOf(context).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task ClientWithCapability_IsAllowed()
    {
        _rbac.Setup(r => r.HasCapabilityAsync("client", Capability.Search, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var context = CreateContext(Authenticated(new Claim("ClientId", "client")));

        await new RequireCapabilityAttribute(Capability.Search).OnAuthorizationAsync(context);

        context.Result.Should().BeNull();
    }
}
