using System.Net;
using System.Net.Sockets;

namespace Cabinet.API.Services;

/// <summary>Where outbound calls that users configure (webhooks, connectors) may go.</summary>
public sealed class OutboundUrlOptions
{
    /// <summary>
    /// Allow private, loopback and link-local addresses. Off by default, so a rule or connector
    /// can't be pointed at this server, the cloud metadata endpoint or the internal network.
    /// </summary>
    public bool AllowPrivateTargets { get; set; }

    /// <summary>Hosts allowed even when private targets aren't, such as an internal ticketing server.</summary>
    public List<string> AllowedHosts { get; set; } = [];
}

/// <summary>
/// Stops server-side request forgery through URLs people configure. A URL is checked when it's
/// saved, and again when connecting, against the address DNS actually returned, so a name that
/// resolves somewhere public when saved can't later point inside the network.
/// </summary>
public sealed class OutboundUrlGuard
{
    private readonly OutboundUrlOptions _options;

    public OutboundUrlGuard(OutboundUrlOptions options)
    {
        _options = options;
    }

    /// <summary>Checks a URL as entered. Returns why it isn't allowed, or null.</summary>
    public string? Check(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return "Needs an http or https URL";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "Put credentials in a connector, not in the URL";
        }

        if (IsAllowedHost(uri.Host))
        {
            return null;
        }

        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return _options.AllowPrivateTargets ? null : "Loopback addresses aren't allowed";
        }

        if (IPAddress.TryParse(uri.Host, out var address) && !_options.AllowPrivateTargets && IsPrivate(address))
        {
            return "Private, link-local and metadata addresses aren't allowed";
        }

        return null;
    }

    /// <summary>
    /// An HTTP handler that refuses connections to disallowed addresses and never follows
    /// redirects, which could otherwise lead a permitted URL somewhere that isn't.
    /// </summary>
    public SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host, cancellationToken);

            if (!IsAllowedHost(host) && !_options.AllowPrivateTargets)
            {
                addresses = addresses.Where(address => !IsPrivate(address)).ToArray();
            }

            if (addresses.Length == 0)
            {
                throw new HttpRequestException($"Connecting to {host} isn't allowed: it resolves only to private or local addresses");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    private bool IsAllowedHost(string host) =>
        _options.AllowedHosts.Any(allowed => string.Equals(allowed.Trim(), host, StringComparison.OrdinalIgnoreCase));

    /// <summary>Loopback, private, link-local (including cloud metadata), CGNAT, multicast and unspecified.</summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                || (address.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7 unique local
        }

        var b = address.GetAddressBytes();
        return b[0] == 10
            || b[0] == 127
            || b[0] == 0
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            || b[0] >= 224;
    }
}
