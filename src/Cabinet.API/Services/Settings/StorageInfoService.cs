using Cabinet.Core.Interfaces;

namespace Cabinet.API.Services.Settings;

/// <summary>Where events are kept and how much room is left, for the settings screen.</summary>
public sealed class StorageInfo
{
    /// <summary>SQLite, PostgreSQL, OpenSearch, Cabinet or Memory.</summary>
    public required string Type { get; init; }

    /// <summary>The database file, or the host and database. Never credentials.</summary>
    public string? Location { get; init; }

    /// <summary>Size of the database, where it can be measured.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>Events stored, in the indexes the caller can read.</summary>
    public long EventsStored { get; init; }

    public DateTime? OldestEvent { get; init; }

    public DateTime? NewestEvent { get; init; }

    /// <summary>Free space on the drive the database is on, for a file database.</summary>
    public long? DiskFreeBytes { get; init; }

    public long? DiskTotalBytes { get; init; }

    /// <summary>Days events are kept, 0 for always.</summary>
    public int RetentionDays { get; init; }

    /// <summary>Where saved searches, rules and other settings are kept.</summary>
    public string? MetadataLocation { get; init; }
}

/// <summary>Works out <see cref="StorageInfo"/> from configuration and the storage itself.</summary>
public static class StorageInfoService
{
    /// <summary>Describes storage as the caller sees it.</summary>
    public static async Task<StorageInfo> DescribeAsync(IConfiguration configuration, ILogStorage storage, CancellationToken cancellationToken = default)
    {
        var type = Normalize(configuration["Storage:Type"]);
        var connection = configuration["Storage:ConnectionString"];
        var stats = await storage.GetStatsAsync(cancellationToken);

        string? location = null;
        long? size = null;
        long? free = null;
        long? total = null;
        switch (type)
        {
            case "SQLite":
                location = SqliteFile(connection ?? "Data Source=cabinet.db");
                if (location != null)
                {
                    size = new[] { location, location + "-wal", location + "-shm" }.Where(File.Exists).Sum(file => new FileInfo(file).Length);
                    (free, total) = Disk(location);
                }

                break;
            case "PostgreSQL":
                location = PostgresTarget(connection);
                size = stats.StorageSizeBytes > 0 ? stats.StorageSizeBytes : null;
                break;
            case "Cabinet":
                location = Cabinet.API.Services.Storage.StorageBackends.EngineDataPath(configuration);
                size = stats.StorageSizeBytes;
                (free, total) = Disk(location);
                break;
            case "OpenSearch" or "ClickHouse":
                location = UrlHost(connection);
                size = stats.StorageSizeBytes > 0 ? stats.StorageSizeBytes : null;
                break;
            default:
                location = "Memory (nothing is kept across restarts)";
                break;
        }

        return new StorageInfo
        {
            Type = type,
            Location = location,
            SizeBytes = size,
            EventsStored = stats.TotalEvents,
            OldestEvent = stats.OldestEvent,
            NewestEvent = stats.NewestEvent,
            DiskFreeBytes = free,
            DiskTotalBytes = total,
            RetentionDays = configuration.GetValue("Storage:RetentionDays", 0),
            MetadataLocation = MetadataLocation(configuration, type, location),
        };
    }

    private static string Normalize(string? type) => (type ?? "SQLite").ToUpperInvariant() switch
    {
        "POSTGRESQL" or "POSTGRES" => "PostgreSQL",
        "OPENSEARCH" or "ELASTICSEARCH" => "OpenSearch",
        "MEMORY" or "INMEMORY" => "Memory",
        "CABINET" or "ENGINE" => "Cabinet",
        "CLICKHOUSE" => "ClickHouse",
        _ => "SQLite",
    };

    private static string? MetadataLocation(IConfiguration configuration, string type, string? location)
    {
        if (type is not ("OpenSearch" or "Cabinet" or "ClickHouse"))
        {
            return location == null ? null : $"The same database ({location})";
        }

        if (PostgresTarget(configuration["Storage:MetadataPostgresConnectionString"]) is { } postgres)
        {
            return postgres;
        }

        return SqliteFile(configuration["Storage:MetadataConnectionString"] ?? "Data Source=cabinet-meta.db");
    }

    /// <summary>The full path of a SQLite connection string's file.</summary>
    public static string? SqliteFile(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        try
        {
            var source = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
            return string.IsNullOrEmpty(source) || source == ":memory:" ? null : Path.GetFullPath(source);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? PostgresTarget(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        try
        {
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
            return $"PostgreSQL {builder.Host}:{builder.Port}, database {builder.Database}";
        }
        catch (ArgumentException)
        {
            return "PostgreSQL";
        }
    }

    private static string? UrlHost(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Host}:{uri.Port}" : null;

    private static (long? Free, long? Total) Disk(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
            {
                return (null, null);
            }

            var drive = new DriveInfo(root);
            return drive.IsReady ? (drive.AvailableFreeSpace, drive.TotalSize) : (null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null);
        }
    }
}
