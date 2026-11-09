using Cabinet.API.Infrastructure;
using Cabinet.Core.Configuration;
using Cabinet.Core.Interfaces;
using Cabinet.Storage;
using Microsoft.Extensions.Options;

namespace Cabinet.API.Services.Storage;

/// <summary>
/// Opens a storage backend from a type and connection string, the same way the server does, so
/// a migration reads and writes exactly what the server would.
/// </summary>
public static class StorageBackends
{
    /// <summary>Backends events can be moved between.</summary>
    public static readonly IReadOnlyList<string> Movable = ["SQLite", "PostgreSQL"];

    /// <summary>The canonical name for a storage type, or null if it isn't one.</summary>
    public static string? Normalize(string? type) => (type ?? string.Empty).ToUpperInvariant() switch
    {
        "SQLITE" or "" => "SQLite",
        "POSTGRESQL" or "POSTGRES" => "PostgreSQL",
        "OPENSEARCH" or "ELASTICSEARCH" => "OpenSearch",
        "MEMORY" => "Memory",
        _ => null,
    };

    /// <summary>The server's configuration with the storage replaced.</summary>
    public static IConfiguration With(IConfiguration configuration, string type, string connectionString) =>
        new ConfigurationBuilder()
            .AddConfiguration(configuration)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Type"] = type,
                ["Storage:ConnectionString"] = connectionString,
            })
            .Build();

    /// <summary>The events backend alone, without encryption or detection in front of it.</summary>
    public static ILogStorage CreateLogStorage(IConfiguration configuration, ILoggerFactory loggers)
    {
        var connectionString = configuration["Storage:ConnectionString"] ?? "Data Source=cabinet.db";
        return Normalize(configuration["Storage:Type"]) switch
        {
            "PostgreSQL" => new Cabinet.Database.Providers.PostgresLogStorage(connectionString, loggers.CreateLogger<Cabinet.Database.Providers.PostgresLogStorage>()),
            "OpenSearch" => new Cabinet.Database.Providers.OpenSearchLogStorage(
                connectionString,
                loggers.CreateLogger<Cabinet.Database.Providers.OpenSearchLogStorage>(),
                indexPrefix: configuration["Storage:IndexPrefix"] ?? "cabinet-logs",
                username: configuration["Storage:Username"],
                password: configuration["Storage:Password"],
                allowInsecureSsl: configuration.GetValue<bool>("Storage:AllowInsecureSsl")),
            "Memory" => new InMemoryStorage(),
            _ => new SqliteLogStorage(Options.Create(Bind(configuration)), loggers.CreateLogger<SqliteLogStorage>()),
        };
    }

    /// <summary>The metadata stores for a configuration: documents, dashboards, knowledge, schedules, saved queries.</summary>
    public static ServiceProvider CreateMetadata(IConfiguration configuration, ILoggerFactory loggers)
    {
        var services = new ServiceCollection();
        services.AddSingleton(loggers);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<IOptions<CabinetConfiguration>>(Options.Create(Bind(configuration)));
        services.AddCabinetMetadataStorage(configuration);
        return services.BuildServiceProvider();
    }

    private static CabinetConfiguration Bind(IConfiguration configuration)
    {
        var options = new CabinetConfiguration();
        configuration.Bind(options);
        options.Storage.ConnectionString = configuration["Storage:ConnectionString"] ?? options.Storage.ConnectionString;
        return options;
    }

    /// <summary>
    /// Where a backend keeps its data, safe to show: a SQLite file's full path, or a server and
    /// database. Never a password.
    /// </summary>
    public static string Describe(string type, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return type;
        }

        switch (Normalize(type))
        {
            case "SQLite":
                return Settings.StorageInfoService.SqliteFile(connectionString) is { } file ? Path.GetFullPath(file) : "SQLite";
            case "PostgreSQL":
                try
                {
                    var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
                    return $"PostgreSQL {builder.Host}:{builder.Port}, database {builder.Database}";
                }
                catch (ArgumentException)
                {
                    return "PostgreSQL";
                }

            case "OpenSearch":
                return Uri.TryCreate(connectionString, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Host}:{uri.Port}" : "OpenSearch";
            default:
                return type;
        }
    }
}

/// <summary>
/// Holds back ingest while storage is being switched, so nothing is written to a database that is
/// about to be left behind. Senders get 503 with Retry-After and resend; the agent keeps events on
/// disk meanwhile.
/// </summary>
public sealed class IngestGate
{
    private volatile string? _reason;

    /// <summary>Why ingest is paused, or null if it isn't.</summary>
    public string? PausedBecause => _reason;

    public void Pause(string reason) => _reason = reason;

    public void Resume() => _reason = null;

    /// <summary>Requests that store events, by every route.</summary>
    public static bool IsIngest(HttpRequest request) =>
        HttpMethods.IsPost(request.Method)
        && (request.Path.StartsWithSegments("/services/collector")
            || request.Path.StartsWithSegments("/api/v1/logs/ingest")
            || request.Path.StartsWithSegments("/v1/logs")
            || request.Path.StartsWithSegments("/v1/traces")
            || request.Path.StartsWithSegments("/v1/metrics"));
}
