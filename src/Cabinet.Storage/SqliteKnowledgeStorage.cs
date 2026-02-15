using System.Text.Json;
using Cabinet.Core.Configuration;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cabinet.Storage;

/// <summary>
/// SQLite-backed storage for knowledge objects (lookups, macros, field aliases)
/// </summary>
public sealed class SqliteKnowledgeStorage : IKnowledgeStorage
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteKnowledgeStorage> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SqliteKnowledgeStorage(
        IOptions<CabinetConfiguration> config,
        ILogger<SqliteKnowledgeStorage> logger)
    {
        _connectionString = config.Value.Storage.ConnectionString;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var sql = @"
                CREATE TABLE IF NOT EXISTS lookup_tables (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    description TEXT,
                    created_at INTEGER NOT NULL,
                    updated_at INTEGER NOT NULL,
                    columns_json TEXT NOT NULL DEFAULT '[]',
                    rows_json TEXT NOT NULL DEFAULT '[]'
                );

                CREATE TABLE IF NOT EXISTS query_macros (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    description TEXT,
                    definition TEXT NOT NULL,
                    parameters_json TEXT NOT NULL DEFAULT '[]',
                    validation_regex TEXT,
                    created_at INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS field_aliases (
                    id TEXT PRIMARY KEY,
                    source_type TEXT NOT NULL DEFAULT '*',
                    original_field TEXT NOT NULL,
                    alias_field TEXT NOT NULL,
                    created_at INTEGER NOT NULL
                );
            ";

            await connection.ExecuteAsync(sql);
            _initialized = true;
            _logger.LogInformation("Knowledge storage initialized");
        }
        finally
        {
            _initLock.Release();
        }
    }

    // ===== Lookup Tables =====

    public async Task<LookupTable> SaveLookupAsync(LookupTable lookup, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = @"
            INSERT OR REPLACE INTO lookup_tables
                (id, name, description, created_at, updated_at, columns_json, rows_json)
            VALUES
                (@Id, @Name, @Description, @CreatedAt, @UpdatedAt, @ColumnsJson, @RowsJson)";

        await connection.ExecuteAsync(sql, new
        {
            lookup.Id,
            lookup.Name,
            lookup.Description,
            CreatedAt = lookup.CreatedAt.Ticks,
            UpdatedAt = lookup.UpdatedAt.Ticks,
            ColumnsJson = JsonSerializer.Serialize(lookup.Columns, JsonOptions),
            RowsJson = JsonSerializer.Serialize(lookup.Rows, JsonOptions)
        });

        return lookup;
    }

    public async Task<LookupTable?> GetLookupByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<LookupRow>(
            "SELECT * FROM lookup_tables WHERE id = @Id", new { Id = id });

        return row == null ? null : MapLookup(row);
    }

    public async Task<LookupTable?> GetLookupByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<LookupRow>(
            "SELECT * FROM lookup_tables WHERE name = @Name COLLATE NOCASE", new { Name = name });

        return row == null ? null : MapLookup(row);
    }

    public async Task<IReadOnlyList<LookupTable>> GetAllLookupsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<LookupRow>(
            "SELECT * FROM lookup_tables ORDER BY updated_at DESC");

        return rows.Select(MapLookup).ToList();
    }

    public async Task<bool> DeleteLookupAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var affected = await connection.ExecuteAsync(
            "DELETE FROM lookup_tables WHERE id = @Id", new { Id = id });

        return affected > 0;
    }

    // ===== Query Macros =====

    public async Task<QueryMacro> SaveMacroAsync(QueryMacro macro, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = @"
            INSERT OR REPLACE INTO query_macros
                (id, name, description, definition, parameters_json, validation_regex, created_at)
            VALUES
                (@Id, @Name, @Description, @Definition, @ParametersJson, @ValidationRegex, @CreatedAt)";

        await connection.ExecuteAsync(sql, new
        {
            macro.Id,
            macro.Name,
            macro.Description,
            macro.Definition,
            ParametersJson = JsonSerializer.Serialize(macro.Parameters, JsonOptions),
            macro.ValidationRegex,
            CreatedAt = macro.CreatedAt.Ticks
        });

        return macro;
    }

    public async Task<QueryMacro?> GetMacroByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<MacroRow>(
            "SELECT * FROM query_macros WHERE id = @Id", new { Id = id });

        return row == null ? null : MapMacro(row);
    }

    public async Task<QueryMacro?> GetMacroByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<MacroRow>(
            "SELECT * FROM query_macros WHERE name = @Name COLLATE NOCASE", new { Name = name });

        return row == null ? null : MapMacro(row);
    }

    public async Task<IReadOnlyList<QueryMacro>> GetAllMacrosAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<MacroRow>(
            "SELECT * FROM query_macros ORDER BY created_at DESC");

        return rows.Select(MapMacro).ToList();
    }

    public async Task<bool> DeleteMacroAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var affected = await connection.ExecuteAsync(
            "DELETE FROM query_macros WHERE id = @Id", new { Id = id });

        return affected > 0;
    }

    // ===== Field Aliases =====

    public async Task<FieldAlias> SaveAliasAsync(FieldAlias alias, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = @"
            INSERT OR REPLACE INTO field_aliases
                (id, source_type, original_field, alias_field, created_at)
            VALUES
                (@Id, @SourceType, @OriginalField, @AliasField, @CreatedAt)";

        await connection.ExecuteAsync(sql, new
        {
            alias.Id,
            alias.SourceType,
            alias.OriginalField,
            alias.AliasField,
            CreatedAt = alias.CreatedAt.Ticks
        });

        return alias;
    }

    public async Task<FieldAlias?> GetAliasByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<AliasRow>(
            "SELECT * FROM field_aliases WHERE id = @Id", new { Id = id });

        return row == null ? null : MapAlias(row);
    }

    public async Task<IReadOnlyList<FieldAlias>> GetAllAliasesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<AliasRow>(
            "SELECT * FROM field_aliases ORDER BY created_at DESC");

        return rows.Select(MapAlias).ToList();
    }

    public async Task<bool> DeleteAliasAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var affected = await connection.ExecuteAsync(
            "DELETE FROM field_aliases WHERE id = @Id", new { Id = id });

        return affected > 0;
    }

    // ===== Mapping =====

    private LookupTable MapLookup(LookupRow row) => new()
    {
        Id = row.id,
        Name = row.name,
        Description = row.description,
        CreatedAt = new DateTime(row.created_at),
        UpdatedAt = new DateTime(row.updated_at),
        Columns = DeserializeOrDefault<List<string>>(row.columns_json) ?? new List<string>(),
        Rows = DeserializeOrDefault<List<Dictionary<string, string>>>(row.rows_json)
               ?? new List<Dictionary<string, string>>()
    };

    private QueryMacro MapMacro(MacroRow row) => new()
    {
        Id = row.id,
        Name = row.name,
        Description = row.description,
        Definition = row.definition,
        Parameters = DeserializeOrDefault<List<string>>(row.parameters_json) ?? new List<string>(),
        ValidationRegex = row.validation_regex,
        CreatedAt = new DateTime(row.created_at)
    };

    private static FieldAlias MapAlias(AliasRow row) => new()
    {
        Id = row.id,
        SourceType = row.source_type ?? "*",
        OriginalField = row.original_field,
        AliasField = row.alias_field,
        CreatedAt = new DateTime(row.created_at)
    };

    private T? DeserializeOrDefault<T>(string? json)
    {
        if (string.IsNullOrEmpty(json)) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize JSON for {Type}: {Json}",
                typeof(T).Name, json.Length > 200 ? json[..200] + "..." : json);
            return default;
        }
    }

    // ===== Row types =====

    private sealed class LookupRow
    {
        public string id { get; set; } = string.Empty;
        public string name { get; set; } = string.Empty;
        public string? description { get; set; }
        public long created_at { get; set; }
        public long updated_at { get; set; }
        public string? columns_json { get; set; }
        public string? rows_json { get; set; }
    }

    private sealed class MacroRow
    {
        public string id { get; set; } = string.Empty;
        public string name { get; set; } = string.Empty;
        public string? description { get; set; }
        public string definition { get; set; } = string.Empty;
        public string? parameters_json { get; set; }
        public string? validation_regex { get; set; }
        public long created_at { get; set; }
    }

    private sealed class AliasRow
    {
        public string id { get; set; } = string.Empty;
        public string? source_type { get; set; }
        public string original_field { get; set; } = string.Empty;
        public string alias_field { get; set; } = string.Empty;
        public long created_at { get; set; }
    }
}
