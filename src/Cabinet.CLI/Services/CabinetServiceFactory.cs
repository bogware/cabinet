using Cabinet.CLI.Configuration;
using Cabinet.Core.Interfaces;
using Cabinet.Detection;
using Cabinet.Parsers;
using Cabinet.Query;
using Cabinet.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cabinet.CLI.Services;

/// <summary>
/// Factory for creating Cabinet service based on configuration
/// </summary>
public static class CabinetServiceFactory
{
    public static ICabinetService Create(CabinetConfig config)
    {
        if (config.Mode.ToLower() == "remote")
        {
            if (config.Remote == null)
            {
                throw new InvalidOperationException("Remote configuration is missing");
            }

            if (string.IsNullOrEmpty(config.Remote.ApiKey))
            {
                throw new InvalidOperationException("API key is required for remote mode");
            }

            var apiClient = new CabinetApiClient(config.Remote.ApiUrl, config.Remote.ApiKey);
            return new RemoteCabinetService(apiClient);
        }
        else
        {
            // Local mode - create all services
            var services = new ServiceCollection();

            // Configure logging
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            });

            // Configure storage
            services.AddSingleton<ILogStorage>(sp =>
            {
                // Create a simple configuration for local use
                var cabinetConfig = new Cabinet.Core.Configuration.CabinetConfiguration
                {
                    Storage = new Cabinet.Core.Configuration.StorageConfiguration
                    {
                        ConnectionString = config.Local.ConnectionString
                    }
                };
                var optionsWrapper = Microsoft.Extensions.Options.Options.Create(cabinetConfig);
                var logger = sp.GetRequiredService<ILogger<SqliteLogStorage>>();

                return config.Local.StorageType.ToLower() switch
                {
                    "sqlite" => new SqliteLogStorage(optionsWrapper, logger),
                    "memory" => new InMemoryStorage(),
                    _ => new SqliteLogStorage(optionsWrapper, logger)
                };
            });

            // Configure parsers
            services.AddSingleton<ILogParserProvider, LogParserProvider>();
            services.AddTransient<ILogParser, GenericParser>();
            services.AddTransient<ILogParser, L4D2Parser>();
            services.AddTransient<ILogParser, SourceModParser>();
            services.AddTransient<ILogParser, SteamCMDParser>();

            // Configure detection
            services.AddScoped<IScumscanDetector, ScumscanDetector>();
            services.AddScoped<IHackwatchDetector, HackwatchDetector>();
            services.AddScoped<IAdminabuseDetector, AdminabuseDetector>();

            // Configure query engine
            services.AddScoped<IQueryEngine, QueryEngine>();

            var serviceProvider = services.BuildServiceProvider();

            return new LocalCabinetService(
                serviceProvider.GetRequiredService<ILogStorage>(),
                serviceProvider.GetRequiredService<IQueryEngine>(),
                serviceProvider.GetRequiredService<IScumscanDetector>(),
                serviceProvider.GetRequiredService<IHackwatchDetector>(),
                serviceProvider.GetRequiredService<IAdminabuseDetector>()
            );
        }
    }

    public static async Task<ICabinetService> CreateAndInitializeAsync(CabinetConfig config)
    {
        var service = Create(config);
        await service.InitializeAsync();
        return service;
    }
}