using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query;
using Cabinet.Query.Bql;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Query;

/// <summary>Uses the shared GeoIP lookup, so these don't run alongside each other.</summary>
[Collection(nameof(IplocationTests))]
[CollectionDefinition(nameof(IplocationTests), DisableParallelization = true)]
public sealed class IplocationTests
{
    private sealed class FakeLookup : IGeoIpLookup
    {
        public GeoLocation? Lookup(string address) => address == "81.2.69.160"
            ? new GeoLocation("London", "England", "United Kingdom", "GB", 51.5142, -0.0931)
            : null;
    }

    private static async Task<List<IDictionary<string, object?>>> RunAsync(string query)
    {
        var storage = new InMemoryStorage();
        var a = new LogEvent { RawLine = "from 81.2.69.160" };
        a.SetField("clientip", "81.2.69.160");
        var b = new LogEvent { RawLine = "from 10.0.0.1" };
        b.SetField("clientip", "10.0.0.1");
        await storage.StoreBatchAsync([a, b]);
        var engine = new BqlQueryEngine(storage, new BqlParser(), new BqlOptimizer(), NullLogger<BqlQueryEngine>.Instance);
        var result = await engine.ExecuteAsync(new QueryExecutionContext { Query = query });
        return result.Events.Select(e => (IDictionary<string, object?>)e).ToList();
    }

    [Fact]
    public async Task Iplocation_AddsWhereAnAddressIs()
    {
        var previous = BqlGeoIp.Lookup;
        BqlGeoIp.Lookup = new FakeLookup();
        try
        {
            var rows = await RunAsync("find * | iplocation clientip | stats count by Country, lat, lon");

            var row = rows.Should().ContainSingle("the private address has no location").Subject;
            row["Country"].Should().Be("United Kingdom");
            row["lat"].Should().Be(51.5142);

            var prefixed = await RunAsync("find * | iplocation prefix=src_ clientip | where isnotnull(src_City)");
            prefixed.Should().ContainSingle().Which["src_City"].Should().Be("London");
        }
        finally
        {
            BqlGeoIp.Lookup = previous;
        }
    }

    [Fact]
    public async Task Iplocation_WithoutADatabase_SaysHowToGetOne()
    {
        var previous = BqlGeoIp.Lookup;
        BqlGeoIp.Lookup = null;
        try
        {
            var act = () => RunAsync("find * | iplocation clientip");

            await act.Should().ThrowAsync<QueryExecutionException>().WithMessage("*GeoIp:DatabasePath*");
        }
        finally
        {
            BqlGeoIp.Lookup = previous;
        }
    }
}
