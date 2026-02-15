using Cabinet.Core.Models;
using Cabinet.Storage;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Storage;

public class InMemoryScheduledSearchStorageTests
{
    private readonly InMemoryScheduledSearchStorage _storage = new();

    [Fact]
    public async Task SaveAndRetrieve()
    {
        // Arrange
        var search = new ScheduledSearch
        {
            Name = "Error Monitor",
            Query = "find * | where severity >= 4 | stats count",
            CronExpression = "*/5 * * * *",
            Description = "Monitor errors every 5 minutes",
            Enabled = true,
            TimeRange = "-5m",
            CreatedBy = "admin"
        };

        // Act
        var saved = await _storage.SaveAsync(search);
        var retrieved = await _storage.GetByIdAsync(saved.Id);

        // Assert
        retrieved.Should().NotBeNull();
        retrieved!.Id.Should().Be(saved.Id);
        retrieved.Name.Should().Be("Error Monitor");
        retrieved.Query.Should().Be("find * | where severity >= 4 | stats count");
        retrieved.CronExpression.Should().Be("*/5 * * * *");
        retrieved.Description.Should().Be("Monitor errors every 5 minutes");
        retrieved.Enabled.Should().BeTrue();
        retrieved.TimeRange.Should().Be("-5m");
        retrieved.CreatedBy.Should().Be("admin");
    }

    [Fact]
    public async Task GetByIdReturnsNullForNonExistent()
    {
        // Act
        var result = await _storage.GetByIdAsync("nonexistent-id");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAllReturnsAll()
    {
        // Arrange
        var search1 = new ScheduledSearch { Name = "Search1", Query = "find *", CronExpression = "*/5 * * * *" };
        var search2 = new ScheduledSearch { Name = "Search2", Query = "find errors", CronExpression = "*/10 * * * *" };
        var search3 = new ScheduledSearch { Name = "Search3", Query = "find warnings", CronExpression = "*/15 * * * *" };

        await _storage.SaveAsync(search1);
        await _storage.SaveAsync(search2);
        await _storage.SaveAsync(search3);

        // Act
        var all = await _storage.GetAllAsync();

        // Assert
        all.Should().HaveCount(3);
        all.Should().Contain(s => s.Name == "Search1");
        all.Should().Contain(s => s.Name == "Search2");
        all.Should().Contain(s => s.Name == "Search3");
    }

    [Fact]
    public async Task UpdateExisting()
    {
        // Arrange
        var search = new ScheduledSearch
        {
            Name = "Original Name",
            Query = "find *",
            CronExpression = "*/5 * * * *",
            Enabled = true
        };
        var saved = await _storage.SaveAsync(search);

        // Act - modify and save again
        saved.Name = "Updated Name";
        saved.Query = "find errors";
        saved.Enabled = false;
        await _storage.SaveAsync(saved);

        var retrieved = await _storage.GetByIdAsync(saved.Id);

        // Assert
        retrieved.Should().NotBeNull();
        retrieved!.Name.Should().Be("Updated Name");
        retrieved.Query.Should().Be("find errors");
        retrieved.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteExisting()
    {
        // Arrange
        var search = new ScheduledSearch
        {
            Name = "ToDelete",
            Query = "find *",
            CronExpression = "*/5 * * * *"
        };
        var saved = await _storage.SaveAsync(search);

        // Act
        var deleteResult = await _storage.DeleteAsync(saved.Id);
        var getResult = await _storage.GetByIdAsync(saved.Id);

        // Assert
        deleteResult.Should().BeTrue();
        getResult.Should().BeNull();
    }
}
