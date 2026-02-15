namespace Cabinet.API.Middleware;

/// <summary>
/// Request logging middleware
/// </summary>
public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = Guid.NewGuid().ToString("N");
        context.Items["RequestId"] = requestId;

        var clientId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anonymous";

        _logger.LogInformation(
            "Request {RequestId} started: {Method} {Path} from {ClientId}",
            requestId,
            context.Request.Method,
            context.Request.Path,
            clientId);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await _next(context);
        }
        finally
        {
            sw.Stop();

            var isError = context.Response.StatusCode >= 500;
            Cabinet.API.Controllers.HealthController.RecordRequest(isError);

            _logger.LogInformation(
                "Request {RequestId} completed: {StatusCode} in {ElapsedMs}ms",
                requestId,
                context.Response.StatusCode,
                sw.ElapsedMilliseconds);
        }
    }
}
