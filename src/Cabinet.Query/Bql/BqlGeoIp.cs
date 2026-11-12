using System.Net;
using Cabinet.Core.Interfaces;
using MaxMind.Db;

namespace Cabinet.Query.Bql;

/// <summary>The GeoIP database queries use, set at startup when one is configured.</summary>
public static class BqlGeoIp
{
    /// <summary>The lookup, or null when no database is configured.</summary>
    public static IGeoIpLookup? Lookup { get; set; }
}

/// <summary>
/// Reads a MaxMind database (GeoLite2-City or GeoIP2-City, <c>.mmdb</c>). The file is mapped
/// into memory and reads are thread-safe.
/// </summary>
public sealed class MaxMindGeoIpLookup : IGeoIpLookup, IDisposable
{
    private readonly Reader _reader;

    public MaxMindGeoIpLookup(string path)
    {
        _reader = new Reader(path, FileAccessMode.MemoryMapped);
    }

    /// <inheritdoc />
    public GeoLocation? Lookup(string address)
    {
        if (!IPAddress.TryParse(address.Trim(), out var ip))
        {
            return null;
        }

        Dictionary<string, object>? found;
        try
        {
            found = _reader.Find<Dictionary<string, object>>(ip);
        }
        catch (InvalidDatabaseException)
        {
            return null;
        }

        if (found == null)
        {
            return null;
        }

        var location = Section(found, "location");
        return new GeoLocation(
            City: EnglishName(Section(found, "city")),
            Region: found.TryGetValue("subdivisions", out var subdivisions) && subdivisions is List<object> { Count: > 0 } list && list[0] is Dictionary<string, object> first ? EnglishName(first) : null,
            Country: EnglishName(Section(found, "country")),
            CountryCode: Section(found, "country") is { } country && country.TryGetValue("iso_code", out var code) ? code?.ToString() : null,
            Latitude: location != null && location.TryGetValue("latitude", out var lat) ? Convert.ToDouble(lat, System.Globalization.CultureInfo.InvariantCulture) : null,
            Longitude: location != null && location.TryGetValue("longitude", out var lon) ? Convert.ToDouble(lon, System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    private static Dictionary<string, object>? Section(Dictionary<string, object> data, string name) =>
        data.TryGetValue(name, out var value) ? value as Dictionary<string, object> : null;

    private static string? EnglishName(Dictionary<string, object>? section) =>
        section != null && section.TryGetValue("names", out var names) && names is Dictionary<string, object> byLanguage
            && byLanguage.TryGetValue("en", out var english) ? english?.ToString() : null;

    public void Dispose() => _reader.Dispose();
}

/// <summary>iplocation [prefix=x] field: adds City, Region, Country, lat and lon for an IP address field.</summary>
public sealed class IplocationStage : BqlStage
{
    public override string StageName => "iplocation";

    /// <summary>The field holding the address.</summary>
    public required string Field { get; init; }

    /// <summary>Put before each added field's name.</summary>
    public string Prefix { get; init; } = string.Empty;
}

internal sealed class LegacyIplocationOperator : QueryOperator
{
    private readonly IplocationStage _stage;
    private readonly IGeoIpLookup _lookup;

    public LegacyIplocationOperator(IplocationStage stage)
    {
        _stage = stage;
        _lookup = BqlGeoIp.Lookup
            ?? throw new QueryExecutionException("iplocation needs a GeoIP database: set GeoIp:DatabasePath to a MaxMind GeoLite2-City .mmdb file");
    }

    public override string Name => "iplocation";

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken cancellationToken = default) =>
        Task.FromResult<IEnumerable<dynamic>>(((IEnumerable<object>)input).Select(row =>
        {
            var dict = (IDictionary<string, object?>)row;
            if (dict.TryGetValue(_stage.Field, out var value) && value?.ToString() is { } address && _lookup.Lookup(address) is { } where)
            {
                dict[$"{_stage.Prefix}City"] = where.City;
                dict[$"{_stage.Prefix}Region"] = where.Region;
                dict[$"{_stage.Prefix}Country"] = where.Country;
                dict[$"{_stage.Prefix}country_code"] = where.CountryCode;
                dict[$"{_stage.Prefix}lat"] = where.Latitude;
                dict[$"{_stage.Prefix}lon"] = where.Longitude;
            }

            return row;
        }));
}
