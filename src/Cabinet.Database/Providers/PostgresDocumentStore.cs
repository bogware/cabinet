using System.Text.Json;
using Cabinet.Core.Interfaces;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Cabinet.Database.Providers;

/// <summary>Keeps documents in PostgreSQL, where every API instance sees them.</summary>
public sealed class PostgresDocumentStore : IDocumentStore, IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresDocumentStore> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    public PostgresDocumentStore(string connectionString, ILogger<PostgresDocumentStore> logger)
    {
        _dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
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
                CREATE TABLE IF NOT EXISTS cabinet_documents (
                    collection TEXT NOT NULL,
                    id TEXT NOT NULL,
                    body TEXT NOT NULL,
                    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    PRIMARY KEY (collection, id)
                )
                """);
            await schemaLock.CommitAsync(cancellationToken);
            _initialized = true;
            _logger.LogDebug("PostgreSQL document store initialized");
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task PutAsync<T>(string collection, string id, T document, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO cabinet_documents (collection, id, body)
            VALUES (@Collection, @Id, @Body)
            ON CONFLICT (collection, id) DO UPDATE SET body = @Body, updated_at = NOW()
            """,
            new { Collection = collection, Id = id, Body = JsonSerializer.Serialize(document).Replace("\\u0000", string.Empty, StringComparison.Ordinal) });
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string collection, string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var body = await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT body FROM cabinet_documents WHERE collection = @Collection AND id = @Id",
            new { Collection = collection, Id = id });
        return body == null ? default : JsonSerializer.Deserialize<T>(body);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string collection, string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(
            "DELETE FROM cabinet_documents WHERE collection = @Collection AND id = @Id",
            new { Collection = collection, Id = id }) > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<T>> ListAsync<T>(string collection, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var bodies = await connection.QueryAsync<string>(
            "SELECT body FROM cabinet_documents WHERE collection = @Collection ORDER BY id",
            new { Collection = collection });
        return bodies.Select(body => JsonSerializer.Deserialize<T>(body)!).ToList();
    }

    public void Dispose()
    {
        _dataSource.Dispose();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListCollectionsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<string>("SELECT DISTINCT collection FROM cabinet_documents ORDER BY collection")).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KeyValuePair<string, string>>> ExportAsync(string collection, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<(string Id, string Body)>(
            "SELECT id AS Id, body AS Body FROM cabinet_documents WHERE collection = @Collection ORDER BY id",
            new { Collection = collection });
        return rows.Select(row => new KeyValuePair<string, string>(row.Id, row.Body)).ToList();
    }

    /// <inheritdoc />
    public async Task ImportAsync(string collection, string id, string json, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO cabinet_documents (collection, id, body)
            VALUES (@Collection, @Id, @Body)
            ON CONFLICT (collection, id) DO UPDATE SET body = @Body, updated_at = NOW()
            """,
            new { Collection = collection, Id = id, Body = json });
    }
}

/// <summary>
/// Makes one instance the leader using a PostgreSQL advisory lock. The lock belongs to a database
/// connection, so if the leader dies or loses its connection the server releases it and another
/// instance takes over within one polling interval.
/// </summary>
public sealed class PostgresLeaderLease : ILeaderLease, IAsyncDisposable, IDisposable
{
    // An arbitrary number, unique among the application's advisory locks
    private const long LockKey = 7_236_151_002;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly string _connectionString;
    private readonly ILogger<PostgresLeaderLease> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _loop;
    private volatile bool _isLeader;
    private int _disposed;

    public PostgresLeaderLease(string connectionString, ILogger<PostgresLeaderLease> logger)
    {
        // A pooled connection could be reset, which would silently release the lock
        _connectionString = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
        _logger = logger;
        _loop = Task.Run(() => RunAsync(_stopping.Token));
    }

    /// <inheritdoc />
    public bool IsLeader => _isLeader;

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!_isLeader)
                    {
                        _isLeader = await connection.ExecuteScalarAsync<bool>("SELECT pg_try_advisory_lock(@Key)", new { Key = LockKey });
                        if (_isLeader)
                        {
                            _logger.LogInformation("This instance is now the leader");
                        }
                    }
                    else
                    {
                        // Notices a lost connection, which means the lock is gone too
                        await connection.ExecuteScalarAsync<int>("SELECT 1");
                    }

                    await Task.Delay(PollInterval, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Leader lease stopping");
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException or InvalidOperationException)
            {
                if (_isLeader)
                {
                    _logger.LogWarning(ex, "Lost the leader lease");
                }
                else
                {
                    _logger.LogDebug(ex, "Couldn't reach the database for the leader lease");
                }

                _isLeader = false;
                await DelayQuietlyAsync(cancellationToken);
            }
            finally
            {
                _isLeader = false;
            }
        }
    }

    private static async Task DelayQuietlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(PollInterval, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Stopping, which the caller's loop checks next
            return;
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stopping.CancelAsync();
        await _loop;
        _stopping.Dispose();
    }
}
