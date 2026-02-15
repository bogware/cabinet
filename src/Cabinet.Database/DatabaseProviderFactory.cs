#pragma warning disable CS0612, CS0618 // Type or member is obsolete
using Cabinet.Database.Abstractions;
using Cabinet.Database.Configuration;
using Cabinet.Database.Providers;
using Microsoft.Extensions.Logging;

namespace Cabinet.Database;

/// <summary>
/// Factory for creating database providers based on configuration
/// </summary>
public class DatabaseProviderFactory : IDatabaseProviderFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public DatabaseProviderFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public ILogDatabaseProvider CreateProvider(DatabaseConfiguration config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        var providerType = config.Provider?.ToUpperInvariant() ?? "SQLITE";

        return providerType switch
        {
            "POSTGRESQL" or "POSTGRES" => new PostgreSQLProvider(
                config,
                _loggerFactory.CreateLogger<PostgreSQLProvider>()),

            "MYSQL" or "MARIADB" => new MySQLProvider(
                config,
                _loggerFactory.CreateLogger<MySQLProvider>()),

            "DYNAMODB" or "DYNAMO" => new DynamoDBProvider(
                config,
                _loggerFactory.CreateLogger<DynamoDBProvider>()),

            "SQLITE" => throw new NotImplementedException("SQLite provider will use existing SqliteLogStorage"),

            _ => throw new NotSupportedException($"Database provider '{config.Provider}' is not supported")
        };
    }

    public async Task<ILogDatabaseProvider> CreateAndInitializeAsync(DatabaseConfiguration config, CancellationToken cancellationToken = default)
    {
        var provider = CreateProvider(config);
        await provider.InitializeAsync(cancellationToken);
        return provider;
    }
}

/// <summary>
/// Factory interface for creating database providers
/// </summary>
public interface IDatabaseProviderFactory
{
    /// <summary>
    /// Create a database provider based on configuration
    /// </summary>
    ILogDatabaseProvider CreateProvider(DatabaseConfiguration config);

    /// <summary>
    /// Create and initialize a database provider
    /// </summary>
    Task<ILogDatabaseProvider> CreateAndInitializeAsync(DatabaseConfiguration config, CancellationToken cancellationToken = default);
}