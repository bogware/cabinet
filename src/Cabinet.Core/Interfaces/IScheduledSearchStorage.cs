using Cabinet.Core.Models;

namespace Cabinet.Core.Interfaces;

/// <summary>
/// Persistent storage for scheduled searches.
/// </summary>
public interface IScheduledSearchStorage
{
    /// <summary>Initialize storage (create tables if needed)</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Save or update a scheduled search</summary>
    Task<ScheduledSearch> SaveAsync(ScheduledSearch search, CancellationToken cancellationToken = default);

    /// <summary>Get a scheduled search by ID</summary>
    Task<ScheduledSearch?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Get all scheduled searches</summary>
    Task<IReadOnlyList<ScheduledSearch>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Delete a scheduled search by ID. Returns true if found and deleted.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}
