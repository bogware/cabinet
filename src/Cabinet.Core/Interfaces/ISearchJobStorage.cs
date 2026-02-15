using Cabinet.Core.Models;

namespace Cabinet.Core.Interfaces;

/// <summary>
/// Storage for async search jobs. Manages job lifecycle and result persistence.
/// </summary>
public interface ISearchJobStorage
{
    /// <summary>Initialize storage</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Create a new search job</summary>
    Task<SearchJob> CreateAsync(SearchJob job, CancellationToken cancellationToken = default);

    /// <summary>Get a job by ID</summary>
    Task<SearchJob?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Get all jobs, optionally filtered by owner</summary>
    Task<IReadOnlyList<SearchJob>> GetAllAsync(string? owner = null, CancellationToken cancellationToken = default);

    /// <summary>Update job status, progress, and results</summary>
    Task UpdateAsync(SearchJob job, CancellationToken cancellationToken = default);

    /// <summary>Delete a job and its results</summary>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Delete expired jobs (past TTL)</summary>
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default);
}
