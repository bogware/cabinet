using Cabinet.Core.Interfaces;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Cabinet.Database.Providers;

/// <summary>Keeps cluster state in PostgreSQL, which every instance shares.</summary>
public sealed class PostgresClusterCoordinator : IClusterCoordinator, IDisposable
{
    private static readonly TimeSpan InstanceTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MatchRetention = TimeSpan.FromDays(1);

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresClusterCoordinator> _logger;
    private readonly string _instanceId = $"{Environment.MachineName}-{Guid.NewGuid():N}";
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;
    private volatile int _liveInstances = 1;
    private DateTime _lastCleanup = DateTime.MinValue;

    public PostgresClusterCoordinator(string connectionString, ILogger<PostgresClusterCoordinator> logger)
    {
        _dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        _logger = logger;
    }

    /// <inheritdoc />
    public int LiveInstances => _liveInstances;

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var schemaLock = await PostgresSchemaLock.BeginAsync(connection, cancellationToken);
            await connection.ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS cabinet_cluster_instances (
                    id TEXT PRIMARY KEY,
                    last_seen TIMESTAMPTZ NOT NULL
                );
                CREATE TABLE IF NOT EXISTS cabinet_detection_matches (
                    rule_id TEXT NOT NULL,
                    group_key TEXT NOT NULL,
                    event_id TEXT NOT NULL,
                    event_time TIMESTAMPTZ NOT NULL,
                    PRIMARY KEY (rule_id, group_key, event_id)
                );
                CREATE INDEX IF NOT EXISTS idx_detection_matches_window ON cabinet_detection_matches (rule_id, group_key, event_time);
                CREATE TABLE IF NOT EXISTS cabinet_detection_fired (
                    rule_id TEXT NOT NULL,
                    group_key TEXT NOT NULL,
                    fired_at TIMESTAMPTZ NOT NULL,
                    PRIMARY KEY (rule_id, group_key)
                );
                """);
            await schemaLock.CommitAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<int> HeartbeatAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var live = await connection.ExecuteScalarAsync<int>(
            """
            INSERT INTO cabinet_cluster_instances (id, last_seen) VALUES (@Id, now())
            ON CONFLICT (id) DO UPDATE SET last_seen = now();
            DELETE FROM cabinet_cluster_instances WHERE last_seen < now() - @Timeout * interval '1 second' * 10;
            SELECT count(*)::int FROM cabinet_cluster_instances WHERE last_seen > now() - @Timeout * interval '1 second';
            """,
            new { Id = _instanceId, Timeout = InstanceTimeout.TotalSeconds });
        _liveInstances = Math.Max(1, live);

        // Matches older than any window are no use. Removing them isn't urgent.
        if (DateTime.UtcNow - _lastCleanup > TimeSpan.FromMinutes(10))
        {
            _lastCleanup = DateTime.UtcNow;
            var removed = await connection.ExecuteAsync(
                "DELETE FROM cabinet_detection_matches WHERE event_time < now() - @Retention * interval '1 second'",
                new { Retention = MatchRetention.TotalSeconds });
            _logger.LogDebug("Removed {Count} old detection matches", removed);
        }

        return _liveInstances;
    }

    /// <inheritdoc />
    public async Task<long> RecordMatchAsync(string ruleId, string group, string eventId, DateTime eventTime, DateTime windowStart, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO cabinet_detection_matches (rule_id, group_key, event_id, event_time) VALUES (@Rule, @Group, @Event, @Time)
            ON CONFLICT DO NOTHING;
            SELECT count(*) FROM cabinet_detection_matches WHERE rule_id = @Rule AND group_key = @Group AND event_time >= @WindowStart;
            """,
            new
            {
                Rule = ruleId,
                Group = group,
                Event = eventId,
                Time = DateTime.SpecifyKind(eventTime, DateTimeKind.Utc),
                WindowStart = DateTime.SpecifyKind(windowStart, DateTimeKind.Utc),
            });
    }

    /// <inheritdoc />
    public async Task<bool> TryFireAsync(string ruleId, string group, TimeSpan cooldown, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // One statement, so two instances can't both see the cooldown as over
        var claimed = await connection.ExecuteScalarAsync<int?>(
            """
            INSERT INTO cabinet_detection_fired (rule_id, group_key, fired_at) VALUES (@Rule, @Group, now())
            ON CONFLICT (rule_id, group_key) DO UPDATE SET fired_at = now()
            WHERE cabinet_detection_fired.fired_at <= now() - @Cooldown * interval '1 second'
            RETURNING 1
            """,
            new { Rule = ruleId, Group = group, Cooldown = cooldown.TotalSeconds },
            transaction);

        if (claimed == 1)
        {
            await connection.ExecuteAsync(
                "DELETE FROM cabinet_detection_matches WHERE rule_id = @Rule AND group_key = @Group",
                new { Rule = ruleId, Group = group },
                transaction);
        }

        await transaction.CommitAsync(cancellationToken);
        return claimed == 1;
    }

    public void Dispose()
    {
        _dataSource.Dispose();
        _initLock.Dispose();
    }
}
