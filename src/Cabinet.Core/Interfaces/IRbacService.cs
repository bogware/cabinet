using Cabinet.Core.Models;

namespace Cabinet.Core.Interfaces;

/// <summary>
/// Service for role-based access control evaluation.
/// Implementations may read principals from configuration, database, or external identity providers.
/// </summary>
public interface IRbacService
{
    /// <summary>
    /// Get the RBAC principal for an API key hash
    /// </summary>
    Task<RbacPrincipal?> GetPrincipalAsync(string apiKeyHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a principal has a specific capability for an index
    /// </summary>
    Task<bool> HasCapabilityAsync(string apiKeyHash, Capability capability, string indexName = "main", CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all indexes a principal is permitted to access
    /// </summary>
    Task<IReadOnlyList<string>> GetPermittedIndexesAsync(string apiKeyHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// List all principals. Returns empty list if not supported by implementation.
    /// </summary>
    Task<IReadOnlyList<RbacPrincipal>> ListPrincipalsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RbacPrincipal>>(Array.Empty<RbacPrincipal>());

    /// <summary>
    /// Create or update a principal. Returns false if not supported by implementation.
    /// </summary>
    Task<bool> SavePrincipalAsync(RbacPrincipal principal, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <summary>
    /// Delete a principal by API key hash. Returns false if not found or not supported.
    /// </summary>
    Task<bool> DeletePrincipalAsync(string apiKeyHash, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}
