using Cabinet.API.Services;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query;
using Cabinet.Query.Bql;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Services;

public sealed class DataModelAccelerationTests
{
    private sealed class Lease(bool leader) : ILeaderLease
    {
        public bool IsLeader => leader;
    }

    private static readonly DateTime Base = DateTime.UtcNow.AddHours(-2).Date.AddHours(DateTime.UtcNow.AddHours(-2).Hour);

    private static async Task<(DataModelAccelerationService Service, InMemoryDataModelStorage Store)> CreateAsync(bool leader = true, int events = 0)
    {
        var storage = new InMemoryStorage();
        var batch = Enumerable.Range(0, events).Select(i => new LogEvent
        {
            RawLine = $"event {i}",
            Host = i % 2 == 0 ? "web-1" : "web-2",
            Timestamp = Base.AddMinutes(i % 30),
        });
        await storage.StoreBatchAsync(batch);

        var services = new ServiceCollection();
        services.AddSingleton<ILogStorage>(storage);
        services.AddSingleton<IQueryEngine>(new BqlQueryEngine(storage, new BqlParser(), new BqlOptimizer(), NullLogger<BqlQueryEngine>.Instance));
        var provider = services.BuildServiceProvider();

        var store = new InMemoryDataModelStorage();
        var service = new DataModelAccelerationService(
            provider.GetRequiredService<IServiceScopeFactory>(), store, NullLogger<DataModelAccelerationService>.Instance, new Lease(leader));
        return (service, store);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(90)]
    [InlineData(1440)]
    public void BucketStart_WorksForSpansUnderAnHourAndOver(int minutes)
    {
        var span = TimeSpan.FromMinutes(minutes);
        var time = new DateTime(2026, 3, 4, 5, 37, 12, DateTimeKind.Utc);

        var bucket = DataModelAccelerationService.BucketStart(time, span);

        bucket.Should().BeOnOrBefore(time);
        (time - bucket).Should().BeLessThan(span);
        (bucket.Ticks % span.Ticks).Should().Be(0);
    }

    [Fact]
    public async Task Rebuild_WithFiveMinuteBuckets_CountsEveryEvent()
    {
        // Buckets under an hour used to divide by zero; the engine now aggregates every event
        var (service, _) = await CreateAsync(events: 3000);
        var model = await service.RegisterModelAsync(new DataModel
        {
            Name = "hosts", BaseQuery = "find *", Fields = ["host"], BucketSpan = TimeSpan.FromMinutes(5),
        });

        (await service.RebuildModelAsync(model, CancellationToken.None)).Should().BeTrue();

        var byHost = await service.ExecuteTstatsAsync(model.Id, groupByFields: ["host"]);
        byHost.Events.Cast<IDictionary<string, object?>>()
            .ToDictionary(r => (string)r["host"]!, r => Convert.ToInt64(r["count"]))
            .Should().BeEquivalentTo(new Dictionary<string, long> { ["web-1"] = 1500, ["web-2"] = 1500 });

        var total = (IDictionary<string, object?>)(await service.ExecuteTstatsAsync(model.Id)).Events.Single();
        total["count"].Should().Be(3000L);
    }

    [Fact]
    public async Task NonLeader_DoesNotSaveItsOwnStaleSummaries_ButReadsTheLeaders()
    {
        var (service, store) = await CreateAsync(leader: false, events: 10);
        var model = new DataModel { Name = "m", BaseQuery = "find *", Fields = ["host"] };
        await store.SaveModelAsync(model);
        await store.SaveSummariesAsync(model.Id, [new DataModelSummary { TimeBucket = Base, Count = 42 }]);

        await service.RunOnceAsync(CancellationToken.None);

        (await store.GetSummariesAsync(model.Id)).Single().Count.Should().Be(42);
        var total = (IDictionary<string, object?>)(await service.ExecuteTstatsAsync(model.Id)).Events.Single();
        total["count"].Should().Be(42L);
    }

    [Fact]
    public async Task NonLeader_DropsModelsDeletedElsewhere()
    {
        var (service, store) = await CreateAsync(leader: false);
        var model = await service.RegisterModelAsync(new DataModel { Name = "m", BaseQuery = "find *", Fields = ["host"] });
        await store.DeleteModelAsync(model.Id);

        await service.RunOnceAsync(CancellationToken.None);

        service.GetModel(model.Id).Should().BeNull();
    }

    [Theory]
    [InlineData("host", true)]
    [InlineData("http.status", true)]
    [InlineData("host | delete", false)]
    [InlineData("1abc", false)]
    public void FieldNames_AreChecked(string field, bool valid) =>
        DataModelAccelerationService.IsValidFieldName(field).Should().Be(valid);
}
