using Cabinet.API.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Cabinet.API.Middleware;

/// <summary>
/// For endpoints that act only on the caller's own local account, such as changing its password:
/// the caller must be signed in as a local account. API keys and single sign-on users are refused.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireLocalAccountAttribute : Attribute, IAuthorizationFilter
{
    /// <summary>The caller's directory user ID, once checked.</summary>
    public static string? UserIdOf(Microsoft.AspNetCore.Http.HttpContext context) =>
        context.User.FindFirst(DirectoryClaims.UserId)?.Value;

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            context.Result = new JsonResult(new { error = "Unauthorized", message = "Authentication is required" }) { StatusCode = StatusCodes.Status401Unauthorized };
            return;
        }

        var clientId = user.FindFirst("ClientId")?.Value;
        if (user.Identity.AuthenticationType != BrowserSession.Scheme
            || user.FindFirst(DirectoryClaims.UserId) == null || clientId == null || !clientId.StartsWith(DirectoryClaims.LocalPrefix, StringComparison.Ordinal))
        {
            context.Result = new JsonResult(new { error = "Forbidden", message = "Only someone signed in with a local account can do this" }) { StatusCode = StatusCodes.Status403Forbidden };
        }
    }
}
