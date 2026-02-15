using Cabinet.Core.Models;
using Cabinet.Storage;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Storage;

public class InMemorySearchJobStorageTests
{
    private readonly InMemorySearchJobStorage _storage = new();

    [Fact]
    public async Task CreateAsync_ReturnsJob()
    {
        var job = new SearchJob { Query = "find errors" };
        var result = await _storage.CreateAsync(job);
        result.Should().NotBeNull();
        result.Id.Should().NotBeNullOrEmpty();
        result.Query.Should().Be("find errors");
        result.Status.Should().Be(SearchJobStatus.Queued);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNullForMissing()
    {
        var result = await _storage.GetByIdAsync("nonexistent");
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsStoredJob()
    {
        var job = new SearchJob { Query = "find *" };
        await _storage.CreateAsync(job);

        var result = await _storage.GetByIdAsync(job.Id);
        result.Should().NotBeNull();
        result!.Query.Should().Be("find *");
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAllJobs()
    {
        await _storage.CreateAsync(new SearchJob { Query = "q1", Owner = "user1" });
        await _storage.CreateAsync(new SearchJob { Query = "q2", Owner = "user2" });

        var all = await _storage.GetAllAsync();
        all.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAllAsync_FiltersByOwner()
    {
        await _storage.CreateAsync(new SearchJob { Query = "q1", Owner = "user1" });
        await _storage.CreateAsync(new SearchJob { Query = "q2", Owner = "user2" });

        var filtered = await _storage.GetAllAsync("user1");
        filtered.Should().HaveCount(1);
        filtered[0].Query.Should().Be("q1");
    }

    [Fact]
    public async Task UpdateAsync_PersistsChanges()
    {
        var job = new SearchJob { Query = "find *" };
        await _storage.CreateAsync(job);

        job.Status = SearchJobStatus.Running;
        job.Progress = 50;
        await _storage.UpdateAsync(job);

        var updated = await _storage.GetByIdAsync(job.Id);
        updated!.Status.Should().Be(SearchJobStatus.Running);
        updated.Progress.Should().Be(50);
    }

    [Fact]
    public async Task DeleteAsync_RemovesJob()
    {
        var job = new SearchJob { Query = "find *" };
        await _storage.CreateAsync(job);

        var deleted = await _storage.DeleteAsync(job.Id);
        deleted.Should().BeTrue();

        var result = await _storage.GetByIdAsync(job.Id);
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ReturnsFalseForMissing()
    {
        var deleted = await _storage.DeleteAsync("nonexistent");
        deleted.Should().BeFalse();
    }

    [Fact]
    public async Task PurgeExpiredAsync_RemovesExpiredJobs()
    {
        var expiredJob = new SearchJob
        {
            Query = "find *",
            Status = SearchJobStatus.Completed,
            CompletedAt = DateTime.UtcNow.AddMinutes(-60),
            Ttl = TimeSpan.FromMinutes(30)
        };
        await _storage.CreateAsync(expiredJob);

        var activeJob = new SearchJob
        {
            Query = "find *",
            Status = SearchJobStatus.Running
        };
        await _storage.CreateAsync(activeJob);

        var purged = await _storage.PurgeExpiredAsync();
        purged.Should().Be(1);

        var remaining = await _storage.GetAllAsync();
        remaining.Should().HaveCount(1);
        remaining[0].Id.Should().Be(activeJob.Id);
    }

    [Fact]
    public async Task PurgeExpiredAsync_KeepsNonExpiredCompletedJobs()
    {
        var recentJob = new SearchJob
        {
            Query = "find *",
            Status = SearchJobStatus.Completed,
            CompletedAt = DateTime.UtcNow,
            Ttl = TimeSpan.FromMinutes(30)
        };
        await _storage.CreateAsync(recentJob);

        var purged = await _storage.PurgeExpiredAsync();
        purged.Should().Be(0);
    }
}
