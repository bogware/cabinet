using Cabinet.Core.Interfaces;
using Cabinet.Extras.Gaming.Detection;
using Cabinet.Extras.Gaming.Parsers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cabinet.Extras.Gaming;

/// <summary>
/// Optional support for game server logs: L4D2, SourceMod and SteamCMD parsers, and the
/// moderation detectors. Off unless <c>Gaming:Enabled</c> is true, because these parsers match
/// loosely enough to claim ordinary log lines.
/// </summary>
public static class GamingExtension
{
    /// <summary>Configuration key that turns the extension on.</summary>
    public const string EnabledKey = "Gaming:Enabled";

    /// <summary>Whether the extension is turned on.</summary>
    public static bool IsEnabled(IConfiguration configuration) =>
        configuration.GetValue<bool>(EnabledKey);

    /// <summary>Registers the game log parsers, most specific first.</summary>
    public static IServiceCollection AddGamingParsers(this IServiceCollection services)
    {
        services.AddSingleton<ILogParser, L4D2Parser>();
        services.AddSingleton<ILogParser, SourceModParser>();
        services.AddSingleton<ILogParser, SteamCMDParser>();
        return services;
    }

    /// <summary>Registers the moderation detectors behind the detection API.</summary>
    public static IServiceCollection AddGamingDetection(this IServiceCollection services)
    {
        services.AddScoped<IScumscanDetector, ScumscanDetector>();
        services.AddScoped<IHackwatchDetector, HackwatchDetector>();
        services.AddScoped<IAdminabuseDetector, AdminabuseDetector>();
        return services;
    }
}

/// <summary>
/// Source types produced by the game log parsers.
/// </summary>
public static class GamingSourceTypes
{
    public const string SteamCMD = "SteamCMD";
    public const string SourceMod = "SourceMod";
    public const string L4D2 = "L4D2";
}
