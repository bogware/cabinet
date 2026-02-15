using Cabinet.Core.Models;

namespace Cabinet.Core.Interfaces;

/// <summary>
/// Persistent storage for dashboards.
/// </summary>
public interface IDashboardStorage
{
    /// <summary>Initialize storage (create tables if needed)</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Save or update a dashboard</summary>
    Task<Dashboard> SaveAsync(Dashboard dashboard, CancellationToken cancellationToken = default);

    /// <summary>Get a dashboard by ID</summary>
    Task<Dashboard?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Get all dashboards, ordered by update date descending</summary>
    Task<IReadOnlyList<Dashboard>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Delete a dashboard by ID. Returns true if found and deleted.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}
