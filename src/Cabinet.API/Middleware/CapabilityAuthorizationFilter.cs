using Cabinet.API.Authentication;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Cabinet.API.Middleware;

/// <summary>
/// Attribute to require a specific RBAC capability on a controller action, on at least one index.
/// Also records the caller's principal and readable indexes in <see cref="IndexAccessScope"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class RequireCapabilityAttribute : Attribute, IAsyncAuthorizationFilter
{
    private readonly Capability _capability;

    public RequireCapabilityAttribute(Capability capability)
    {
        _capability = capability;
    }

    /// <summary>The capability this requires.</summary>
    public Capability Capability => _capability;

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

        var user = context.HttpContext.User;
        var principal = user == null ? null : await PrincipalResolver.ResolveAsync(
            user, rbac, context.HttpContext.RequestAborted, context.HttpContext.RequestServices.GetService<Services.Identity.UserDirectory>());
        if (principal == null)
        {
            // No identity to check capabilities against, so deny. Letting this through
            // would grant every capability to any principal without the claim.
            var authenticated = user?.Identity?.IsAuthenticated == true;
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

        // Which indexes it applies to is for the storage scope and the action to enforce, since
        // only they know which index is involved.
        var scope = context.HttpContext.RequestServices.GetService<IndexAccessScope>();
        if (scope != null)
        {
            scope.Principal = principal;
            scope.ReadableIndexes = principal.IndexesWith(Capability.Search);
        }

        if (!principal.HasCapabilityOnAnyIndex(_capability))
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
