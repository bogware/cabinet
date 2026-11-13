using System.Diagnostics;
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
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);

    private readonly string _connectionString;
    private readonly ILogger<PostgresLeaderLease> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _loop;
    private volatile bool _isLeader;
    private long _confirmedAt;
    private int _disposed;

    /// <summary>
    /// How long a confirmation of the lock counts. If the loop stalls (a starved thread pool, a
    /// hung socket), this instance stops counting itself the leader within this time, rather
    /// than carrying on beside whoever takes the lock next.
    /// </summary>
    public static readonly TimeSpan ConfirmationLasts = PollInterval * 2 + CheckTimeout;

    public PostgresLeaderLease(string connectionString, ILogger<PostgresLeaderLease> logger)
    {
        // A pooled connection could be reset, which would silently release the lock
        // A lost connection must be noticed in seconds, since another instance may take the lock then
        _connectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            Timeout = (int)CheckTimeout.TotalSeconds,
            CommandTimeout = (int)CheckTimeout.TotalSeconds,
            KeepAlive = (int)PollInterval.TotalSeconds,
        }.ConnectionString;
        _logger = logger;
        _loop = Task.Run(() => RunAsync(_stopping.Token));
    }

    /// <inheritdoc />
    public bool IsLeader => _isLeader && Stopwatch.GetElapsedTime(Interlocked.Read(ref _confirmedAt)) < ConfirmationLasts;

    private void Confirmed() => Interlocked.Exchange(ref _confirmedAt, Stopwatch.GetTimestamp());

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
                        var acquired = await connection.ExecuteScalarAsync<bool>(
                            new CommandDefinition("SELECT pg_try_advisory_lock(@Key)", new { Key = LockKey }, commandTimeout: (int)CheckTimeout.TotalSeconds, cancellationToken: cancellationToken));
                        if (acquired)
                        {
                            Confirmed();
                            _isLeader = true;
                            _logger.LogInformation("This instance is now the leader, holding the lock on database connection {ProcessId}", connection.ProcessID);
                        }
                    }
                    else
                    {
                        // Asks whether this session still holds the lock, not just whether a connection
                        // answers: a lost connection fails, and a session without the lock steps down
                        var held = await connection.ExecuteScalarAsync<bool>(
                            new CommandDefinition(
                                "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND granted AND pid = pg_backend_pid() AND objid = (@Key & 4294967295)::oid AND classid = (@Key >> 32)::oid)",
                                new { Key = LockKey },
                                commandTimeout: (int)CheckTimeout.TotalSeconds,
                                cancellationToken: cancellationToken));
                        if (!held)
                        {
                            throw new InvalidOperationException("This session no longer holds the leader lock");
                        }

                        Confirmed();
                    }

                    await Task.Delay(PollInterval, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Leader lease stopping");
            }
            catch (Exception ex)
            {
                // Anything else would end the loop, and this instance could never lead again
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
