using Cabinet.Core.Models;

namespace Cabinet.Core.Interfaces;

/// <summary>
/// Persistent storage for saved queries. Separate from ILogStorage
/// because saved queries are configuration/metadata, not log data.
/// </summary>
public interface ISavedQueryStorage
{
    /// <summary>Initialize storage (create tables if needed)</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Save or update a query</summary>
    Task<SavedQuery> SaveAsync(SavedQuery query, CancellationToken cancellationToken = default);

    /// <summary>Get a saved query by ID</summary>
    Task<SavedQuery?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Get all saved queries, ordered by creation date descending</summary>
    Task<IReadOnlyList<SavedQuery>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Delete a saved query by ID. Returns true if found and deleted.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}
