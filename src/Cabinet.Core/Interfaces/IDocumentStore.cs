namespace Cabinet.Core.Interfaces;

/// <summary>
/// Stores small JSON documents by collection and ID. State that every API instance has to share
/// lives here, so it follows the deployment's metadata database: PostgreSQL when several instances
/// run, SQLite for a single instance.
/// </summary>
public interface IDocumentStore
{
    /// <summary>Creates the store if needed.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces a document.</summary>
    Task PutAsync<T>(string collection, string id, T document, CancellationToken cancellationToken = default);

    /// <summary>Reads a document, or returns the default if there's none with that ID.</summary>
    Task<T?> GetAsync<T>(string collection, string id, CancellationToken cancellationToken = default);

    /// <summary>Deletes a document, returning whether there was one.</summary>
    Task<bool> DeleteAsync(string collection, string id, CancellationToken cancellationToken = default);

    /// <summary>Reads every document in a collection.</summary>
    Task<IReadOnlyList<T>> ListAsync<T>(string collection, CancellationToken cancellationToken = default);
}

/// <summary>
/// Whether this instance should run the work only one instance may do: scheduled searches,
/// retention, search jobs and data model builds.
/// </summary>
public interface ILeaderLease
{
    /// <summary>True while this instance holds the lease.</summary>
    bool IsLeader { get; }
}

/// <summary>
/// For backends that serve one instance, which is always the leader.
/// </summary>
public sealed class SingleInstanceLease : ILeaderLease
{
    /// <inheritdoc />
    public bool IsLeader => true;
}
