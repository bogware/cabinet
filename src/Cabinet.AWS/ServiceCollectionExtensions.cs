using Cabinet.AWS.Parsers;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Remote;
using Microsoft.Extensions.DependencyInjection;

namespace Cabinet.AWS;

/// <summary>
/// Service collection extensions for AWS integration
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add Cabinet AWS services
    /// </summary>
    public static IServiceCollection AddCabinetAWS(this IServiceCollection services)
    {
        // Register AWS log access implementations
        services.AddScoped<CloudWatchLogAccess>();
        services.AddScoped<CloudTrailLogAccess>();

        // Register as IRemoteLogAccess implementations
        services.AddScoped<IRemoteLogAccess, CloudWatchLogAccess>();
        services.AddScoped<IRemoteLogAccess, CloudTrailLogAccess>();

        // Register AWS parsers
        services.AddScoped<ILogParser, CloudWatchParser>();
        services.AddScoped<ILogParser, CloudTrailParser>();

        // Register network share access (for remote log management)
        services.AddScoped<IRemoteLogAccess, NetworkShareAccess>();

        return services;
    }
}