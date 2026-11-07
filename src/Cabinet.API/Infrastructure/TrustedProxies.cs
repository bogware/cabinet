using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Cabinet.API.Infrastructure;

/// <summary>
/// Reads <c>Network:TrustedProxies</c>, the proxies allowed to report a client's address
/// through <c>X-Forwarded-For</c>. Without any, the header is ignored, because anyone
/// could otherwise set it to dodge IP rate limits or forge audit addresses.
/// </summary>
public static class TrustedProxies
{
    /// <summary>
    /// Parses the configured proxy addresses. An invalid address throws, so a typo fails
    /// startup rather than silently disabling proxy support.
    /// </summary>
    public static IReadOnlyList<IPAddress> Read(IConfiguration configuration) =>
        (configuration.GetSection("Network:TrustedProxies").Get<string[]>() ?? [])
            .Select(IPAddress.Parse)
            .ToList();

    /// <summary>
    /// Creates middleware options trusting exactly the given proxies. Callers must not use
    /// them with an empty list: forwarded-header middleware with no known proxies or
    /// networks trusts every sender.
    /// </summary>
    public static ForwardedHeadersOptions CreateOptions(IReadOnlyList<IPAddress> proxies)
    {
        ArgumentOutOfRangeException.ThrowIfZero(proxies.Count);

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        };
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var proxy in proxies)
        {
            options.KnownProxies.Add(proxy);
        }

        return options;
    }
}
