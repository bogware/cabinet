using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;

namespace Cabinet.API.Services;

/// <summary>
/// Principals saved through the API, shared by all instances. Anyone without a saved principal
/// falls through to their API key's tier.
/// </summary>
public sealed class StoredRbacService : IRbacService
{
    private const string Principals = "rbac-principals";

    private readonly IDocumentStore _documents;
    private readonly ConfigurationRbacService _fallback;
    private readonly ILogger<StoredRbacService> _logger;

    public StoredRbacService(IDocumentStore documents, ConfigurationRbacService fallback, ILogger<StoredRbacService> logger)
    {
        _documents = documents;
        _fallback = fallback;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RbacPrincipal?> GetPrincipalAsync(string apiKeyHash, CancellationToken cancellationToken = default) =>
        await _documents.GetAsync<RbacPrincipal>(Principals, apiKeyHash, cancellationToken)
        ?? await _fallback.GetPrincipalAsync(apiKeyHash, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> HasCapabilityAsync(string apiKeyHash, Capability capability, string indexName = "main", CancellationToken cancellationToken = default)
    {
        var principal = await GetPrincipalAsync(apiKeyHash, cancellationToken);
        return principal?.HasCapability(capability, indexName) ?? false;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetPermittedIndexesAsync(string apiKeyHash, CancellationToken cancellationToken = default)
    {
        var principal = await GetPrincipalAsync(apiKeyHash, cancellationToken);
        if (principal == null)
        {
            return Array.Empty<string>();
        }

        return principal.IndexesWith(Capability.Search) ?? (IReadOnlyList<string>)["*"];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RbacPrincipal>> ListPrincipalsAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _documents.ListAsync<RbacPrincipal>(Principals, cancellationToken);
        var fromKeys = await _fallback.ListPrincipalsAsync(cancellationToken);
        var storedIds = stored.Select(principal => principal.Id).ToHashSet(StringComparer.Ordinal);
        return stored.Concat(fromKeys.Where(principal => !storedIds.Contains(principal.Id))).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> SavePrincipalAsync(RbacPrincipal principal, CancellationToken cancellationToken = default)
    {
        await _documents.PutAsync(Principals, principal.Id, principal, cancellationToken);
        _logger.LogInformation("Saved RBAC principal {Id} (role: {Role})", principal.Id, principal.Role);
        return true;
    }

    /// <inheritdoc />
    public Task<bool> DeletePrincipalAsync(string apiKeyHash, CancellationToken cancellationToken = default) =>
        _documents.DeleteAsync(Principals, apiKeyHash, cancellationToken);
}
