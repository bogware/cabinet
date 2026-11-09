using System.Collections.Concurrent;
using System.Text.Json;
using Cabinet.Core.Configuration;
using Cabinet.Core.Interfaces;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Cabinet.Storage;

/// <summary>Keeps documents in memory, as JSON, so readers can't change a stored copy.</summary>
public sealed class InMemoryDocumentStore : IDocumentStore
{
    private readonly ConcurrentDictionary<(string Collection, string Id), string> _documents = new();

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task PutAsync<T>(string collection, string id, T document, CancellationToken cancellationToken = default)
    {
        _documents[(collection, id)] = JsonSerializer.Serialize(document);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string collection, string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_documents.TryGetValue((collection, id), out var json) ? JsonSerializer.Deserialize<T>(json) : default);

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string collection, string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_documents.TryRemove((collection, id), out _));

    /// <inheritdoc />
    public Task<IReadOnlyList<T>> ListAsync<T>(string collection, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<T> documents = _documents
            .Where(document => document.Key.Collection == collection)
            .OrderBy(document => document.Key.Id, StringComparer.Ordinal)
            .Select(document => JsonSerializer.Deserialize<T>(document.Value)!)
            .ToList();
        return Task.FromResult(documents);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListCollectionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(_documents.Keys.Select(key => key.Collection).Distinct().Order(StringComparer.Ordinal).ToList());

    /// <inheritdoc />
    public Task<IReadOnlyList<KeyValuePair<string, string>>> ExportAsync(string collection, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<KeyValuePair<string, string>>>(_documents
            .Where(document => document.Key.Collection == collection)
            .OrderBy(document => document.Key.Id, StringComparer.Ordinal)
            .Select(document => new KeyValuePair<string, string>(document.Key.Id, document.Value))
            .ToList());

    /// <inheritdoc />
    public Task ImportAsync(string collection, string id, string json, CancellationToken cancellationToken = default)
    {
        _documents[(collection, id)] = json;
        return Task.CompletedTask;
    }
}

/// <summary>Keeps documents in the instance's SQLite database.</summary>
public sealed class SqliteDocumentStore : IDocumentStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    public SqliteDocumentStore(IOptions<CabinetConfiguration> configuration)
    {
        _connectionString = configuration.Value.Storage.ConnectionString ?? "Data Source=cabinet.db";
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

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await connection.ExecuteAsync("""
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS cabinet_documents (
                    collection TEXT NOT NULL,
                    id TEXT NOT NULL,
                    body TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    PRIMARY KEY (collection, id)
                );
                """);
            _initialized = true;
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
        await using var connection = new SqliteConnection(_connectionString);
        await connection.ExecuteAsync(
            """
            INSERT INTO cabinet_documents (collection, id, body, updated_at)
            VALUES (@Collection, @Id, @Body, @UpdatedAt)
            ON CONFLICT (collection, id) DO UPDATE SET body = @Body, updated_at = @UpdatedAt
            """,
            new { Collection = collection, Id = id, Body = JsonSerializer.Serialize(document), UpdatedAt = DateTime.UtcNow.ToString("o") });
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string collection, string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        var body = await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT body FROM cabinet_documents WHERE collection = @Collection AND id = @Id",
            new { Collection = collection, Id = id });
        return body == null ? default : JsonSerializer.Deserialize<T>(body);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string collection, string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        return await connection.ExecuteAsync(
            "DELETE FROM cabinet_documents WHERE collection = @Collection AND id = @Id",
            new { Collection = collection, Id = id }) > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<T>> ListAsync<T>(string collection, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        var bodies = await connection.QueryAsync<string>(
            "SELECT body FROM cabinet_documents WHERE collection = @Collection ORDER BY id",
            new { Collection = collection });
        return bodies.Select(body => JsonSerializer.Deserialize<T>(body)!).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListCollectionsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        return (await connection.QueryAsync<string>("SELECT DISTINCT collection FROM cabinet_documents ORDER BY collection")).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KeyValuePair<string, string>>> ExportAsync(string collection, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        var rows = await connection.QueryAsync<(string Id, string Body)>(
            "SELECT id AS Id, body AS Body FROM cabinet_documents WHERE collection = @Collection ORDER BY id",
            new { Collection = collection });
        return rows.Select(row => new KeyValuePair<string, string>(row.Id, row.Body)).ToList();
    }

    /// <inheritdoc />
    public async Task ImportAsync(string collection, string id, string json, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.ExecuteAsync(
            """
            INSERT INTO cabinet_documents (collection, id, body, updated_at)
            VALUES (@Collection, @Id, @Body, @UpdatedAt)
            ON CONFLICT (collection, id) DO UPDATE SET body = @Body, updated_at = @UpdatedAt
            """,
            new { Collection = collection, Id = id, Body = json, UpdatedAt = DateTime.UtcNow.ToString("o") });
    }
}
