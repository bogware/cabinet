namespace Cabinet.API.Middleware;

/// <summary>
/// Adds API version headers to all responses.
/// Supports version negotiation via Accept-Version header.
/// </summary>
public sealed class ApiVersionMiddleware
{
    private readonly RequestDelegate _next;
    private const string CurrentVersion = "1.0";
    private const string MinSupportedVersion = "1.0";

    public ApiVersionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var requestedVersion = context.Request.Headers["Accept-Version"].FirstOrDefault();

        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-API-Version"] = CurrentVersion;
            context.Response.Headers["X-API-Min-Version"] = MinSupportedVersion;

            if (requestedVersion != null && requestedVersion != CurrentVersion)
            {
                context.Response.Headers["X-API-Version-Warning"] =
                    $"Requested version {requestedVersion}; serving {CurrentVersion}";
            }

            return Task.CompletedTask;
        });

        await _next(context);
    }
}
