#pragma warning disable CS0612, CS0618 // Type or member is obsolete
using Cabinet.Database.Abstractions;
using Cabinet.Database.Configuration;
using Cabinet.Database.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cabinet.Database;

/// <summary>
/// Extension methods for registering database services
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add Cabinet database services
    /// </summary>
    public static IServiceCollection AddCabinetDatabase(this IServiceCollection services)
    {
        // Register configuration
        services.AddOptions<DatabaseConfiguration>()
            .Configure<IConfiguration>((config, configuration) =>
            {
                configuration.GetSection("Cabinet:Database").Bind(config);
            });

        // Register factory
        services.AddSingleton<IDatabaseProviderFactory, DatabaseProviderFactory>();

        // Register database provider as singleton
        services.AddSingleton<ILogDatabaseProvider>(serviceProvider =>
        {
            var factory = serviceProvider.GetRequiredService<IDatabaseProviderFactory>();
            var config = serviceProvider.GetRequiredService<IConfiguration>();

            var dbConfig = new DatabaseConfiguration();
            config.GetSection("Cabinet:Database").Bind(dbConfig);

            var provider = factory.CreateProvider(dbConfig);

            // Initialize synchronously (not ideal, but needed for DI)
            provider.InitializeAsync().GetAwaiter().GetResult();

            return provider;
        });

        // Register services
        services.AddSingleton<IBackupService, BackupService>();
        services.AddHostedService<RetentionPolicyService>();

        return services;
    }

    /// <summary>
    /// Add a specific database provider
    /// </summary>
    public static IServiceCollection AddDatabaseProvider<TProvider>(
        this IServiceCollection services,
        DatabaseConfiguration config)
        where TProvider : class, ILogDatabaseProvider
    {
        services.AddSingleton<ILogDatabaseProvider>(serviceProvider =>
        {
            var provider = ActivatorUtilities.CreateInstance<TProvider>(serviceProvider, config);
            provider.InitializeAsync().GetAwaiter().GetResult();
            return provider;
        });

        return services;
    }
}