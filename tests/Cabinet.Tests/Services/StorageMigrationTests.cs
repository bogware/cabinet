using Cabinet.API.Services.Settings;
using Cabinet.API.Services.Storage;
using Cabinet.API.Services.Updates;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;

namespace Cabinet.Tests.Services;

/// <summary>
/// Moving Cabinet to another database through the same code the dashboard uses: everything
/// arrives, the settings switch, and the old database is left as it was.
/// </summary>
public sealed class StorageMigrationTests : IDisposable
{
    private static readonly string? Postgres = Environment.GetEnvironmentVariable("CABINET_TEST_POSTGRES_CONNSTR");

    private readonly string _directory = Directory.CreateTempSubdirectory("cabinet-migrate-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Yield();
            }
        }
    }

    private string Sqlite(string name) => $"Data Source={Path.Combine(_directory, name)}";

    private (StorageMigrationService Migration, IngestGate Gate, IConfigurationRoot Configuration) Build(string type, string connectionString)
    {
        // As the server: appsettings first, then the dashboard's settings file over it
        var appsettings = Path.Combine(_directory, $"appsettings-{Guid.NewGuid():N}.json");
        File.WriteAllText(appsettings, System.Text.Json.JsonSerializer.Serialize(new { Storage = new { Type = type, ConnectionString = connectionString } }));
        var builder = new ConfigurationBuilder().AddJsonFile(appsettings);
        var store = SettingsOverrideStore.AddTo(builder, Path.Combine(_directory, "settings", "cabinet.settings.json"));
        var configuration = builder.Build();
        var restarter = new ServiceRestarter(new UpdateOptions(), Mock.Of<IHostApplicationLifetime>(), NullLogger<ServiceRestarter>.Instance);
        var settings = new SettingsService(configuration, store, restarter);
        var gate = new IngestGate();
        return (new StorageMigrationService(configuration, gate, settings, restarter, NullLoggerFactory.Instance), gate, configuration);
    }

    /// <summary>Events and one of each kind of metadata, in the database a configuration names.</summary>
    private static async Task SeedAsync(IConfiguration configuration, int events)
    {
        var storage = StorageBackends.CreateLogStorage(configuration, NullLoggerFactory.Instance);
        await storage.InitializeAsync();
        var start = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        await storage.StoreBatchAsync(Enumerable.Range(0, events).Select(i => new LogEvent
        {
            Id = $"evt-{i:D6}-{Guid.NewGuid():N}"[..26],
            RawLine = $"line {i}",
            Timestamp = start.AddSeconds(i),
            Host = i % 2 == 0 ? "l4d2-dev" : "l4d2-prod",
            Index = "l4d2",
            Fields = { ["n"] = i },
        }).ToList());
        (storage as IDisposable)?.Dispose();

        await using var metadata = StorageBackends.CreateMetadata(configuration, NullLoggerFactory.Instance);
        var documents = metadata.GetRequiredService<IDocumentStore>();
        await documents.InitializeAsync();
        await documents.PutAsync("api-keys", "key-1", new { Name = "agent", Tier = "Ingest" });
        await documents.PutAsync("detection-rules", "rule-1", new { Name = "Bans" });
        var saved = metadata.GetRequiredService<ISavedQueryStorage>();
        await saved.InitializeAsync();
        await saved.SaveAsync(new SavedQuery { Id = "saved-1", Name = "Bans today", Query = "find Banid" });
        var dashboards = metadata.GetRequiredService<IDashboardStorage>();
        await dashboards.InitializeAsync();
        await dashboards.SaveAsync(new Dashboard { Id = "dash-1", Title = "Servers" });
    }

    private static async Task<MigrationStatus> WaitAsync(StorageMigrationService migration)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var status = migration.Status;
            if (status.State is not "Running")
            {
                return status;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Still running: {migration.Status.Phase}");
    }

    /// <summary>The settings file is reloaded by a file watcher, a moment after it's written.</summary>
    private static async Task<string?> SettingAsync(IConfiguration configuration, string key, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (configuration[key] != expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        return configuration[key];
    }

    private static async Task<long> CountAsync(IConfiguration configuration)
    {
        var storage = StorageBackends.CreateLogStorage(configuration, NullLoggerFactory.Instance);
        await storage.InitializeAsync();
        var count = await storage.GetCountAsync();
        (storage as IDisposable)?.Dispose();
        return count;
    }

    private static async Task MetadataShouldMatchAsync(IConfiguration configuration)
    {
        await using var metadata = StorageBackends.CreateMetadata(configuration, NullLoggerFactory.Instance);
        var documents = metadata.GetRequiredService<IDocumentStore>();
        (await documents.ExportAsync("api-keys")).Should().ContainSingle().Which.Key.Should().Be("key-1");
        (await documents.ExportAsync("detection-rules")).Should().ContainSingle();
        (await metadata.GetRequiredService<ISavedQueryStorage>().GetByIdAsync("saved-1"))!.Query.Should().Be("find Banid");
        (await metadata.GetRequiredService<IDashboardStorage>().GetByIdAsync("dash-1"))!.Title.Should().Be("Servers");
    }

    [Fact]
    public async Task MovingASqliteFile_CopiesEverything_AndSwitches()
    {
        var (migration, gate, configuration) = Build("SQLite", Sqlite("old.db"));
        await SeedAsync(configuration, 250);
        var target = Sqlite(Path.Combine("moved", "cabinet.db"));

        migration.Start("SQLite", target, copy: true, "test");
        var status = await WaitAsync(migration);

        status.State.Should().Be("WaitingForRestart", status.Error ?? status.Phase);
        status.EventsCopied.Should().Be(250);
        (await SettingAsync(configuration, "Storage:ConnectionString", target)).Should().Be(target, "the settings now point at the new file");
        gate.PausedBecause.Should().NotBeNull("nothing may land in the old file before the restart");
        var moved = StorageBackends.With(configuration, "SQLite", target);
        (await CountAsync(moved)).Should().Be(250);
        await MetadataShouldMatchAsync(moved);
        (await CountAsync(StorageBackends.With(configuration, "SQLite", Sqlite("old.db")))).Should().Be(250, "the old database is left as it was");
    }

    [SkippableFact]
    public async Task MovingToPostgres_CopiesEventsAndMetadata()
    {
        Skip.If(string.IsNullOrEmpty(Postgres));
        var database = $"cabinet_migrate_{Guid.NewGuid():N}"[..30];
        var target = new NpgsqlConnectionStringBuilder(Postgres) { Database = database }.ConnectionString;
        var (migration, _, configuration) = Build("SQLite", Sqlite("source.db"));
        await SeedAsync(configuration, 4321);
        try
        {
            var created = await migration.CreatePostgresDatabaseAsync(target);
            (await migration.TestAsync("PostgreSQL", created)).EventsStored.Should().Be(0);

            migration.Start("PostgreSQL", created, copy: true, "test");
            var status = await WaitAsync(migration);

            status.State.Should().Be("WaitingForRestart", status.Error ?? status.Phase);
            status.EventsCopied.Should().Be(4321);
            status.MetadataCopied.Should().BeGreaterThanOrEqualTo(4);
            (await SettingAsync(configuration, "Storage:Type", "PostgreSQL")).Should().Be("PostgreSQL");
            var moved = StorageBackends.With(configuration, "PostgreSQL", created);
            (await CountAsync(moved)).Should().Be(4321);
            await MetadataShouldMatchAsync(moved);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Postgres) { Database = "postgres" }.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task MovingToTheCabinetEngine_CopiesEverything_AndKeepsMetadataInItsFolder()
    {
        var (migration, gate, configuration) = Build("SQLite", Sqlite("source.db"));
        await SeedAsync(configuration, 3210);
        var folder = Path.Combine(_directory, "engine");

        (await migration.TestAsync("Cabinet", folder)).Ok.Should().BeTrue();
        migration.Start("Cabinet", folder, copy: true, "test");
        var status = await WaitAsync(migration);

        status.State.Should().Be("WaitingForRestart", status.Error ?? status.Phase);
        status.EventsCopied.Should().Be(3210);
        (await SettingAsync(configuration, "Storage:Type", "Cabinet")).Should().Be("Cabinet");
        configuration["Storage:DataPath"].Should().Be(folder);
        configuration["Storage:MetadataConnectionString"].Should().Be($"Data Source={Path.Combine(folder, "metadata.db")}");
        gate.PausedBecause.Should().NotBeNull();
        (await CountAsync(configuration)).Should().Be(3210);
        await MetadataShouldMatchAsync(configuration);

        // And back: the engine is a source too
        var (back, _, engineConfiguration) = Build("Cabinet", folder);
        engineConfiguration["Storage:DataPath"] = folder;
        engineConfiguration["Storage:MetadataConnectionString"] = configuration["Storage:MetadataConnectionString"];
        back.Start("SQLite", Sqlite("again.db"), copy: true, "test");
        (await WaitAsync(back)).EventsCopied.Should().Be(3210);
    }

    [SkippableFact]
    public async Task MovingFromPostgres_ToTheCabinetEngine_CopiesEventsAndMetadata()
    {
        Skip.If(string.IsNullOrEmpty(Postgres));
        var database = $"cabinet_toengine_{Guid.NewGuid():N}"[..30];
        var source = new NpgsqlConnectionStringBuilder(Postgres) { Database = database }.ConnectionString;
        var (creator, _, _) = Build("SQLite", Sqlite("unused.db"));
        await creator.CreatePostgresDatabaseAsync(source);
        try
        {
            var (migration, _, configuration) = Build("PostgreSQL", source);
            await SeedAsync(configuration, 2345);
            var folder = Path.Combine(_directory, "from-postgres");

            migration.Start("Cabinet", folder, copy: true, "test");
            var status = await WaitAsync(migration);

            status.State.Should().Be("WaitingForRestart", status.Error ?? status.Phase);
            (await SettingAsync(configuration, "Storage:Type", "Cabinet")).Should().Be("Cabinet");
            (await CountAsync(configuration)).Should().Be(2345);
            await MetadataShouldMatchAsync(configuration);
            (await CountAsync(StorageBackends.With(configuration, "PostgreSQL", source))).Should().Be(2345, "PostgreSQL is left as it was, to switch back to");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Postgres) { Database = "postgres" }.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task CopyingIntoADatabaseWithEvents_IsRefused_AndNothingSwitches()
    {
        var (migration, gate, configuration) = Build("SQLite", Sqlite("old.db"));
        await SeedAsync(configuration, 10);
        var busy = StorageBackends.With(configuration, "SQLite", Sqlite("busy.db"));
        await SeedAsync(busy, 5);

        migration.Start("SQLite", Sqlite("busy.db"), copy: true, "test");
        var status = await WaitAsync(migration);

        status.State.Should().Be("Failed");
        status.Phase.Should().Contain("already exists");
        configuration["Storage:ConnectionString"].Should().Be(Sqlite("old.db"));
        gate.PausedBecause.Should().BeNull("ingest resumes when a move stops");
    }

    [Fact]
    public async Task StartingFresh_SwitchesWithoutCopying()
    {
        var (migration, _, configuration) = Build("SQLite", Sqlite("old.db"));
        await SeedAsync(configuration, 10);

        migration.Start("SQLite", Sqlite("fresh.db"), copy: false, "test");
        var status = await WaitAsync(migration);

        status.State.Should().Be("WaitingForRestart", status.Error ?? status.Phase);
        (await SettingAsync(configuration, "Storage:ConnectionString", Sqlite("fresh.db"))).Should().Be(Sqlite("fresh.db"));
        (await CountAsync(StorageBackends.With(configuration, "SQLite", Sqlite("fresh.db")))).Should().Be(0);
    }

    [Fact]
    public void MovingToWhereItAlreadyIs_OrToAnUnknownType_IsRefused()
    {
        var (migration, _, _) = Build("SQLite", Sqlite("old.db"));

        ((Action)(() => migration.Start("SQLite", Sqlite("old.db"), true, "test"))).Should().Throw<InvalidOperationException>().WithMessage("*already uses*");
        ((Action)(() => migration.Start("Oracle", "x", true, "test"))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Testing_ExplainsWhatItFound()
    {
        var (migration, _, configuration) = Build("SQLite", Sqlite("old.db"));
        await SeedAsync(configuration, 3);

        (await migration.TestAsync("SQLite", Sqlite("old.db"))).EventsStored.Should().Be(3);
        (await migration.TestAsync("SQLite", Sqlite("new.db"))).Message.Should().Contain("new file");
        var unreachable = await migration.TestAsync("PostgreSQL", "Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p;Timeout=3");
        unreachable.Ok.Should().BeFalse();
        unreachable.Message.Should().NotContain("Password");
    }

    [Theory]
    [InlineData("POST", "/services/collector/event", true)]
    [InlineData("POST", "/api/v1/logs/ingest", true)]
    [InlineData("POST", "/v1/traces", true)]
    [InlineData("GET", "/services/collector/health", false)]
    [InlineData("POST", "/api/v1/query/execute", false)]
    public void TheGate_HoldsBackIngestOnly(string method, string path, bool held)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;

        IngestGate.IsIngest(context.Request).Should().Be(held);
    }

    [Fact]
    public void Descriptions_NeverShowPasswords()
    {
        StorageBackends.Describe("PostgreSQL", "Host=db;Port=5433;Database=cabinet;Username=cab;Password=hunter2")
            .Should().Be("PostgreSQL db:5433, database cabinet");
    }
}
