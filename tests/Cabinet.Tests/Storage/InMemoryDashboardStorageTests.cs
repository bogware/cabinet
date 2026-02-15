using Cabinet.Core.Models;
using Cabinet.Storage;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Storage;

public class InMemoryDashboardStorageTests
{
    private readonly InMemoryDashboardStorage _storage = new();

    [Fact]
    public async Task SaveAndRetrieve_StoresDashboard_ReturnsAllProperties()
    {
        // Arrange
        var dashboard = CreateTestDashboard(
            title: "Test Dashboard",
            description: "A test dashboard",
            createdBy: "testuser",
            isShared: true,
            refreshInterval: 60,
            defaultTimeRange: "-1h"
        );
        dashboard.Panels.Add(new DashboardPanel
        {
            Title = "Test Panel",
            Query = "find * | stats count",
            Type = VisualizationType.LineChart,
            Row = 0,
            Column = 0,
            Width = 12,
            Height = 6
        });

        // Act
        var savedDashboard = await _storage.SaveAsync(dashboard);
        var retrievedDashboard = await _storage.GetByIdAsync(savedDashboard.Id);

        // Assert
        retrievedDashboard.Should().NotBeNull();
        retrievedDashboard!.Id.Should().Be(dashboard.Id);
        retrievedDashboard.Title.Should().Be("Test Dashboard");
        retrievedDashboard.Description.Should().Be("A test dashboard");
        retrievedDashboard.CreatedBy.Should().Be("testuser");
        retrievedDashboard.IsShared.Should().BeTrue();
        retrievedDashboard.RefreshIntervalSeconds.Should().Be(60);
        retrievedDashboard.DefaultTimeRange.Should().Be("-1h");
        retrievedDashboard.Panels.Should().HaveCount(1);
        retrievedDashboard.Panels[0].Title.Should().Be("Test Panel");
        retrievedDashboard.Panels[0].Query.Should().Be("find * | stats count");
        retrievedDashboard.Panels[0].Type.Should().Be(VisualizationType.LineChart);
        retrievedDashboard.Panels[0].Width.Should().Be(12);
        retrievedDashboard.Panels[0].Height.Should().Be(6);
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
    public async Task GetAllReturnsAllSaved_ThreeDashboards_ReturnsCorrectCount()
    {
        // Arrange
        var dashboard1 = CreateTestDashboard("Dashboard 1");
        var dashboard2 = CreateTestDashboard("Dashboard 2");
        var dashboard3 = CreateTestDashboard("Dashboard 3");

        await _storage.SaveAsync(dashboard1);
        await Task.Delay(10); // Ensure different UpdatedAt times
        await _storage.SaveAsync(dashboard2);
        await Task.Delay(10);
        await _storage.SaveAsync(dashboard3);

        // Act
        var allDashboards = await _storage.GetAllAsync();

        // Assert
        allDashboards.Should().HaveCount(3);
        allDashboards.Should().Contain(d => d.Title == "Dashboard 1");
        allDashboards.Should().Contain(d => d.Title == "Dashboard 2");
        allDashboards.Should().Contain(d => d.Title == "Dashboard 3");

        // Verify ordering by UpdatedAt descending
        allDashboards[0].UpdatedAt.Should().BeOnOrAfter(allDashboards[1].UpdatedAt);
        allDashboards[1].UpdatedAt.Should().BeOnOrAfter(allDashboards[2].UpdatedAt);
    }

    [Fact]
    public async Task UpdateExisting_ModifyAndSave_VerifiesUpdatedProperties()
    {
        // Arrange
        var dashboard = CreateTestDashboard("Original Title", "Original Description");
        await _storage.SaveAsync(dashboard);

        // Act
        dashboard.Title = "Updated Title";
        dashboard.Description = "Updated Description";
        dashboard.IsShared = true;
        dashboard.RefreshIntervalSeconds = 120;
        dashboard.UpdatedAt = DateTime.UtcNow;

        await _storage.SaveAsync(dashboard);
        var updatedDashboard = await _storage.GetByIdAsync(dashboard.Id);

        // Assert
        updatedDashboard.Should().NotBeNull();
        updatedDashboard!.Id.Should().Be(dashboard.Id);
        updatedDashboard.Title.Should().Be("Updated Title");
        updatedDashboard.Description.Should().Be("Updated Description");
        updatedDashboard.IsShared.Should().BeTrue();
        updatedDashboard.RefreshIntervalSeconds.Should().Be(120);
    }

    [Fact]
    public async Task DeleteExisting_RemovesDashboard_VerifiesGone()
    {
        // Arrange
        var dashboard = CreateTestDashboard("Dashboard to Delete");
        await _storage.SaveAsync(dashboard);
        var beforeDelete = await _storage.GetByIdAsync(dashboard.Id);
        beforeDelete.Should().NotBeNull();

        // Act
        var deleteResult = await _storage.DeleteAsync(dashboard.Id);
        var afterDelete = await _storage.GetByIdAsync(dashboard.Id);

        // Assert
        deleteResult.Should().BeTrue();
        afterDelete.Should().BeNull();
    }

    [Fact]
    public async Task DeleteNonExistentReturnsFalse()
    {
        // Act
        var result = await _storage.DeleteAsync("nonexistent-id");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ConcurrentSaveAndRead_ParallelOperations_DoesNotThrow()
    {
        // Arrange - seed some initial data
        for (var i = 0; i < 10; i++)
        {
            await _storage.SaveAsync(CreateTestDashboard($"Initial Dashboard {i}"));
        }

        // Act - run concurrent writes and reads
        var tasks = new List<Task>();

        for (var i = 0; i < 10; i++)
        {
            var idx = i;

            // Concurrent saves
            tasks.Add(Task.Run(async () =>
            {
                for (var j = 0; j < 10; j++)
                {
                    var dashboard = CreateTestDashboard($"Concurrent Dashboard {idx}-{j}");
                    await _storage.SaveAsync(dashboard);
                }
            }));

            // Concurrent reads
            tasks.Add(Task.Run(async () =>
            {
                for (var j = 0; j < 10; j++)
                {
                    var all = await _storage.GetAllAsync();
                    all.Should().NotBeNull();
                }
            }));
        }

        // Assert - should not throw any exceptions
        await Task.WhenAll(tasks);

        var finalCount = (await _storage.GetAllAsync()).Count;
        finalCount.Should().BeGreaterThanOrEqualTo(10); // At least the initial 10
    }

    private static Dashboard CreateTestDashboard(
        string title,
        string? description = null,
        string? createdBy = null,
        bool isShared = false,
        int refreshInterval = 0,
        string? defaultTimeRange = null)
    {
        return new Dashboard
        {
            Title = title,
            Description = description,
            CreatedBy = createdBy ?? string.Empty,
            IsShared = isShared,
            RefreshIntervalSeconds = refreshInterval,
            DefaultTimeRange = defaultTimeRange ?? "-24h"
        };
    }
}
