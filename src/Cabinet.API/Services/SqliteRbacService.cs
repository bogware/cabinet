using System.Text.Json;
using Cabinet.Core.Configuration;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Cabinet.API.Services;

/// <summary>
/// Database-backed RBAC service using SQLite for persistent storage of principals and permissions.
/// Falls through to ConfigurationRbacService for principals not found in the database,
/// allowing API-key-tier-based defaults while supporting per-principal overrides.
/// </summary>
public sealed class SqliteRbacService : IRbacService, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ConfigurationRbacService _fallback;
    private readonly ILogger<SqliteRbacService> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    public SqliteRbacService(
        IOptions<CabinetConfiguration> config,
        ConfigurationRbacService fallback,
        ILogger<SqliteRbacService> logger)
    {
        var connStr = config.Value.Storage?.ConnectionString ?? "Data Source=cabinet.db";
        // Use a separate database file for RBAC to avoid coupling to log storage.
        // Extract the Data Source path and replace just the filename component.
        var dataSourcePrefix = "Data Source=";
        var dataSourceIdx = connStr.IndexOf(dataSourcePrefix, StringComparison.OrdinalIgnoreCase);
        if (dataSourceIdx >= 0)
        {
            var path = connStr[(dataSourceIdx + dataSourcePrefix.Length)..].Split(';')[0].Trim();
            var dir = Path.GetDirectoryName(path) ?? "";
            var rbacPath = Path.Combine(dir, "cabinet-rbac.db");
            _connectionString = $"Data Source={rbacPath}";
        }
        else
        {
            _connectionString = "Data Source=cabinet-rbac.db";
        }
        _fallback = fallback;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(cancellationToken);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS rbac_principals (
                    id TEXT PRIMARY KEY,
                    display_name TEXT,
                    role INTEGER NOT NULL DEFAULT 0,
                    additional_capabilities INTEGER NOT NULL DEFAULT 0,
                    index_permissions TEXT NOT NULL DEFAULT '[]',
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
                );
                CREATE INDEX IF NOT EXISTS idx_rbac_principals_role ON rbac_principals(role);
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken);

            _initialized = true;
            _logger.LogInformation("SQLite RBAC service initialized");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<RbacPrincipal?> GetPrincipalAsync(string apiKeyHash, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, display_name, role, additional_capabilities, index_permissions FROM rbac_principals WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", apiKeyHash);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadPrincipal(reader);
        }

        // Fall through to config-based resolution
        return await _fallback.GetPrincipalAsync(apiKeyHash, cancellationToken);
    }

    public async Task<bool> HasCapabilityAsync(string apiKeyHash, Capability capability, string indexName = "main", CancellationToken cancellationToken = default)
    {
        var principal = await GetPrincipalAsync(apiKeyHash, cancellationToken);
        return principal?.HasCapability(capability, indexName) ?? false;
    }

    public async Task<IReadOnlyList<string>> GetPermittedIndexesAsync(string apiKeyHash, CancellationToken cancellationToken = default)
    {
        var principal = await GetPrincipalAsync(apiKeyHash, cancellationToken);
        if (principal == null)
            return Array.Empty<string>();

        return principal.IndexPermissions
            .Select(p => p.IndexPattern)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Save or update a principal in the database
    /// </summary>
    public async Task SavePrincipalAsync(RbacPrincipal principal, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var permissionsJson = JsonSerializer.Serialize(principal.IndexPermissions.Select(p => new
        {
            indexPattern = p.IndexPattern,
            capabilities = (int)p.Capabilities
        }));

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rbac_principals (id, display_name, role, additional_capabilities, index_permissions, updated_at)
            VALUES (@id, @displayName, @role, @additionalCaps, @permissions, datetime('now'))
            ON CONFLICT(id) DO UPDATE SET
                display_name = @displayName,
                role = @role,
                additional_capabilities = @additionalCaps,
                index_permissions = @permissions,
                updated_at = datetime('now')
            """;
        cmd.Parameters.AddWithValue("@id", principal.Id);
        cmd.Parameters.AddWithValue("@displayName", (object?)principal.DisplayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@role", (int)principal.Role);
        cmd.Parameters.AddWithValue("@additionalCaps", (int)principal.AdditionalCapabilities);
        cmd.Parameters.AddWithValue("@permissions", permissionsJson);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogInformation("Saved RBAC principal {Id} (role: {Role})", principal.Id, principal.Role);
    }

    /// <summary>
    /// Delete a principal from the database
    /// </summary>
    public async Task<bool> DeletePrincipalAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM rbac_principals WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);

        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// List all principals stored in the database
    /// </summary>
    public async Task<IReadOnlyList<RbacPrincipal>> ListPrincipalsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, display_name, role, additional_capabilities, index_permissions FROM rbac_principals ORDER BY role DESC, display_name";

        var principals = new List<RbacPrincipal>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            principals.Add(ReadPrincipal(reader));
        }

        return principals;
    }

    private static RbacPrincipal ReadPrincipal(SqliteDataReader reader)
    {
        var principal = new RbacPrincipal
        {
            Id = reader.GetString(0),
            DisplayName = reader.IsDBNull(1) ? null : reader.GetString(1),
            Role = (SystemRole)reader.GetInt32(2),
            AdditionalCapabilities = (Capability)reader.GetInt32(3)
        };

        var permissionsJson = reader.GetString(4);
        try
        {
            using var doc = JsonDocument.Parse(permissionsJson);
            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                principal.IndexPermissions.Add(new IndexPermission
                {
                    IndexPattern = elem.GetProperty("indexPattern").GetString() ?? "*",
                    Capabilities = (Capability)(elem.GetProperty("capabilities").GetInt32())
                });
            }
        }
        catch (JsonException)
        {
            // Fail closed: corrupted permission JSON gets no permissions rather than wildcard access
            principal.IndexPermissions.Add(new IndexPermission
            {
                IndexPattern = "*",
                Capabilities = 0
            });
        }

        return principal;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
            await InitializeAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _initLock.Dispose();
        await Task.CompletedTask;
    }
}
