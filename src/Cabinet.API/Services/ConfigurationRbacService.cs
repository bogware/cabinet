using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.API.Authentication;

namespace Cabinet.API.Services;

/// <summary>
/// Configuration-based RBAC service that maps API key tiers to system roles.
/// This is the default implementation — production deployments may use a database-backed service.
/// </summary>
public sealed class ConfigurationRbacService : IRbacService
{
    private readonly IApiKeyService _apiKeyService;

    public ConfigurationRbacService(IApiKeyService apiKeyService)
    {
        _apiKeyService = apiKeyService;
    }

    public async Task<RbacPrincipal?> GetPrincipalAsync(string apiKeyHash, CancellationToken cancellationToken = default)
    {
        // Look up the key info from active keys by matching the principal ID
        var activeKeys = await _apiKeyService.GetActiveKeysAsync();
        var keyInfo = activeKeys.FirstOrDefault(k => k.Id == apiKeyHash || k.Key == apiKeyHash);
        if (keyInfo == null)
            return null;

        var role = MapTierToRole(keyInfo.Tier);
        var principal = new RbacPrincipal
        {
            Id = apiKeyHash,
            DisplayName = keyInfo.ClientName,
            Role = role,
        };

        // For agents and senders, which shouldn't be able to read what everyone sends
        if (keyInfo.Tier.Equals("Ingest", StringComparison.OrdinalIgnoreCase))
        {
            principal.IndexPermissions = [new IndexPermission { IndexPattern = "*", Capabilities = Capability.Ingest }];

            // Not the fleet's host names and versions, or every index's retention: a key copied to
            // every sending host is the least trusted credential there is
            principal.WithheldCapabilities = Capability.SystemHealth;
        }

        return principal;
    }

    public async Task<bool> HasCapabilityAsync(string apiKeyHash, Capability capability, string indexName = "main", CancellationToken cancellationToken = default)
    {
        var principal = await GetPrincipalAsync(apiKeyHash, cancellationToken);
        return principal?.HasCapability(capability, indexName) ?? false;
    }

    public async Task<IReadOnlyList<string>> GetPermittedIndexesAsync(string apiKeyHash, CancellationToken cancellationToken = default)
    {
        var principal = await GetPrincipalAsync(apiKeyHash, cancellationToken);
        if (principal == null)
            return Array.Empty<string>();

        return principal.IndexesWith(Capability.Search) ?? (IReadOnlyList<string>)["*"];
    }

    public async Task<IReadOnlyList<RbacPrincipal>> ListPrincipalsAsync(CancellationToken cancellationToken = default)
    {
        var keys = await _apiKeyService.GetActiveKeysAsync();
        return keys
            .Select(key => new RbacPrincipal { Id = key.Id, DisplayName = key.ClientName, Role = MapTierToRole(key.Tier) })
            .ToList();
    }

    private static SystemRole MapTierToRole(string tier) => tier.ToLowerInvariant() switch
    {
        "admin" => SystemRole.SuperAdmin,
        "enterprise" => SystemRole.Admin,
        "professional" => SystemRole.Analyst,
        "basic" => SystemRole.Viewer,
        _ => SystemRole.Viewer
    };
}
