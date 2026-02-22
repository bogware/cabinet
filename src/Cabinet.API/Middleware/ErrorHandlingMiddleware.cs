namespace Cabinet.API.Middleware;

/// <summary>
/// Error handling middleware
/// </summary>
public class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorHandlingMiddleware> _logger;

    public ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected — this is normal, not an error
            _logger.LogDebug("Request {RequestId} was cancelled by client", context.Items["RequestId"]);
            context.Response.StatusCode = 499; // Client Closed Request (nginx convention)
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in request {RequestId}", context.Items["RequestId"]);
            await HandleExceptionAsync(context, ex);
        }
    }

    private static async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        context.Response.StatusCode = exception switch
        {
            ArgumentException => StatusCodes.Status400BadRequest,
            UnauthorizedAccessException => StatusCodes.Status401Unauthorized,
            KeyNotFoundException => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status500InternalServerError
        };

        context.Response.ContentType = "application/problem+json";
        var problemDetails = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = context.Response.StatusCode,
            Title = context.Response.StatusCode switch
            {
                400 => "Bad Request",
                401 => "Unauthorized",
                404 => "Not Found",
                _ => "Internal Server Error"
            },
            Detail = exception switch
            {
                ArgumentException ae => $"Invalid request parameters: {ae.Message}",
                KeyNotFoundException knf => $"The requested resource was not found: {knf.Message}",
                UnauthorizedAccessException => "Access denied.",
                _ => "An unexpected error occurred."
            },
            Instance = context.Request.Path,
            Extensions =
            {
                ["requestId"] = context.Items["RequestId"]?.ToString()
            }
        };

        await context.Response.WriteAsJsonAsync(problemDetails);
    }
}
