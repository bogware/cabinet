using Cabinet.API.Infrastructure;
using Cabinet.Core.Configuration;
using Cabinet.Core.Interfaces;
using Cabinet.Database.Providers;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cabinet.Tests.Services;

public sealed class StorageRegistrationTests : IDisposable
{
    private const string PostgresConnectionString = "Host=db.invalid;Database=cabinet;Username=u;Password=p";

    private readonly string _directory = Directory.CreateTempSubdirectory("cabinet-storage-").FullName;

    private ServiceProvider BuildProvider(string storageType, string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Type"] = storageType,
                ["Storage:ConnectionString"] = connectionString,
                ["Storage:MetadataConnectionString"] = $"Data Source={Path.Combine(_directory, "metadata.db")}",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<CabinetConfiguration>(configuration);
        services.AddCabinetMetadataStorage(configuration);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("postgres")]
    public void Postgres_UsesPostgresStoresWhereTheyExist(string storageType)
    {
        using var provider = BuildProvider(storageType, PostgresConnectionString);

        provider.GetRequiredService<IDashboardStorage>().Should().BeOfType<PostgresDashboardStorage>();
        provider.GetRequiredService<IKnowledgeStorage>().Should().BeOfType<PostgresKnowledgeStorage>();
        provider.GetRequiredService<IScheduledSearchStorage>().Should().BeOfType<PostgresScheduledSearchStorage>();
        provider.GetRequiredService<ISavedQueryStorage>().Should().BeOfType<PostgresSavedQueryStorage>();
    }

    [Fact]
    public void Postgres_SharesDocumentsAndLeadership()
    {
        using var provider = BuildProvider("PostgreSQL", PostgresConnectionString);

        provider.GetRequiredService<IDocumentStore>().Should().BeOfType<PostgresDocumentStore>();
        provider.GetRequiredService<ILeaderLease>().Should().BeOfType<PostgresLeaderLease>();
    }

    [Fact]
    public async Task OpenSearch_KeepsDocumentsOnMetadataDatabase()
    {
        using var provider = BuildProvider("OpenSearch", "https://search.invalid:9200");

        await provider.GetRequiredService<ISearchJobStorage>().InitializeAsync();
        await provider.GetRequiredService<IDataModelStorage>().InitializeAsync();

        File.Exists(Path.Combine(_directory, "metadata.db")).Should().BeTrue();
    }

    [Fact]
    public async Task OpenSearch_AllMetadataStoresInitializeOnMetadataDatabase()
    {
        using var provider = BuildProvider("OpenSearch", "https://search.invalid:9200");

        await provider.GetRequiredService<IDashboardStorage>().InitializeAsync();
        await provider.GetRequiredService<IKnowledgeStorage>().InitializeAsync();
        await provider.GetRequiredService<IScheduledSearchStorage>().InitializeAsync();
        await provider.GetRequiredService<ISavedQueryStorage>().InitializeAsync();
    }

    [Fact]
    public void Sqlite_UsesSqliteStores()
    {
        using var provider = BuildProvider("SQLite", $"Data Source={Path.Combine(_directory, "logs.db")}");

        provider.GetRequiredService<IDashboardStorage>().Should().BeOfType<SqliteDashboardStorage>();
        provider.GetRequiredService<ISearchJobStorage>().Should().BeOfType<DocumentSearchJobStorage>();
        provider.GetRequiredService<IDocumentStore>().Should().BeOfType<SqliteDocumentStore>();
        provider.GetRequiredService<ILeaderLease>().Should().BeOfType<SingleInstanceLease>();
    }

    [Fact]
    public void Memory_UsesInMemoryStores()
    {
        using var provider = BuildProvider("Memory", string.Empty);

        provider.GetRequiredService<IDashboardStorage>().Should().BeOfType<InMemoryDashboardStorage>();
        provider.GetRequiredService<ISearchJobStorage>().Should().BeOfType<DocumentSearchJobStorage>();
        provider.GetRequiredService<IDocumentStore>().Should().BeOfType<InMemoryDocumentStore>();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }
}
