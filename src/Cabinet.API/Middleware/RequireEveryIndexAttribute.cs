using Cabinet.API.Authentication;
using Cabinet.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Cabinet.API.Middleware;

/// <summary>
/// Requires a capability on every index, not just some. For things that act across all indexes
/// whoever triggers them: detection rules, data models, macros and lookups that scheduled searches
/// use, and backups of the whole database. Someone limited to some indexes could otherwise use
/// them to read or change the rest. Runs after <see cref="RequireCapabilityAttribute"/>, which
/// identifies the caller.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireEveryIndexAttribute : Attribute, IAsyncActionFilter
{
    private readonly Capability _capability;

    public RequireEveryIndexAttribute(Capability capability)
    {
        _capability = capability;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var principal = context.HttpContext.RequestServices.GetService<IndexAccessScope>()?.Principal;
        if (principal == null || principal.IndexesWith(_capability) != null)
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Forbidden",
                Detail = $"This applies to every index, so it needs {_capability} on all of them",
            })
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
            return;
        }

        await next();
    }
}
