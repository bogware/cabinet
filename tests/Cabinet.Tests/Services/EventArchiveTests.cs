using Cabinet.API.Services;
using Cabinet.API.Services.Archive;
using Cabinet.Core.Configuration;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cabinet.Tests.Services;

public sealed class EventArchiveTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cabinet-archive-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static async Task<InMemoryStorage> StorageAsync(int days, int perDay)
    {
        var storage = new InMemoryStorage();
        var now = DateTime.UtcNow;
        var events = new List<LogEvent>();
        for (var d = 0; d < days; d++)
        {
            for (var i = 0; i < perDay; i++)
            {
                var e = new LogEvent { RawLine = $"day {d} event {i}", Index = "web", Timestamp = now.AddDays(-d).AddMinutes(-i), Host = "web-1" };
                e.SetField("Status", 200 + i % 3);
                events.Add(e);
            }
        }

        await storage.StoreBatchAsync(events);
        return storage;
    }

    [Fact]
    public async Task Retention_ArchivesThenDeletes_AndRehydrateBringsThemBack()
    {
        var storage = await StorageAsync(days: 10, perDay: 7000);
        var documents = new InMemoryDocumentStore();
        await documents.PutAsync(RetentionService.PoliciesCollection, "web", new RetentionPolicyDto { Index = "web", MaxAgeDays = 5, ArchiveBeforeDelete = true });
        var archiver = new EventArchiver(new DirectoryEventArchive(_directory), NullLogger<EventArchiver>.Instance);
        var retention = new RetentionService(NullLogger<RetentionService>.Instance, storage, documents, Options.Create(new CabinetConfiguration()), new ConfigurationBuilder().Build(), archiver: archiver);

        var before = await storage.GetCountAsync(new QueryContext { Index = "web" });
        await retention.ApplyRetentionPoliciesAsync();
        var after = await storage.GetCountAsync(new QueryContext { Index = "web" });

        after.Should().BeLessThan(before);
        var archived = before - after;
        var chunks = await new DirectoryEventArchive(_directory).ListAsync("web");
        chunks.Should().NotBeEmpty().And.OnlyContain(c => c.Index == "web");

        var restored = await archiver.RehydrateAsync(storage, "web", DateTime.UtcNow.AddDays(-30), DateTime.UtcNow, "web-restored", CancellationToken.None);

        restored.Should().Be(archived);
        var back = await storage.QueryAsync(new QueryContext { Index = "web-restored", Limit = 1 }).FirstAsync();
        Convert.ToInt64(back.Fields["Status"]).Should().BeOneOf(200, 201, 202);
        back.Fields.ContainsKey("status").Should().BeTrue("field names stay case-insensitive");
    }

    [Fact]
    public async Task ArchivingWithNoArchive_KeepsTheEvents()
    {
        var storage = await StorageAsync(days: 10, perDay: 10);
        var documents = new InMemoryDocumentStore();
        await documents.PutAsync(RetentionService.PoliciesCollection, "web", new RetentionPolicyDto { Index = "web", MaxAgeDays = 5, ArchiveBeforeDelete = true });
        var retention = new RetentionService(NullLogger<RetentionService>.Instance, storage, documents, Options.Create(new CabinetConfiguration()), new ConfigurationBuilder().Build());

        await retention.ApplyRetentionPoliciesAsync();

        (await storage.GetCountAsync(new QueryContext { Index = "web" })).Should().Be(100);
    }

    [Fact]
    public void Keys_CantLeaveTheArchive()
    {
        var archive = new DirectoryEventArchive(_directory);

        var act = () => archive.OpenAsync("../../etc/passwd");

        act.Should().ThrowAsync<ArgumentException>();
    }
}
