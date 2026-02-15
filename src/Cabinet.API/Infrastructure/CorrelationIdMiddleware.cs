using System.Text.RegularExpressions;
using Serilog.Context;

namespace Cabinet.API.Infrastructure;

/// <summary>
/// Middleware that propagates or generates a correlation ID for distributed tracing.
/// Uses W3C-compatible X-Correlation-Id header.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    private const string CorrelationIdHeader = "X-Correlation-Id";
    private static readonly Regex CorrelationIdPattern = new(@"^[a-zA-Z0-9\-]+$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var rawCorrelationId = context.Request.Headers[CorrelationIdHeader].FirstOrDefault();

        // Validate incoming correlation ID: max 64 chars, alphanumeric + hyphens only
        string correlationId;
        if (!string.IsNullOrEmpty(rawCorrelationId)
            && rawCorrelationId.Length <= 64
            && CorrelationIdPattern.IsMatch(rawCorrelationId))
        {
            correlationId = rawCorrelationId;
        }
        else
        {
            correlationId = Guid.NewGuid().ToString("N");
        }

        context.Items["CorrelationId"] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.Append(CorrelationIdHeader, correlationId);
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }
}
