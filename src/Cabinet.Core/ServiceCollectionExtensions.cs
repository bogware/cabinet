using Cabinet.Core.Configuration;
using Cabinet.Core.Ingestion;
using Cabinet.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cabinet.Core
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddCabinetCore(this IServiceCollection services)
        {
            // Register core services
            services.AddSingleton<IIngestionEngine, IngestionEngine>();

            // Register configuration
            services.ConfigureOptions<CabinetConfigureOptions>();

            return services;
        }
    }

    public class CabinetConfigureOptions : IConfigureOptions<CabinetConfiguration>
    {
        public void Configure(CabinetConfiguration options)
        {
            // Set defaults if not configured
            options.Storage ??= new StorageConfiguration();
            options.Ingestion ??= new IngestionConfiguration();
            options.Output ??= new OutputConfiguration();
        }
    }
}

