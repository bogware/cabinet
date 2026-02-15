using Cabinet.Core.Models;

namespace Cabinet.Core.Interfaces;

/// <summary>
/// Persistent storage for knowledge objects (lookup tables, query macros, field aliases).
/// </summary>
public interface IKnowledgeStorage
{
    /// <summary>Initialize storage (create tables if needed)</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    // Lookup tables
    Task<LookupTable> SaveLookupAsync(LookupTable lookup, CancellationToken cancellationToken = default);
    Task<LookupTable?> GetLookupByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<LookupTable?> GetLookupByNameAsync(string name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LookupTable>> GetAllLookupsAsync(CancellationToken cancellationToken = default);
    Task<bool> DeleteLookupAsync(string id, CancellationToken cancellationToken = default);

    // Query macros
    Task<QueryMacro> SaveMacroAsync(QueryMacro macro, CancellationToken cancellationToken = default);
    Task<QueryMacro?> GetMacroByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<QueryMacro?> GetMacroByNameAsync(string name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QueryMacro>> GetAllMacrosAsync(CancellationToken cancellationToken = default);
    Task<bool> DeleteMacroAsync(string id, CancellationToken cancellationToken = default);

    // Field aliases
    Task<FieldAlias> SaveAliasAsync(FieldAlias alias, CancellationToken cancellationToken = default);
    Task<FieldAlias?> GetAliasByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FieldAlias>> GetAllAliasesAsync(CancellationToken cancellationToken = default);
    Task<bool> DeleteAliasAsync(string id, CancellationToken cancellationToken = default);
}
