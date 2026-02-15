using System.Collections.Concurrent;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;

namespace Cabinet.Storage;

/// <summary>
/// In-memory knowledge storage for testing and ephemeral deployments
/// </summary>
public sealed class InMemoryKnowledgeStorage : IKnowledgeStorage
{
    private readonly ConcurrentDictionary<string, LookupTable> _lookups = new();
    private readonly ConcurrentDictionary<string, QueryMacro> _macros = new();
    private readonly ConcurrentDictionary<string, FieldAlias> _aliases = new();

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    // Lookups
    public Task<LookupTable> SaveLookupAsync(LookupTable lookup, CancellationToken cancellationToken = default)
    {
        _lookups[lookup.Id] = lookup;
        return Task.FromResult(lookup);
    }

    public Task<LookupTable?> GetLookupByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        _lookups.TryGetValue(id, out var lookup);
        return Task.FromResult(lookup);
    }

    public Task<LookupTable?> GetLookupByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var lookup = _lookups.Values.FirstOrDefault(l =>
            string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(lookup);
    }

    public Task<IReadOnlyList<LookupTable>> GetAllLookupsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LookupTable> result = _lookups.Values.OrderByDescending(l => l.UpdatedAt).ToList();
        return Task.FromResult(result);
    }

    public Task<bool> DeleteLookupAsync(string id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_lookups.TryRemove(id, out _));
    }

    // Macros
    public Task<QueryMacro> SaveMacroAsync(QueryMacro macro, CancellationToken cancellationToken = default)
    {
        _macros[macro.Id] = macro;
        return Task.FromResult(macro);
    }

    public Task<QueryMacro?> GetMacroByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        _macros.TryGetValue(id, out var macro);
        return Task.FromResult(macro);
    }

    public Task<QueryMacro?> GetMacroByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var macro = _macros.Values.FirstOrDefault(m =>
            string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(macro);
    }

    public Task<IReadOnlyList<QueryMacro>> GetAllMacrosAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<QueryMacro> result = _macros.Values.OrderByDescending(m => m.CreatedAt).ToList();
        return Task.FromResult(result);
    }

    public Task<bool> DeleteMacroAsync(string id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_macros.TryRemove(id, out _));
    }

    // Aliases
    public Task<FieldAlias> SaveAliasAsync(FieldAlias alias, CancellationToken cancellationToken = default)
    {
        _aliases[alias.Id] = alias;
        return Task.FromResult(alias);
    }

    public Task<FieldAlias?> GetAliasByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        _aliases.TryGetValue(id, out var alias);
        return Task.FromResult(alias);
    }

    public Task<IReadOnlyList<FieldAlias>> GetAllAliasesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FieldAlias> result = _aliases.Values.OrderByDescending(a => a.CreatedAt).ToList();
        return Task.FromResult(result);
    }

    public Task<bool> DeleteAliasAsync(string id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_aliases.TryRemove(id, out _));
    }
}
