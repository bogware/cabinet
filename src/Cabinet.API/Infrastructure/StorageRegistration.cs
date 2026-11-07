using Cabinet.Core.Configuration;
using Cabinet.Core.Interfaces;
using Cabinet.Database.Providers;
using Cabinet.Storage;
using Microsoft.Extensions.Options;

namespace Cabinet.API.Infrastructure;

/// <summary>
/// Storage backends selectable through <c>Storage:Type</c>.
/// </summary>
public enum StorageBackend
{
    Sqlite,
    Memory,
    Postgres,
    OpenSearch,
}

/// <summary>
/// Registers the stores for everything other than log events: dashboards, knowledge
/// objects, saved queries, schedules, search jobs and data models.
/// </summary>
public static class StorageRegistration
{
    /// <summary>
    /// Holds metadata that has no store on the primary backend. It stays local to the
    /// instance, so deployments with several replicas must not rely on it being shared.
    /// </summary>
    public const string DefaultMetadataConnectionString = "Data Source=cabinet-metadata.db";

    /// <summary>
    /// Maps a <c>Storage:Type</c> value to its backend. Unknown values fall back to SQLite;
    /// startup validation rejects them before this is reached.
    /// </summary>
    public static StorageBackend ResolveBackend(IConfiguration configuration) =>
        (configuration["Storage:Type"] ?? "SQLite").ToUpperInvariant() switch
        {
            "MEMORY" => StorageBackend.Memory,
            "POSTGRESQL" or "POSTGRES" => StorageBackend.Postgres,
            "OPENSEARCH" or "ELASTICSEARCH" => StorageBackend.OpenSearch,
            _ => StorageBackend.Sqlite,
        };

    /// <summary>
    /// Where shared state lives. OpenSearch has no store for it, so it stays on a local SQLite
    /// database unless <c>Storage:MetadataPostgresConnectionString</c> names a shared one.
    /// </summary>
    public static StorageBackend ResolveMetadataBackend(IConfiguration configuration)
    {
        var backend = ResolveBackend(configuration);
        return backend == StorageBackend.OpenSearch && !string.IsNullOrEmpty(configuration["Storage:MetadataPostgresConnectionString"])
            ? StorageBackend.Postgres
            : backend;
    }

    private static string? ResolveMetadataPostgres(IConfiguration configuration) =>
        ResolveBackend(configuration) == StorageBackend.OpenSearch
            ? configuration["Storage:MetadataPostgresConnectionString"]
            : configuration["Storage:ConnectionString"];

    /// <summary>
    /// Registers each metadata store on the configured backend, using the local SQLite
    /// metadata database where the backend has no implementation of that store.
    /// </summary>
    public static IServiceCollection AddCabinetMetadataStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var backend = ResolveMetadataBackend(configuration);
        var connectionString = ResolveMetadataPostgres(configuration) ?? string.Empty;

        services.AddSingleton<IDocumentStore>(sp => backend switch
        {
            StorageBackend.Memory => new InMemoryDocumentStore(),
            StorageBackend.Postgres => new PostgresDocumentStore(connectionString, Logger<PostgresDocumentStore>(sp)),
            _ => new SqliteDocumentStore(SqliteOptions(sp, configuration, backend)),
        });

        // Only a shared database can have more than one instance to choose from
        services.AddSingleton<ILeaderLease>(sp => backend == StorageBackend.Postgres
            ? new PostgresLeaderLease(connectionString, Logger<PostgresLeaderLease>(sp))
            : new SingleInstanceLease());

        services.AddSingleton<IDashboardStorage>(sp => backend switch
        {
            StorageBackend.Memory => new InMemoryDashboardStorage(),
            StorageBackend.Postgres => new PostgresDashboardStorage(connectionString, Logger<PostgresDashboardStorage>(sp)),
            _ => new SqliteDashboardStorage(SqliteOptions(sp, configuration, backend), Logger<SqliteDashboardStorage>(sp)),
        });

        services.AddSingleton<IKnowledgeStorage>(sp => backend switch
        {
            StorageBackend.Memory => new InMemoryKnowledgeStorage(),
            StorageBackend.Postgres => new PostgresKnowledgeStorage(connectionString, Logger<PostgresKnowledgeStorage>(sp)),
            _ => new SqliteKnowledgeStorage(SqliteOptions(sp, configuration, backend), Logger<SqliteKnowledgeStorage>(sp)),
        });

        services.AddSingleton<IScheduledSearchStorage>(sp => backend switch
        {
            StorageBackend.Memory => new InMemoryScheduledSearchStorage(),
            StorageBackend.Postgres => new PostgresScheduledSearchStorage(connectionString, Logger<PostgresScheduledSearchStorage>(sp)),
            _ => new SqliteScheduledSearchStorage(SqliteOptions(sp, configuration, backend), Logger<SqliteScheduledSearchStorage>(sp)),
        });

        services.AddSingleton<ISavedQueryStorage>(sp => backend switch
        {
            StorageBackend.Memory => new InMemorySavedQueryStorage(),
            StorageBackend.Postgres => new PostgresSavedQueryStorage(connectionString, Logger<PostgresSavedQueryStorage>(sp)),
            _ => new SqliteSavedQueryStorage(SqliteOptions(sp, configuration, backend), Logger<SqliteSavedQueryStorage>(sp)),
        });

        services.AddSingleton<ISearchJobStorage>(sp => new DocumentSearchJobStorage(sp.GetRequiredService<IDocumentStore>()));
        services.AddSingleton<IDataModelStorage>(sp => new DocumentDataModelStorage(sp.GetRequiredService<IDocumentStore>()));
        services.AddSingleton<IAlertHistoryStorage>(sp => new DocumentAlertHistoryStorage(sp.GetRequiredService<IDocumentStore>()));
        services.AddSingleton<IFieldExtractionStorage>(sp => new DocumentFieldExtractionStorage(sp.GetRequiredService<IDocumentStore>()));

        return services;
    }

    private static ILogger<T> Logger<T>(IServiceProvider services) =>
        services.GetRequiredService<ILogger<T>>();

    /// <summary>
    /// SQLite stores read <c>Storage:ConnectionString</c>. That points at the primary backend
    /// when it isn't SQLite, so give them the metadata database instead.
    /// </summary>
    private static IOptions<CabinetConfiguration> SqliteOptions(
        IServiceProvider services,
        IConfiguration configuration,
        StorageBackend backend)
    {
        if (backend == StorageBackend.Sqlite)
        {
            return services.GetRequiredService<IOptions<CabinetConfiguration>>();
        }

        var options = new CabinetConfiguration();
        configuration.Bind(options);
        options.Storage.ConnectionString =
            configuration["Storage:MetadataConnectionString"] ?? DefaultMetadataConnectionString;
        return Options.Create(options);
    }
}
