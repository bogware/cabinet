using System.Collections.Concurrent;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;

namespace Cabinet.Storage;

/// <summary>
/// In-memory scheduled search storage for testing and ephemeral deployments
/// </summary>
public sealed class InMemoryScheduledSearchStorage : IScheduledSearchStorage
{
    private readonly ConcurrentDictionary<string, ScheduledSearch> _searches = new();

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ScheduledSearch> SaveAsync(ScheduledSearch search, CancellationToken cancellationToken = default)
    {
        _searches[search.Id] = search;
        return Task.FromResult(search);
    }

    public Task<ScheduledSearch?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        _searches.TryGetValue(id, out var search);
        return Task.FromResult(search);
    }

    public Task<IReadOnlyList<ScheduledSearch>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ScheduledSearch> result = _searches.Values
            .OrderByDescending(s => s.CreatedAt)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_searches.TryRemove(id, out _));
    }
}
