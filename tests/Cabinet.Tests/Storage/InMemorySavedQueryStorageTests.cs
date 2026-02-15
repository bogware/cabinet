using Cabinet.Core.Models;
using Cabinet.Storage;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Storage;

public class InMemorySavedQueryStorageTests
{
    private readonly InMemorySavedQueryStorage _storage = new();

    [Fact]
    public async Task SaveAndRetrieve()
    {
        // Arrange
        var query = new SavedQuery
        {
            Name = "Test Query",
            Query = "find * | where severity >= 4",
            Description = "Find all errors",
            CreatedBy = "testuser"
        };

        // Act
        var saved = await _storage.SaveAsync(query);
        var retrieved = await _storage.GetByIdAsync(saved.Id);

        // Assert
        retrieved.Should().NotBeNull();
        retrieved!.Id.Should().Be(saved.Id);
        retrieved.Name.Should().Be("Test Query");
        retrieved.Query.Should().Be("find * | where severity >= 4");
        retrieved.Description.Should().Be("Find all errors");
        retrieved.CreatedBy.Should().Be("testuser");
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
        var query1 = new SavedQuery { Name = "Query1", Query = "find *" };
        var query2 = new SavedQuery { Name = "Query2", Query = "find errors" };
        var query3 = new SavedQuery { Name = "Query3", Query = "find warnings" };

        await _storage.SaveAsync(query1);
        await _storage.SaveAsync(query2);
        await _storage.SaveAsync(query3);

        // Act
        var all = await _storage.GetAllAsync();

        // Assert
        all.Should().HaveCount(3);
        all.Should().Contain(q => q.Name == "Query1");
        all.Should().Contain(q => q.Name == "Query2");
        all.Should().Contain(q => q.Name == "Query3");
    }

    [Fact]
    public async Task DeleteExisting()
    {
        // Arrange
        var query = new SavedQuery { Name = "ToDelete", Query = "find *" };
        var saved = await _storage.SaveAsync(query);

        // Act
        var deleteResult = await _storage.DeleteAsync(saved.Id);
        var getResult = await _storage.GetByIdAsync(saved.Id);

        // Assert
        deleteResult.Should().BeTrue();
        getResult.Should().BeNull();
    }

    [Fact]
    public async Task DeleteNonExistentReturnsFalse()
    {
        // Act
        var result = await _storage.DeleteAsync("nonexistent-id");

        // Assert
        result.Should().BeFalse();
    }
}
