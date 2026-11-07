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
            // No identity to check capabilities against, so deny. Letting this through
            // would grant every capability to any principal without the claim.
            var authenticated = context.HttpContext.User?.Identity?.IsAuthenticated == true;
            context.Result = new JsonResult(new
            {
                error = authenticated ? "Forbidden" : "Unauthorized",
                message = authenticated
                    ? "This identity has no capabilities assigned"
                    : "Authentication is required"
            })
            { StatusCode = authenticated ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized };
            return;
        }

        var hasCapability = await rbac.HasCapabilityAsync(apiKeyHash, _capability, _indexName);
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
