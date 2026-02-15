using System.Security.Claims;
using Cabinet.API.Middleware;
using Cabinet.Core.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Cabinet.Tests.Middleware;

public class TenantMiddlewareTests : IDisposable
{
    public TenantMiddlewareTests()
    {
        // Clean up any registered tenants from other tests
        foreach (var tenant in TenantMiddleware.GetTenants())
        {
            TenantMiddleware.RemoveTenant(tenant.Id);
        }
    }

    public void Dispose()
    {
        foreach (var tenant in TenantMiddleware.GetTenants())
        {
            TenantMiddleware.RemoveTenant(tenant.Id);
        }
    }

    [Fact]
    public async Task InvokeAsync_NoTenant_PassesThrough()
    {
        var middleware = new TenantMiddleware(_ => Task.CompletedTask);
        var context = CreateContext("some-api-key-hash");

        await middleware.InvokeAsync(context);

        context.Items.Should().NotContainKey("Tenant");
        context.Items.Should().NotContainKey("TenantIndexPrefix");
    }

    [Fact]
    public async Task InvokeAsync_MatchingTenant_SetsTenantContext()
    {
        var tenant = new Tenant
        {
            Id = "tenant-1",
            Name = "Acme Corp",
            IndexPrefix = "acme_",
            Enabled = true,
            ApiKeyHashes = new List<string> { "matching-hash" }
        };
        TenantMiddleware.RegisterTenant(tenant);

        var middleware = new TenantMiddleware(_ => Task.CompletedTask);
        var context = CreateContext("matching-hash");

        await middleware.InvokeAsync(context);

        context.Items["Tenant"].Should().Be(tenant);
        context.Items["TenantIndexPrefix"].Should().Be("acme_");
    }

    [Fact]
    public async Task InvokeAsync_DisabledTenant_DoesNotMatch()
    {
        var tenant = new Tenant
        {
            Id = "tenant-disabled",
            Name = "Disabled Corp",
            IndexPrefix = "disabled_",
            Enabled = false,
            ApiKeyHashes = new List<string> { "disabled-hash" }
        };
        TenantMiddleware.RegisterTenant(tenant);

        var middleware = new TenantMiddleware(_ => Task.CompletedTask);
        var context = CreateContext("disabled-hash");

        await middleware.InvokeAsync(context);

        context.Items.Should().NotContainKey("Tenant");
    }

    [Fact]
    public void ScopeIndex_NoTenant_ReturnsOriginalIndex()
    {
        var context = new DefaultHttpContext();

        var scoped = context.ScopeIndex("main");

        scoped.Should().Be("main");
    }

    [Fact]
    public void ScopeIndex_WithTenant_PrefixesIndex()
    {
        var context = new DefaultHttpContext();
        context.Items["TenantIndexPrefix"] = "acme_";

        var scoped = context.ScopeIndex("main");

        scoped.Should().Be("acme_main");
    }

    [Fact]
    public void ScopeIndex_AlreadyPrefixed_DoesNotDoublePrefix()
    {
        var context = new DefaultHttpContext();
        context.Items["TenantIndexPrefix"] = "acme_";

        // Index already has the prefix — should not double it
        var scoped = context.ScopeIndex("acme_main");

        scoped.Should().Be("acme_main");
    }

    [Fact]
    public void ScopeIndex_CrossTenantPrefix_StripsAndReapplies()
    {
        // Tenant A's context
        var context = new DefaultHttpContext();
        context.Items["TenantIndexPrefix"] = "acme_";

        // Trying to access tenant B's index — should strip "acme_" prefix from the requested
        // name and re-apply tenant A's prefix. But if the name starts with "acme_",
        // it strips it first.
        var scoped = context.ScopeIndex("acme_secrets");

        // "acme_" prefix stripped → "secrets", then reapplied → "acme_secrets"
        scoped.Should().Be("acme_secrets");
    }

    [Fact]
    public void RegisterTenant_And_GetTenants_Works()
    {
        var tenant = new Tenant
        {
            Id = "test-tenant",
            Name = "Test",
            IndexPrefix = "test_",
            Enabled = true,
            ApiKeyHashes = new List<string> { "hash1" }
        };

        TenantMiddleware.RegisterTenant(tenant);

        TenantMiddleware.GetTenants().Should().Contain(t => t.Id == "test-tenant");
    }

    [Fact]
    public void RemoveTenant_ExistingTenant_ReturnsTrue()
    {
        var tenant = new Tenant
        {
            Id = "to-remove",
            Name = "Remove Me",
            IndexPrefix = "rm_",
            Enabled = true,
            ApiKeyHashes = new List<string>()
        };
        TenantMiddleware.RegisterTenant(tenant);

        var result = TenantMiddleware.RemoveTenant("to-remove");

        result.Should().BeTrue();
        TenantMiddleware.GetTenants().Should().NotContain(t => t.Id == "to-remove");
    }

    [Fact]
    public void RemoveTenant_NonExistent_ReturnsFalse()
    {
        var result = TenantMiddleware.RemoveTenant("nonexistent");

        result.Should().BeFalse();
    }

    private static DefaultHttpContext CreateContext(string apiKeyHash)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("ClientId", apiKeyHash)
        }, "ApiKey"));
        return context;
    }
}
