using System.Net;
using Cabinet.API.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Cabinet.Tests.Middleware;

public class TrustedProxiesTests
{
    private static IConfiguration Configuration(params string[] proxies) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(proxies.Select((proxy, i) =>
                new KeyValuePair<string, string?>($"Network:TrustedProxies:{i}", proxy)))
            .Build();

    [Fact]
    public void Read_NothingConfigured_ReturnsEmpty()
    {
        TrustedProxies.Read(Configuration()).Should().BeEmpty();
    }

    [Fact]
    public void Read_InvalidAddress_Throws()
    {
        var act = () => TrustedProxies.Read(Configuration("not-an-ip"));

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void CreateOptions_TrustsOnlyConfiguredProxies()
    {
        var proxy = IPAddress.Parse("10.0.0.5");

        var options = TrustedProxies.CreateOptions([proxy]);

        options.KnownProxies.Should().ContainSingle().Which.Should().Be(proxy);
        options.KnownIPNetworks.Should().BeEmpty();
    }

    [Fact]
    public void CreateOptions_NoProxies_Throws()
    {
        // Empty known lists would make the forwarded-headers middleware trust every sender.
        var act = () => TrustedProxies.CreateOptions([]);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
