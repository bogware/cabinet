using Cabinet.API.Infrastructure;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Cabinet.API.Middleware;

/// <summary>Which configurable body size limit an endpoint uses.</summary>
public enum RequestSizeLimitKind
{
    /// <summary><see cref="ServerLimits.HecMaxRequestMB"/>.</summary>
    Hec,

    /// <summary><see cref="ServerLimits.OtlpMaxRequestMB"/>.</summary>
    Otlp,
}

/// <summary>
/// Like <c>[RequestSizeLimit]</c>, but the limit comes from <see cref="ServerLimits"/>, so it can
/// be changed in settings. Runs before the body is read.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class ConfiguredRequestSizeLimitAttribute : Attribute, IResourceFilter
{
    private readonly RequestSizeLimitKind _kind;

    public ConfiguredRequestSizeLimitAttribute(RequestSizeLimitKind kind)
    {
        _kind = kind;
    }

    /// <summary>The limit in bytes for a kind of request.</summary>
    public static long BytesFor(ServerLimits limits, RequestSizeLimitKind kind) =>
        (kind == RequestSizeLimitKind.Hec ? limits.HecMaxRequestMB : limits.OtlpMaxRequestMB) * ServerLimits.Megabyte;

    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var limits = context.HttpContext.RequestServices.GetService<ServerLimits>() ?? new ServerLimits();
        var feature = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = BytesFor(limits, _kind);
        }
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
        // Nothing to undo: the limit belongs to this request
    }
}
