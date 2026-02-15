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
/// SQLite-backed storage for data model definitions and pre-aggregated summaries.
/// Uses the same database as log events (via the Storage:ConnectionString config).
/// </summary>
public sealed class SqliteDataModelStorage : IDataModelStorage
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteDataModelStorage> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SqliteDataModelStorage(
        IOptions<CabinetConfiguration> config,
        ILogger<SqliteDataModelStorage> logger)
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
                CREATE TABLE IF NOT EXISTS data_models (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    description TEXT,
                    base_query TEXT NOT NULL,
                    fields_json TEXT NOT NULL DEFAULT '[]',
                    bucket_span_minutes INTEGER NOT NULL DEFAULT 60,
                    created_at TEXT NOT NULL,
                    last_rebuilt_at TEXT,
                    acceleration_enabled INTEGER NOT NULL DEFAULT 1,
                    retention_days INTEGER NOT NULL DEFAULT 90
                );

                CREATE TABLE IF NOT EXISTS data_model_summaries (
                    model_id TEXT NOT NULL,
                    time_bucket TEXT NOT NULL,
                    field_values_json TEXT NOT NULL DEFAULT '{}',
                    count INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY (model_id) REFERENCES data_models(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS idx_dms_model_id ON data_model_summaries(model_id);
                CREATE INDEX IF NOT EXISTS idx_dms_time ON data_model_summaries(model_id, time_bucket);
            ";

            await connection.ExecuteAsync(sql);
            _initialized = true;
            _logger.LogInformation("Data model storage initialized");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task SaveModelAsync(DataModel model, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = @"
            INSERT OR REPLACE INTO data_models
                (id, name, description, base_query, fields_json, bucket_span_minutes,
                 created_at, last_rebuilt_at, acceleration_enabled, retention_days)
            VALUES
                (@Id, @Name, @Description, @BaseQuery, @FieldsJson, @BucketSpanMinutes,
                 @CreatedAt, @LastRebuiltAt, @AccelerationEnabled, @RetentionDays)";

        await connection.ExecuteAsync(sql, new
        {
            model.Id,
            model.Name,
            model.Description,
            model.BaseQuery,
            FieldsJson = JsonSerializer.Serialize(model.Fields, JsonOptions),
            BucketSpanMinutes = (int)model.BucketSpan.TotalMinutes,
            CreatedAt = model.CreatedAt.ToString("O"),
            LastRebuiltAt = model.LastRebuiltAt?.ToString("O"),
            AccelerationEnabled = model.AccelerationEnabled ? 1 : 0,
            model.RetentionDays
        });
    }

    public async Task<DataModel?> GetModelAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<DataModelRow>(
            "SELECT * FROM data_models WHERE id = @Id", new { Id = id });

        return row == null ? null : MapToModel(row);
    }

    public async Task<IReadOnlyList<DataModel>> GetAllModelsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<DataModelRow>("SELECT * FROM data_models ORDER BY name");
        return rows.Select(MapToModel).ToList().AsReadOnly();
    }

    public async Task<bool> DeleteModelAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // Delete summaries first (foreign key may not be enforced without PRAGMA)
        await connection.ExecuteAsync(
            "DELETE FROM data_model_summaries WHERE model_id = @Id", new { Id = id });

        var rows = await connection.ExecuteAsync(
            "DELETE FROM data_models WHERE id = @Id", new { Id = id });

        return rows > 0;
    }

    public async Task SaveSummariesAsync(
        string modelId,
        IReadOnlyList<DataModelSummary> summaries,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            // Clear existing summaries for this model
            await connection.ExecuteAsync(
                "DELETE FROM data_model_summaries WHERE model_id = @ModelId",
                new { ModelId = modelId },
                transaction);

            // Insert new summaries in batches
            const int batchSize = 500;
            for (var i = 0; i < summaries.Count; i += batchSize)
            {
                var batch = summaries.Skip(i).Take(batchSize);

                var sql = @"
                    INSERT INTO data_model_summaries (model_id, time_bucket, field_values_json, count)
                    VALUES (@ModelId, @TimeBucket, @FieldValuesJson, @Count)";

                await connection.ExecuteAsync(sql, batch.Select(s => new
                {
                    ModelId = modelId,
                    TimeBucket = s.TimeBucket.ToString("O"),
                    FieldValuesJson = JsonSerializer.Serialize(s.FieldValues, JsonOptions),
                    s.Count
                }), transaction);
            }

            await transaction.CommitAsync(cancellationToken);

            _logger.LogDebug(
                "Saved {Count} summaries for data model {ModelId}", summaries.Count, modelId);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<List<DataModelSummary>> GetSummariesAsync(
        string modelId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<DataModelSummaryRow>(
            "SELECT * FROM data_model_summaries WHERE model_id = @ModelId ORDER BY time_bucket",
            new { ModelId = modelId });

        return rows.Select(r => new DataModelSummary
        {
            TimeBucket = DateTime.Parse(r.time_bucket).ToUniversalTime(),
            FieldValues = JsonSerializer.Deserialize<Dictionary<string, string>>(
                              r.field_values_json, JsonOptions) ?? new Dictionary<string, string>(),
            Count = r.count
        }).ToList();
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized)
            await InitializeAsync(cancellationToken);
    }

    private static DataModel MapToModel(DataModelRow row)
    {
        return new DataModel
        {
            Id = row.id,
            Name = row.name,
            Description = row.description,
            BaseQuery = row.base_query,
            Fields = JsonSerializer.Deserialize<List<string>>(row.fields_json, JsonOptions) ?? new List<string>(),
            BucketSpan = TimeSpan.FromMinutes(row.bucket_span_minutes),
            CreatedAt = DateTime.Parse(row.created_at).ToUniversalTime(),
            LastRebuiltAt = string.IsNullOrEmpty(row.last_rebuilt_at)
                ? null
                : DateTime.Parse(row.last_rebuilt_at).ToUniversalTime(),
            AccelerationEnabled = row.acceleration_enabled != 0,
            RetentionDays = row.retention_days
        };
    }

    // Dapper row types (match SQLite column names)
    private sealed class DataModelRow
    {
        public string id { get; set; } = string.Empty;
        public string name { get; set; } = string.Empty;
        public string? description { get; set; }
        public string base_query { get; set; } = string.Empty;
        public string fields_json { get; set; } = "[]";
        public int bucket_span_minutes { get; set; } = 60;
        public string created_at { get; set; } = string.Empty;
        public string? last_rebuilt_at { get; set; }
        public int acceleration_enabled { get; set; } = 1;
        public int retention_days { get; set; } = 90;
    }

    private sealed class DataModelSummaryRow
    {
        public string model_id { get; set; } = string.Empty;
        public string time_bucket { get; set; } = string.Empty;
        public string field_values_json { get; set; } = "{}";
        public long count { get; set; }
    }
}
