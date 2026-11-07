using Cabinet.Alerts;
using Cabinet.API.Services;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Database.Providers;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;

namespace Cabinet.Tests.Services;

public abstract class ClusterCoordinatorTests
{
    protected abstract bool IsAvailable { get; }

    /// <summary>A coordinator for another instance of the same cluster.</summary>
    protected abstract IClusterCoordinator CreateInstance();

    private static readonly DateTime Time = new(2026, 3, 4, 5, 0, 0, DateTimeKind.Utc);

    [SkippableFact]
    public async Task Matches_CountOnce_InTheWindow()
    {
        Skip.IfNot(IsAvailable);
        var coordinator = CreateInstance();
        var rule = Guid.NewGuid().ToString("N");

        await coordinator.RecordMatchAsync(rule, "a", "e1", Time, Time.AddMinutes(-5));
        await coordinator.RecordMatchAsync(rule, "a", "e1", Time, Time.AddMinutes(-5));
        await coordinator.RecordMatchAsync(rule, "b", "e2", Time, Time.AddMinutes(-5));
        (await coordinator.RecordMatchAsync(rule, "a", "e3", Time.AddMinutes(1), Time.AddMinutes(-4))).Should().Be(2);

        // e1 and e3 fall outside a window that starts after them
        (await coordinator.RecordMatchAsync(rule, "a", "e4", Time.AddMinutes(20), Time.AddMinutes(15))).Should().Be(1);
    }

    [SkippableFact]
    public async Task Firing_HappensOnce_PerCooldown_AndResetsTheCount()
    {
        Skip.IfNot(IsAvailable);
        var coordinator = CreateInstance();
        var rule = Guid.NewGuid().ToString("N");
        await coordinator.RecordMatchAsync(rule, "a", "e1", Time, Time.AddMinutes(-5));

        (await coordinator.TryFireAsync(rule, "a", TimeSpan.FromHours(1))).Should().BeTrue();
        (await coordinator.TryFireAsync(rule, "a", TimeSpan.FromHours(1))).Should().BeFalse();
        (await coordinator.TryFireAsync(rule, "b", TimeSpan.FromHours(1))).Should().BeTrue("another group has its own cooldown");
        (await coordinator.TryFireAsync(rule, "a", TimeSpan.Zero)).Should().BeTrue("no cooldown");
        (await coordinator.RecordMatchAsync(rule, "a", "e2", Time, Time.AddMinutes(-5))).Should().Be(1, "firing starts the count again");
    }

    [SkippableFact]
    public async Task ManyInstancesRacing_OnlyOneFires()
    {
        Skip.IfNot(IsAvailable);
        var instances = Enumerable.Range(0, 4).Select(_ => CreateInstance()).ToList();
        var rule = Guid.NewGuid().ToString("N");

        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => instances[i % instances.Count].TryFireAsync(rule, "g", TimeSpan.FromHours(1))));

        results.Count(fired => fired).Should().Be(1);
    }
}

public sealed class InMemoryClusterCoordinatorTests : ClusterCoordinatorTests
{
    private readonly InMemoryClusterCoordinator _shared = new();

    protected override bool IsAvailable => true;

    // One process: every caller shares the same state
    protected override IClusterCoordinator CreateInstance() => _shared;
}

[Trait("Category", "Integration")]
public sealed class PostgresClusterCoordinatorTests : ClusterCoordinatorTests, IDisposable
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("CABINET_TEST_POSTGRES_CONNSTR");

    // Each has its own connection pool, which would otherwise stay open
    private readonly List<PostgresClusterCoordinator> _created = new();

    protected override bool IsAvailable => !string.IsNullOrEmpty(ConnectionString);

    protected override IClusterCoordinator CreateInstance()
    {
        var coordinator = new PostgresClusterCoordinator(ConnectionString!, NullLogger<PostgresClusterCoordinator>.Instance);
        _created.Add(coordinator);
        return coordinator;
    }

    public void Dispose()
    {
        foreach (var coordinator in _created)
        {
            coordinator.Dispose();
        }
    }

    [SkippableFact]
    public async Task Heartbeats_CountTheRunningInstances()
    {
        Skip.IfNot(IsAvailable);

        // Its own database, since other tests' instances heartbeat into the shared one
        var database = $"cabinet_cluster_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin);
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var isolated = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = database }.ConnectionString;
            using var first = new PostgresClusterCoordinator(isolated, NullLogger<PostgresClusterCoordinator>.Instance);
            using var second = new PostgresClusterCoordinator(isolated, NullLogger<PostgresClusterCoordinator>.Instance);

            (await first.HeartbeatAsync()).Should().Be(1);
            (await second.HeartbeatAsync()).Should().Be(2);
            await first.HeartbeatAsync();
            first.LiveInstances.Should().Be(2);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [SkippableFact]
    public async Task Threshold_CountsEventsReceivedByDifferentInstances_AndFiresOnce()
    {
        Skip.IfNot(IsAvailable);
        var history = new InMemoryAlertHistoryStorage();
        var documents = new InMemoryDocumentStore();
        var rules = new DetectionRuleStore(documents, new ConfigurationBuilder().Build());
        var rule = new DetectionRule { Name = $"cluster-{Guid.NewGuid():N}", Pattern = "failed", Threshold = 4, WindowSeconds = 300 };
        await rules.SaveAsync(rule);

        DetectionEngine Instance() => new(
            rules,
            new AlertDispatcher(history, Mock.Of<IAlertService>(), NullLogger<AlertDispatcher>.Instance),
            NullLogger<DetectionEngine>.Instance,
            CreateInstance());
        var first = Instance();
        var second = Instance();
        var start = DateTime.UtcNow;
        LogEvent Event(int i) => new() { RawLine = $"login failed {i}", Timestamp = start.AddSeconds(i) };

        // Two events each: neither instance alone reaches the threshold
        await first.CheckAsync([Event(0), Event(1)]);
        await second.CheckAsync([Event(2)]);
        (await history.GetAlertsAsync()).Should().BeEmpty();

        await second.CheckAsync([Event(3)]);
        await first.CheckAsync([Event(4)]);

        (await history.GetAlertsAsync()).Should().ContainSingle().Which.SearchName.Should().Be(rule.Name);
    }
}

public sealed class KeyRotationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cabinet-rotation-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private async Task<EncryptionService> CreateAsync(string current, params string[] previous)
    {
        var service = new EncryptionService(
            new EncryptionConfiguration
            {
                Enabled = true,
                KeyFilePath = Path.Combine(_directory, current),
                PreviousKeyFilePaths = previous.Select(name => Path.Combine(_directory, name)).ToList(),
            },
            NullLogger<EncryptionService>.Instance);
        await service.InitializeAsync();
        return service;
    }

    [Fact]
    public async Task AfterRotation_OldDataStaysReadable_AndNewDataUsesTheNewKey()
    {
        var before = await CreateAsync("old.key");
        var old = before.Encrypt("written before");

        var after = await CreateAsync("new.key", "old.key");
        var fresh = after.Encrypt("written after");

        after.Decrypt(old).Should().Be("written before");
        after.Decrypt(fresh).Should().Be("written after");
        fresh.Should().Contain(after.CurrentKeyId).And.NotContain(before.CurrentKeyId);
    }

    [Fact]
    public async Task DataFromAKeyNoLongerConfigured_SaysWhichKeyIsMissing()
    {
        var before = await CreateAsync("old.key");
        var old = before.Encrypt("secret");

        var after = await CreateAsync("new.key");

        var decrypt = () => after.Decrypt(old);
        decrypt.Should().Throw<System.Security.Cryptography.CryptographicException>().WithMessage($"*{before.CurrentKeyId}*");
    }

    [Fact]
    public async Task ValuesFromBeforeKeyIds_StillDecrypt()
    {
        var service = await CreateAsync("old.key");
        var versioned = service.Encrypt("legacy");

        // The old format was the same bytes with only "enc:" in front
        var legacy = "enc:" + versioned[(versioned.LastIndexOf(':') + 1)..];
        var rotated = await CreateAsync("new.key", "old.key");

        rotated.Decrypt(legacy).Should().Be("legacy");
    }

    [Fact]
    public async Task ApiKey_PastItsExpiry_IsRefused_AndAFutureOneWorks()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApiKeys:Keys:0:Key"] = "old-key",
            ["ApiKeys:Keys:0:ExpiresAt"] = "2000-01-01T00:00:00Z",
            ["ApiKeys:Keys:1:Key"] = "new-key",
            ["ApiKeys:Keys:1:ExpiresAt"] = "2999-01-01T00:00:00Z",
        }).Build();
        var keys = new ApiKeyService(configuration, NullLogger<ApiKeyService>.Instance);

        (await keys.ValidateKeyAsync("old-key")).IsValid.Should().BeFalse();
        (await keys.ValidateKeyAsync("new-key")).IsValid.Should().BeTrue();
    }

    [Fact]
    public void ApiKey_WithAnUnreadableExpiry_FailsAtStartup()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApiKeys:Keys:0:Key"] = "key",
            ["ApiKeys:Keys:0:ExpiresAt"] = "next tuesday",
        }).Build();

        var create = () => new ApiKeyService(configuration, NullLogger<ApiKeyService>.Instance);

        create.Should().Throw<InvalidOperationException>().WithMessage("*ExpiresAt*");
    }
}
