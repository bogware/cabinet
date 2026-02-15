using Cabinet.Core.Models;
using Cabinet.Storage;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Storage;

public class InMemoryKnowledgeStorageTests
{
    private readonly InMemoryKnowledgeStorage _storage = new();

    [Fact]
    public async Task SaveAndRetrieveLookup()
    {
        // Arrange
        var lookup = new LookupTable
        {
            Name = "users",
            Description = "User database",
            Columns = new List<string> { "user_id", "username", "email" },
            Rows = new List<Dictionary<string, string>>
            {
                new() { ["user_id"] = "1", ["username"] = "alice", ["email"] = "alice@example.com" },
                new() { ["user_id"] = "2", ["username"] = "bob", ["email"] = "bob@example.com" }
            }
        };

        // Act
        var saved = await _storage.SaveLookupAsync(lookup);
        var retrieved = await _storage.GetLookupByIdAsync(saved.Id);

        // Assert
        retrieved.Should().NotBeNull();
        retrieved!.Id.Should().Be(saved.Id);
        retrieved.Name.Should().Be("users");
        retrieved.Description.Should().Be("User database");
        retrieved.Columns.Should().HaveCount(3);
        retrieved.Columns.Should().Contain(new[] { "user_id", "username", "email" });
        retrieved.Rows.Should().HaveCount(2);
        retrieved.Rows[0]["username"].Should().Be("alice");
        retrieved.Rows[1]["username"].Should().Be("bob");
    }

    [Fact]
    public async Task GetLookupByName()
    {
        // Arrange
        var lookup = new LookupTable
        {
            Name = "servers",
            Description = "Server inventory",
            Columns = new List<string> { "hostname", "ip" }
        };
        await _storage.SaveLookupAsync(lookup);

        // Act
        var retrieved = await _storage.GetLookupByNameAsync("servers");

        // Assert
        retrieved.Should().NotBeNull();
        retrieved!.Name.Should().Be("servers");
        retrieved.Description.Should().Be("Server inventory");
    }

    [Fact]
    public async Task GetLookupByNameReturnsNullForNonExistent()
    {
        // Act
        var result = await _storage.GetLookupByNameAsync("nonexistent-lookup");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteLookup()
    {
        // Arrange
        var lookup = new LookupTable { Name = "temp_lookup" };
        var saved = await _storage.SaveLookupAsync(lookup);

        // Act
        var deleteResult = await _storage.DeleteLookupAsync(saved.Id);
        var getResult = await _storage.GetLookupByIdAsync(saved.Id);

        // Assert
        deleteResult.Should().BeTrue();
        getResult.Should().BeNull();
    }

    [Fact]
    public async Task SaveAndRetrieveMacro()
    {
        // Arrange
        var macro = new QueryMacro
        {
            Name = "errors",
            Definition = "severity >= 4",
            Description = "Filter for errors",
            Parameters = new List<string> { "host", "timerange" },
            ValidationRegex = @"^\w+$"
        };

        // Act
        var saved = await _storage.SaveMacroAsync(macro);
        var retrieved = await _storage.GetMacroByIdAsync(saved.Id);

        // Assert
        retrieved.Should().NotBeNull();
        retrieved!.Id.Should().Be(saved.Id);
        retrieved.Name.Should().Be("errors");
        retrieved.Definition.Should().Be("severity >= 4");
        retrieved.Description.Should().Be("Filter for errors");
        retrieved.Parameters.Should().HaveCount(2);
        retrieved.Parameters.Should().Contain(new[] { "host", "timerange" });
        retrieved.ValidationRegex.Should().Be(@"^\w+$");
    }

    [Fact]
    public async Task GetAllMacros()
    {
        // Arrange
        var macro1 = new QueryMacro { Name = "errors", Definition = "severity >= 4" };
        var macro2 = new QueryMacro { Name = "warnings", Definition = "severity >= 3" };

        await _storage.SaveMacroAsync(macro1);
        await _storage.SaveMacroAsync(macro2);

        // Act
        var all = await _storage.GetAllMacrosAsync();

        // Assert
        all.Should().HaveCount(2);
        all.Should().Contain(m => m.Name == "errors");
        all.Should().Contain(m => m.Name == "warnings");
    }

    [Fact]
    public async Task DeleteMacroReturnsFlaseForNonExistent()
    {
        // Act
        var result = await _storage.DeleteMacroAsync("nonexistent-id");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task SaveAndRetrieveAlias()
    {
        // Arrange
        var alias = new FieldAlias
        {
            SourceType = "l4d2",
            OriginalField = "server_id",
            AliasField = "host"
        };

        // Act
        var saved = await _storage.SaveAliasAsync(alias);
        var retrieved = await _storage.GetAliasByIdAsync(saved.Id);

        // Assert
        retrieved.Should().NotBeNull();
        retrieved!.Id.Should().Be(saved.Id);
        retrieved.SourceType.Should().Be("l4d2");
        retrieved.OriginalField.Should().Be("server_id");
        retrieved.AliasField.Should().Be("host");
    }

    [Fact]
    public async Task GetAllAliases()
    {
        // Arrange
        var alias1 = new FieldAlias { OriginalField = "src_ip", AliasField = "source_ip" };
        var alias2 = new FieldAlias { OriginalField = "dst_ip", AliasField = "dest_ip" };

        await _storage.SaveAliasAsync(alias1);
        await _storage.SaveAliasAsync(alias2);

        // Act
        var all = await _storage.GetAllAliasesAsync();

        // Assert
        all.Should().HaveCount(2);
        all.Should().Contain(a => a.OriginalField == "src_ip");
        all.Should().Contain(a => a.OriginalField == "dst_ip");
    }

    [Fact]
    public async Task DeleteAlias()
    {
        // Arrange
        var alias = new FieldAlias { OriginalField = "old_field", AliasField = "new_field" };
        var saved = await _storage.SaveAliasAsync(alias);

        // Act
        var deleteResult = await _storage.DeleteAliasAsync(saved.Id);
        var getResult = await _storage.GetAliasByIdAsync(saved.Id);

        // Assert
        deleteResult.Should().BeTrue();
        getResult.Should().BeNull();
    }
}
