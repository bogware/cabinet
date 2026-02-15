using System.Collections.Concurrent;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;

namespace Cabinet.Storage;

/// <summary>
/// In-memory implementation of saved query storage for testing and development
/// </summary>
public sealed class InMemorySavedQueryStorage : ISavedQueryStorage
{
    private readonly ConcurrentDictionary<string, SavedQuery> _queries = new();

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<SavedQuery> SaveAsync(SavedQuery query, CancellationToken cancellationToken = default)
    {
        _queries[query.Id] = query;
        return Task.FromResult(query);
    }

    public Task<SavedQuery?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        _queries.TryGetValue(id, out var query);
        return Task.FromResult(query);
    }

    public Task<IReadOnlyList<SavedQuery>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SavedQuery> result = _queries.Values
            .OrderByDescending(q => q.CreatedAt)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_queries.TryRemove(id, out _));
    }
}
