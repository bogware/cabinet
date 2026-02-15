using System.Collections.Concurrent;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;

namespace Cabinet.Storage;

/// <summary>
/// In-memory implementation of search job storage for testing and single-instance use.
/// </summary>
public sealed class InMemorySearchJobStorage : ISearchJobStorage
{
    private readonly ConcurrentDictionary<string, SearchJob> _jobs = new();

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<SearchJob> CreateAsync(SearchJob job, CancellationToken cancellationToken = default)
    {
        _jobs[job.Id] = job;
        return Task.FromResult(job);
    }

    public Task<SearchJob?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        _jobs.TryGetValue(id, out var job);
        return Task.FromResult(job);
    }

    public Task<IReadOnlyList<SearchJob>> GetAllAsync(string? owner = null, CancellationToken cancellationToken = default)
    {
        var snapshot = _jobs.Values.ToList();
        if (!string.IsNullOrEmpty(owner))
            snapshot = snapshot.Where(j => j.Owner == owner).ToList();

        IReadOnlyList<SearchJob> result = snapshot
            .OrderByDescending(j => j.CreatedAt)
            .ToList();
        return Task.FromResult(result);
    }

    public Task UpdateAsync(SearchJob job, CancellationToken cancellationToken = default)
    {
        _jobs[job.Id] = job;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_jobs.TryRemove(id, out _));
    }

    public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var purged = 0;
        foreach (var kvp in _jobs)
        {
            var job = kvp.Value;
            if (job.Status is SearchJobStatus.Completed or SearchJobStatus.Failed or SearchJobStatus.Cancelled)
            {
                var expiry = (job.CompletedAt ?? job.CreatedAt) + job.Ttl;
                if (now > expiry)
                {
                    if (_jobs.TryRemove(kvp.Key, out _))
                        purged++;
                }
            }
        }
        return Task.FromResult(purged);
    }
}
