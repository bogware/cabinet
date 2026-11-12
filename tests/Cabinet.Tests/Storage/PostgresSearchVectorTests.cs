using System.Reflection;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Database.Providers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Cabinet.Tests.Storage;

/// <summary>
/// The stored search_vector column: new databases use it at once, and older databases fill it in
/// the background while searches keep working through the old expression. Each test runs in its own
/// throwaway database so it can't disturb the shared one.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresSearchVectorTests
{
    private static string? Base => Environment.GetEnvironmentVariable("CABINET_TEST_POSTGRES_CONNSTR");

    private static string Admin => Replace(Base!, "postgres");

    private static string Replace(string connectionString, string database) =>
        string.Join(';', connectionString.Split(';').Select(part =>
            part.TrimStart().StartsWith("Database=", StringComparison.OrdinalIgnoreCase) ? $"Database={database}" : part));

    private static async Task<(string ConnectionString, Func<Task> Drop)> FreshDatabaseAsync()
    {
        var name = "cabinet_sv_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = new NpgsqlConnection(Admin))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await create.ExecuteNonQueryAsync();
        }

        async Task Drop()
        {
            await using var admin = new NpgsqlConnection(Admin);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }

        return (Replace(Base!, name), Drop);
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull or null ? default! : (T)value;
    }

    private static async Task<List<LogEvent>> SearchAsync(PostgresLogStorage storage, string text)
    {
        var events = new List<LogEvent>();
        await foreach (var e in storage.QueryAsync(new QueryContext { SearchText = text }))
        {
            events.Add(e);
        }

        return events;
    }

    private static LogEvent Event(string line) => new() { RawLine = line, Timestamp = DateTime.UtcNow, Index = "main" };

    [SkippableFact]
    public async Task NewDatabase_UsesTheStoredColumn_AndSearchWorks()
    {
        Skip.If(string.IsNullOrEmpty(Base), "PostgreSQL not available.");
        var (connectionString, drop) = await FreshDatabaseAsync();
        try
        {
            var storage = new PostgresLogStorage(connectionString, NullLogger<PostgresLogStorage>.Instance);
            await storage.InitializeAsync();
            await storage.StoreBatchAsync([Event("player said hello world"), Event("disk full on /var/log")]);

            (await ScalarAsync<string>(connectionString, "SELECT v FROM cabinet_meta WHERE k='search_vector_ready'")).Should().Be("true");
            (await ScalarAsync<long>(connectionString, "SELECT count(*) FROM log_events WHERE search_vector IS NULL")).Should().Be(0);

            (await SearchAsync(storage, "hello")).Should().ContainSingle();
            (await SearchAsync(storage, "log")).Should().ContainSingle("the file path splits into words");
            storage.Dispose();
        }
        finally
        {
            await drop();
        }
    }

    [SkippableFact]
    public async Task OlderDatabase_SearchesThroughTheFallback_ThenMigrationFillsTheColumn()
    {
        Skip.If(string.IsNullOrEmpty(Base), "PostgreSQL not available.");
        var (connectionString, drop) = await FreshDatabaseAsync();
        try
        {
            var storage = new PostgresLogStorage(connectionString, NullLogger<PostgresLogStorage>.Instance);
            await storage.InitializeAsync();
            await storage.StoreBatchAsync([Event("alpha from 10.0.0.7"), Event("beta connection reset"), Event("gamma all fine")]);

            // Make it look like data from before the column existed: clear the vectors and the flag,
            // and put this instance back on the fallback path, as an upgraded server would be.
            await using (var conn = new NpgsqlConnection(connectionString))
            {
                await conn.OpenAsync();
                await new NpgsqlCommand("UPDATE log_events SET search_vector = NULL", conn).ExecuteNonQueryAsync();
                await new NpgsqlCommand("DELETE FROM cabinet_meta WHERE k='search_vector_ready'", conn).ExecuteNonQueryAsync();
                await new NpgsqlCommand($"CREATE INDEX IF NOT EXISTS idx_log_events_search ON log_events USING GIN (to_tsvector('simple'::regconfig, regexp_replace(raw_line, '[^[:alnum:]]+', ' ', 'g')))", conn).ExecuteNonQueryAsync();
            }

            typeof(PostgresLogStorage).GetField("_searchVectorReady", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(storage, false);

            // The fallback recomputes the expression, so search is correct even with the column empty.
            (await SearchAsync(storage, "connection")).Should().ContainSingle();
            (await SearchAsync(storage, "10.0.0.7")).Should().ContainSingle();

            await storage.MigrateSearchVectorAsync();

            (await ScalarAsync<long>(connectionString, "SELECT count(*) FROM log_events WHERE search_vector IS NULL")).Should().Be(0);
            (await ScalarAsync<string>(connectionString, "SELECT v FROM cabinet_meta WHERE k='search_vector_ready'")).Should().Be("true");
            (await ScalarAsync<long>(connectionString, "SELECT count(*) FROM pg_class WHERE relname='idx_log_events_search_vector'")).Should().Be(1);
            (await ScalarAsync<long>(connectionString, "SELECT count(*) FROM pg_class WHERE relname='idx_log_events_search'")).Should().Be(0, "the old expression index is dropped once the column is ready");

            // Now on the fast path, searches still return the right events.
            (await SearchAsync(storage, "connection")).Should().ContainSingle();
            (await SearchAsync(storage, "alpha")).Should().ContainSingle();
            storage.Dispose();
        }
        finally
        {
            await drop();
        }
    }
}
