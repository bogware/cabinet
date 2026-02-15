using System.Collections.Concurrent;
using Cabinet.Core.Interfaces;

namespace Cabinet.Storage;

/// <summary>
/// In-memory implementation of field extraction storage.
/// </summary>
public class InMemoryFieldExtractionStorage : IFieldExtractionStorage
{
    private readonly ConcurrentDictionary<string, FieldExtraction> _extractions = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task<FieldExtraction> SaveAsync(FieldExtraction extraction)
    {
        extraction.UpdatedAt = DateTime.UtcNow;
        _extractions[extraction.Id] = extraction;
        return Task.FromResult(extraction);
    }

    public Task<List<FieldExtraction>> GetAllAsync()
        => Task.FromResult(_extractions.Values.ToList().OrderBy(e => e.Priority).ToList());

    public Task<FieldExtraction?> GetByIdAsync(string id)
    {
        _extractions.TryGetValue(id, out var extraction);
        return Task.FromResult(extraction);
    }

    public Task<bool> DeleteAsync(string id)
        => Task.FromResult(_extractions.TryRemove(id, out _));
}
