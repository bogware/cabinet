using Cabinet.Core.Models;
using System.Collections.Concurrent;

namespace Cabinet.API.Middleware;

/// <summary>Resolves tenant from authenticated API key and provides index scoping</summary>
public sealed class TenantMiddleware
{
    private readonly RequestDelegate _next;
    private static readonly ConcurrentDictionary<string, Tenant> _tenants = new();

    public TenantMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var apiKeyHash = context.User?.FindFirst("ClientId")?.Value;
        if (apiKeyHash != null)
        {
            var tenant = _tenants.Values.FirstOrDefault(t => t.Enabled && t.ApiKeyHashes.Contains(apiKeyHash));
            if (tenant != null)
            {
                context.Items["Tenant"] = tenant;
                context.Items["TenantIndexPrefix"] = tenant.IndexPrefix;
            }
        }

        await _next(context);
    }

    public static void RegisterTenant(Tenant tenant) => _tenants[tenant.Id] = tenant;

    public static bool RemoveTenant(string id) => _tenants.TryRemove(id, out _);

    public static IReadOnlyList<Tenant> GetTenants() => _tenants.Values.ToList();
}

public static class TenantContextExtensions
{
    public static Tenant? GetTenant(this HttpContext context) =>
        context.Items["Tenant"] as Tenant;

    public static string? GetTenantIndexPrefix(this HttpContext context) =>
        context.Items["TenantIndexPrefix"] as string;

    public static string ScopeIndex(this HttpContext context, string indexName)
    {
        var prefix = context.GetTenantIndexPrefix();
        if (prefix == null) return indexName;

        // Security: always strip any existing prefix before re-applying.
        // This prevents Tenant A from accessing Tenant B's data by requesting
        // an index name that already starts with Tenant B's prefix.
        if (indexName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            indexName = indexName[prefix.Length..];

        return prefix + indexName;
    }
}
