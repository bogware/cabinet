namespace Cabinet.Core.Interfaces;

/// <summary>Finds where an IP address is, from a GeoIP database.</summary>
public interface IGeoIpLookup
{
    /// <summary>Where the address is, or null for one the database doesn't know, such as a private one.</summary>
    GeoLocation? Lookup(string address);
}

/// <summary>Where an IP address is. Any part the database doesn't have is null.</summary>
/// <param name="City">The city's English name.</param>
/// <param name="Region">The first subdivision, such as a state or province.</param>
/// <param name="Country">The country's English name.</param>
/// <param name="CountryCode">The ISO 3166-1 alpha-2 country code.</param>
/// <param name="Latitude">Latitude in degrees.</param>
/// <param name="Longitude">Longitude in degrees.</param>
public sealed record GeoLocation(string? City, string? Region, string? Country, string? CountryCode, double? Latitude, double? Longitude);
