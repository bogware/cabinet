using System.Collections.Concurrent;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;

namespace Cabinet.Storage;

/// <summary>
/// In-memory dashboard storage for testing and ephemeral deployments
/// </summary>
public sealed class InMemoryDashboardStorage : IDashboardStorage
{
    private readonly ConcurrentDictionary<string, Dashboard> _dashboards = new();

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<Dashboard> SaveAsync(Dashboard dashboard, CancellationToken cancellationToken = default)
    {
        _dashboards[dashboard.Id] = dashboard;
        return Task.FromResult(dashboard);
    }

    public Task<Dashboard?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        _dashboards.TryGetValue(id, out var dashboard);
        return Task.FromResult(dashboard);
    }

    public Task<IReadOnlyList<Dashboard>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Dashboard> result = _dashboards.Values
            .OrderByDescending(d => d.UpdatedAt)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_dashboards.TryRemove(id, out _));
    }
}
