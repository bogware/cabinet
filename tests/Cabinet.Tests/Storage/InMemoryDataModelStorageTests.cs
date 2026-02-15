using Cabinet.Core.Models;
using Cabinet.Storage;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Storage;

public class InMemoryDataModelStorageTests
{
    private readonly InMemoryDataModelStorage _storage = new();

    [Fact]
    public async Task InitializeAsync_CompletesWithoutError()
    {
        await _storage.InitializeAsync();
    }

    [Fact]
    public async Task SaveAndGetModel_RoundTrip_PreservesAllProperties()
    {
        var model = CreateTestModel("Test Model", "find errors", new List<string> { "host", "severity" });

        await _storage.SaveModelAsync(model);
        var retrieved = await _storage.GetModelAsync(model.Id);

        retrieved.Should().NotBeNull();
        retrieved!.Id.Should().Be(model.Id);
        retrieved.Name.Should().Be("Test Model");
        retrieved.BaseQuery.Should().Be("find errors");
        retrieved.Fields.Should().BeEquivalentTo(new[] { "host", "severity" });
        retrieved.AccelerationEnabled.Should().BeTrue();
        retrieved.RetentionDays.Should().Be(90);
    }

    [Fact]
    public async Task GetModelAsync_NonExistent_ReturnsNull()
    {
        var result = await _storage.GetModelAsync("nonexistent");
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAllModelsAsync_MultipleModels_ReturnsAll()
    {
        var m1 = CreateTestModel("Alpha Model", "find *");
        var m2 = CreateTestModel("Beta Model", "find errors");
        var m3 = CreateTestModel("Gamma Model", "find warnings");

        await _storage.SaveModelAsync(m1);
        await _storage.SaveModelAsync(m2);
        await _storage.SaveModelAsync(m3);

        var all = await _storage.GetAllModelsAsync();
        all.Should().HaveCount(3);
        all.Select(m => m.Name).Should().BeEquivalentTo(new[] { "Alpha Model", "Beta Model", "Gamma Model" });
    }

    [Fact]
    public async Task DeleteModelAsync_ExistingModel_RemovesModelAndSummaries()
    {
        var model = CreateTestModel("Delete Me", "find *");
        await _storage.SaveModelAsync(model);
        await _storage.SaveSummariesAsync(model.Id, new List<DataModelSummary>
        {
            new() { TimeBucket = DateTime.UtcNow, Count = 100, FieldValues = new() { { "host", "web-01" } } }
        });

        var deleted = await _storage.DeleteModelAsync(model.Id);

        deleted.Should().BeTrue();
        (await _storage.GetModelAsync(model.Id)).Should().BeNull();
        (await _storage.GetSummariesAsync(model.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteModelAsync_NonExistent_ReturnsFalse()
    {
        var result = await _storage.DeleteModelAsync("nonexistent");
        result.Should().BeFalse();
    }

    [Fact]
    public async Task SaveAndGetSummaries_RoundTrip_PreservesData()
    {
        var model = CreateTestModel("Summary Model", "find *", new List<string> { "host" });
        await _storage.SaveModelAsync(model);

        var bucket1 = new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var bucket2 = new DateTime(2024, 1, 1, 11, 0, 0, DateTimeKind.Utc);

        var summaries = new List<DataModelSummary>
        {
            new() { TimeBucket = bucket1, Count = 50, FieldValues = new() { { "host", "web-01" } } },
            new() { TimeBucket = bucket2, Count = 75, FieldValues = new() { { "host", "web-02" } } }
        };

        await _storage.SaveSummariesAsync(model.Id, summaries);
        var retrieved = await _storage.GetSummariesAsync(model.Id);

        retrieved.Should().HaveCount(2);
        retrieved[0].TimeBucket.Should().Be(bucket1);
        retrieved[0].Count.Should().Be(50);
        retrieved[0].FieldValues["host"].Should().Be("web-01");
        retrieved[1].Count.Should().Be(75);
        retrieved[1].FieldValues["host"].Should().Be("web-02");
    }

    [Fact]
    public async Task SaveSummaries_ReplacesExisting()
    {
        var model = CreateTestModel("Replace Test", "find *");
        await _storage.SaveModelAsync(model);

        var original = new List<DataModelSummary>
        {
            new() { TimeBucket = DateTime.UtcNow, Count = 10, FieldValues = new() }
        };
        await _storage.SaveSummariesAsync(model.Id, original);

        var replacement = new List<DataModelSummary>
        {
            new() { TimeBucket = DateTime.UtcNow, Count = 999, FieldValues = new() },
            new() { TimeBucket = DateTime.UtcNow.AddHours(1), Count = 500, FieldValues = new() }
        };
        await _storage.SaveSummariesAsync(model.Id, replacement);

        var retrieved = await _storage.GetSummariesAsync(model.Id);
        retrieved.Should().HaveCount(2);
        retrieved[0].Count.Should().Be(999);
        retrieved[1].Count.Should().Be(500);
    }

    [Fact]
    public async Task GetSummariesAsync_NoSummaries_ReturnsEmptyList()
    {
        var result = await _storage.GetSummariesAsync("nonexistent");
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateModel_SaveTwice_UpdatesProperties()
    {
        var model = CreateTestModel("Original Name", "find *");
        await _storage.SaveModelAsync(model);

        model.Name = "Updated Name";
        model.AccelerationEnabled = false;
        await _storage.SaveModelAsync(model);

        var retrieved = await _storage.GetModelAsync(model.Id);
        retrieved.Should().NotBeNull();
        retrieved!.Name.Should().Be("Updated Name");
        retrieved.AccelerationEnabled.Should().BeFalse();
    }

    private static DataModel CreateTestModel(
        string name,
        string baseQuery,
        List<string>? fields = null)
    {
        return new DataModel
        {
            Name = name,
            BaseQuery = baseQuery,
            Fields = fields ?? new List<string> { "host" },
            BucketSpan = TimeSpan.FromHours(1),
            AccelerationEnabled = true,
            RetentionDays = 90
        };
    }
}
