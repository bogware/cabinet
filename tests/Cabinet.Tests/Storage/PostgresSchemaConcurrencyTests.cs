using Cabinet.Database.Providers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Cabinet.Tests.Storage;

[Trait("Category", "Integration")]
public class PostgresSchemaConcurrencyTests
{
    private const int Instances = 8;

    [SkippableFact]
    public async Task Initialize_ManyInstancesOnNewDatabase_AllSucceed()
    {
        var connectionString = Environment.GetEnvironmentVariable("CABINET_TEST_POSTGRES_CONNSTR");
        Skip.If(string.IsNullOrEmpty(connectionString), "CABINET_TEST_POSTGRES_CONNSTR is not set");

        // A new schema is what makes the race possible: nothing exists yet.
        var schema = $"concurrency_{Guid.NewGuid():N}";
        await using var admin = NpgsqlDataSource.Create(connectionString!);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA {schema}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        var scoped = $"{connectionString};Search Path={schema}";
        var initializations = new List<Task>();
        var stores = new List<IDisposable>();
        for (var i = 0; i < Instances; i++)
        {
            var logs = new PostgresLogStorage(scoped, NullLogger<PostgresLogStorage>.Instance);
            var dashboards = new PostgresDashboardStorage(scoped, NullLogger<PostgresDashboardStorage>.Instance);
            var knowledge = new PostgresKnowledgeStorage(scoped, NullLogger<PostgresKnowledgeStorage>.Instance);
            var queries = new PostgresSavedQueryStorage(scoped, NullLogger<PostgresSavedQueryStorage>.Instance);
            var schedules = new PostgresScheduledSearchStorage(scoped, NullLogger<PostgresScheduledSearchStorage>.Instance);
            stores.AddRange([logs, dashboards, knowledge, queries, schedules]);
            initializations.AddRange(
            [
                logs.InitializeAsync(),
                dashboards.InitializeAsync(),
                knowledge.InitializeAsync(),
                queries.InitializeAsync(),
                schedules.InitializeAsync(),
            ]);
        }

        try
        {
            var act = () => Task.WhenAll(initializations);

            await act.Should().NotThrowAsync();
        }
        finally
        {
            stores.ForEach(store => store.Dispose());
            await using var drop = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
