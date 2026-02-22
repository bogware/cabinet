using System.Text.Json;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Cabinet.Database.Providers;

/// <summary>
/// PostgreSQL implementation of <see cref="IKnowledgeStorage"/> for lookup tables, query macros,
/// and field aliases. Uses JSONB for complex nested data (columns, rows, parameters) and
/// connection pooling via <see cref="NpgsqlDataSource"/>.
/// </summary>
public sealed class PostgresKnowledgeStorage : IKnowledgeStorage, IDisposable
{
    private readonly ILogger<PostgresKnowledgeStorage> _logger;
    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Creates a new PostgreSQL knowledge storage instance.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string</param>
    /// <param name="logger">Logger instance</param>
    public PostgresKnowledgeStorage(string connectionString, ILogger<PostgresKnowledgeStorage> logger)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = dataSourceBuilder.Build();
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            _logger.LogInformation("Initializing PostgreSQL knowledge storage");
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

            // Lookup tables — columns and rows stored as JSONB
            await conn.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS lookup_tables (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    description TEXT,
                    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    columns JSONB NOT NULL DEFAULT '[]',
                    rows JSONB NOT NULL DEFAULT '[]'
                )");

            await conn.ExecuteAsync(@"
                CREATE UNIQUE INDEX IF NOT EXISTS idx_lookup_tables_name
                ON lookup_tables (LOWER(name))");

            await conn.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_lookup_tables_updated_at
                ON lookup_tables (updated_at DESC)");

            // Query macros — parameters stored as JSONB array
            await conn.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS query_macros (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    description TEXT,
                    definition TEXT NOT NULL,
                    parameters JSONB NOT NULL DEFAULT '[]',
                    validation_regex TEXT,
                    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
                )");

            await conn.ExecuteAsync(@"
                CREATE UNIQUE INDEX IF NOT EXISTS idx_query_macros_name
                ON query_macros (LOWER(name))");

            await conn.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_query_macros_created_at
                ON query_macros (created_at DESC)");

            // Field aliases
            await conn.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS field_aliases (
                    id TEXT PRIMARY KEY,
                    source_type TEXT NOT NULL DEFAULT '*',
                    original_field TEXT NOT NULL,
                    alias_field TEXT NOT NULL,
                    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
                )");

            await conn.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_field_aliases_created_at
                ON field_aliases (created_at DESC)");

            await conn.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_field_aliases_source_type
                ON field_aliases (source_type)");

            _initialized = true;
            _logger.LogInformation("PostgreSQL knowledge storage initialized");
        }
        finally
        {
            _initLock.Release();
        }
    }

    // ===== Lookup Tables =====

    /// <inheritdoc />
    public async Task<LookupTable> SaveLookupAsync(LookupTable lookup, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        await conn.ExecuteAsync(@"
            INSERT INTO lookup_tables (id, name, description, created_at, updated_at, columns, rows)
            VALUES (@Id, @Name, @Description, @CreatedAt, @UpdatedAt, @Columns::jsonb, @Rows::jsonb)
            ON CONFLICT (id) DO UPDATE SET
                name = EXCLUDED.name,
                description = EXCLUDED.description,
                updated_at = EXCLUDED.updated_at,
                columns = EXCLUDED.columns,
                rows = EXCLUDED.rows",
            new
            {
                lookup.Id,
                lookup.Name,
                lookup.Description,
                lookup.CreatedAt,
                lookup.UpdatedAt,
                Columns = JsonSerializer.Serialize(lookup.Columns, JsonOptions),
                Rows = JsonSerializer.Serialize(lookup.Rows, JsonOptions)
            });

        return lookup;
    }

    /// <inheritdoc />
    public async Task<LookupTable?> GetLookupByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var row = await conn.QueryFirstOrDefaultAsync<LookupRow>(
            "SELECT * FROM lookup_tables WHERE id = @Id", new { Id = id });

        return row == null ? null : MapLookup(row);
    }

    /// <inheritdoc />
    public async Task<LookupTable?> GetLookupByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var row = await conn.QueryFirstOrDefaultAsync<LookupRow>(
            "SELECT * FROM lookup_tables WHERE LOWER(name) = LOWER(@Name)", new { Name = name });

        return row == null ? null : MapLookup(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LookupTable>> GetAllLookupsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await conn.QueryAsync<LookupRow>(
            "SELECT * FROM lookup_tables ORDER BY updated_at DESC");

        return rows.Select(MapLookup).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> DeleteLookupAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var affected = await conn.ExecuteAsync(
            "DELETE FROM lookup_tables WHERE id = @Id", new { Id = id });

        return affected > 0;
    }

    // ===== Query Macros =====

    /// <inheritdoc />
    public async Task<QueryMacro> SaveMacroAsync(QueryMacro macro, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        await conn.ExecuteAsync(@"
            INSERT INTO query_macros (id, name, description, definition, parameters, validation_regex, created_at)
            VALUES (@Id, @Name, @Description, @Definition, @Parameters::jsonb, @ValidationRegex, @CreatedAt)
            ON CONFLICT (id) DO UPDATE SET
                name = EXCLUDED.name,
                description = EXCLUDED.description,
                definition = EXCLUDED.definition,
                parameters = EXCLUDED.parameters,
                validation_regex = EXCLUDED.validation_regex",
            new
            {
                macro.Id,
                macro.Name,
                macro.Description,
                macro.Definition,
                Parameters = JsonSerializer.Serialize(macro.Parameters, JsonOptions),
                macro.ValidationRegex,
                macro.CreatedAt
            });

        return macro;
    }

    /// <inheritdoc />
    public async Task<QueryMacro?> GetMacroByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var row = await conn.QueryFirstOrDefaultAsync<MacroRow>(
            "SELECT * FROM query_macros WHERE id = @Id", new { Id = id });

        return row == null ? null : MapMacro(row);
    }

    /// <inheritdoc />
    public async Task<QueryMacro?> GetMacroByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var row = await conn.QueryFirstOrDefaultAsync<MacroRow>(
            "SELECT * FROM query_macros WHERE LOWER(name) = LOWER(@Name)", new { Name = name });

        return row == null ? null : MapMacro(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueryMacro>> GetAllMacrosAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await conn.QueryAsync<MacroRow>(
            "SELECT * FROM query_macros ORDER BY created_at DESC");

        return rows.Select(MapMacro).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> DeleteMacroAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var affected = await conn.ExecuteAsync(
            "DELETE FROM query_macros WHERE id = @Id", new { Id = id });

        return affected > 0;
    }

    // ===== Field Aliases =====

    /// <inheritdoc />
    public async Task<FieldAlias> SaveAliasAsync(FieldAlias alias, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        await conn.ExecuteAsync(@"
            INSERT INTO field_aliases (id, source_type, original_field, alias_field, created_at)
            VALUES (@Id, @SourceType, @OriginalField, @AliasField, @CreatedAt)
            ON CONFLICT (id) DO UPDATE SET
                source_type = EXCLUDED.source_type,
                original_field = EXCLUDED.original_field,
                alias_field = EXCLUDED.alias_field",
            new
            {
                alias.Id,
                alias.SourceType,
                alias.OriginalField,
                alias.AliasField,
                alias.CreatedAt
            });

        return alias;
    }

    /// <inheritdoc />
    public async Task<FieldAlias?> GetAliasByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var row = await conn.QueryFirstOrDefaultAsync<AliasRow>(
            "SELECT * FROM field_aliases WHERE id = @Id", new { Id = id });

        return row == null ? null : MapAlias(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FieldAlias>> GetAllAliasesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await conn.QueryAsync<AliasRow>(
            "SELECT * FROM field_aliases ORDER BY created_at DESC");

        return rows.Select(MapAlias).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAliasAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var affected = await conn.ExecuteAsync(
            "DELETE FROM field_aliases WHERE id = @Id", new { Id = id });

        return affected > 0;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _dataSource.Dispose();
    }

    // ===== Private helpers =====

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized)
            await InitializeAsync(cancellationToken);
    }

    private LookupTable MapLookup(LookupRow row) => new()
    {
        Id = row.id,
        Name = row.name,
        Description = row.description,
        CreatedAt = row.created_at,
        UpdatedAt = row.updated_at,
        Columns = DeserializeOrDefault<List<string>>(row.columns) ?? new List<string>(),
        Rows = DeserializeOrDefault<List<Dictionary<string, string>>>(row.rows)
               ?? new List<Dictionary<string, string>>()
    };

    private QueryMacro MapMacro(MacroRow row) => new()
    {
        Id = row.id,
        Name = row.name,
        Description = row.description,
        Definition = row.definition,
        Parameters = DeserializeOrDefault<List<string>>(row.parameters) ?? new List<string>(),
        ValidationRegex = row.validation_regex,
        CreatedAt = row.created_at
    };

    private static FieldAlias MapAlias(AliasRow row) => new()
    {
        Id = row.id,
        SourceType = row.source_type ?? "*",
        OriginalField = row.original_field,
        AliasField = row.alias_field,
        CreatedAt = row.created_at
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

    // ===== Dapper row types =====

    /// <summary>Internal row type for Dapper mapping of lookup_tables</summary>
    private sealed class LookupRow
    {
        public string id { get; set; } = string.Empty;
        public string name { get; set; } = string.Empty;
        public string? description { get; set; }
        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }
        public string? columns { get; set; }
        public string? rows { get; set; }
    }

    /// <summary>Internal row type for Dapper mapping of query_macros</summary>
    private sealed class MacroRow
    {
        public string id { get; set; } = string.Empty;
        public string name { get; set; } = string.Empty;
        public string? description { get; set; }
        public string definition { get; set; } = string.Empty;
        public string? parameters { get; set; }
        public string? validation_regex { get; set; }
        public DateTime created_at { get; set; }
    }

    /// <summary>Internal row type for Dapper mapping of field_aliases</summary>
    private sealed class AliasRow
    {
        public string id { get; set; } = string.Empty;
        public string? source_type { get; set; }
        public string original_field { get; set; } = string.Empty;
        public string alias_field { get; set; } = string.Empty;
        public DateTime created_at { get; set; }
    }
}
