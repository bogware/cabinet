using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Cabinet.API.Middleware;

/// <summary>
/// Attribute to require a specific RBAC capability on a controller action.
/// Works with the IRbacService to check capability grants per API key.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class RequireCapabilityAttribute : Attribute, IAsyncAuthorizationFilter
{
    private readonly Capability _capability;
    private readonly string _indexName;

    public RequireCapabilityAttribute(Capability capability, string indexName = "main")
    {
        _capability = capability;
        _indexName = indexName;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var rbac = context.HttpContext.RequestServices.GetService<IRbacService>();
        if (rbac == null)
        {
            // RBAC service not registered — fail closed to prevent unauthorized access
            context.Result = new JsonResult(new
            {
                error = "Service Unavailable",
                message = "Authorization service is unavailable"
            })
            { StatusCode = StatusCodes.Status503ServiceUnavailable };
            return;
        }

        var apiKeyHash = context.HttpContext.User?.FindFirst("ClientId")?.Value;
        if (string.IsNullOrEmpty(apiKeyHash))
        {
            // No API key claim — let normal auth handle it
            return;
        }

        // Apply tenant index scoping if configured
        var index = _indexName;
        var tenantPrefix = context.HttpContext.Items["TenantIndexPrefix"] as string;
        if (!string.IsNullOrEmpty(tenantPrefix) && !index.StartsWith(tenantPrefix))
        {
            index = tenantPrefix + index;
        }

        var hasCapability = await rbac.HasCapabilityAsync(apiKeyHash, _capability, index);
        if (!hasCapability)
        {
            context.Result = new JsonResult(new
            {
                error = "Forbidden",
                message = $"Missing required capability: {_capability}"
            })
            { StatusCode = StatusCodes.Status403Forbidden };
        }
    }
}
