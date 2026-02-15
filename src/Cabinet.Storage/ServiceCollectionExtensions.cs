using Cabinet.Core.Configuration;
using Cabinet.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cabinet.Storage;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCabinetStorage(this IServiceCollection services)
    {
        services.AddSingleton<ILogStorage, SqliteLogStorage>();
        services.AddSingleton<ICheckpointManager>(provider =>
        {
            var config = provider.GetRequiredService<IOptions<CabinetConfiguration>>();
            var logger = provider.GetRequiredService<ILogger<SqliteCheckpointManager>>();
            return new SqliteCheckpointManager(
                config.Value.Storage.ConnectionString.Replace("cabinet.db", "checkpoints.db"),
                logger);
        });
        return services;
    }
}