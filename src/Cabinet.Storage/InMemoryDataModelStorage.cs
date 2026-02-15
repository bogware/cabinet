using System.Collections.Concurrent;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;

namespace Cabinet.Storage;

/// <summary>
/// In-memory implementation of IDataModelStorage for testing and ephemeral deployments.
/// </summary>
public sealed class InMemoryDataModelStorage : IDataModelStorage
{
    private readonly ConcurrentDictionary<string, DataModel> _models = new();
    private readonly ConcurrentDictionary<string, List<DataModelSummary>> _summaries = new();

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SaveModelAsync(DataModel model, CancellationToken cancellationToken = default)
    {
        _models[model.Id] = model;
        return Task.CompletedTask;
    }

    public Task<DataModel?> GetModelAsync(string id, CancellationToken cancellationToken = default)
    {
        _models.TryGetValue(id, out var model);
        return Task.FromResult(model);
    }

    public Task<IReadOnlyList<DataModel>> GetAllModelsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DataModel> result = _models.Values.OrderBy(m => m.Name).ToList().AsReadOnly();
        return Task.FromResult(result);
    }

    public Task<bool> DeleteModelAsync(string id, CancellationToken cancellationToken = default)
    {
        _summaries.TryRemove(id, out _);
        return Task.FromResult(_models.TryRemove(id, out _));
    }

    public Task SaveSummariesAsync(
        string modelId,
        IReadOnlyList<DataModelSummary> summaries,
        CancellationToken cancellationToken = default)
    {
        _summaries[modelId] = summaries.ToList();
        return Task.CompletedTask;
    }

    public Task<List<DataModelSummary>> GetSummariesAsync(
        string modelId, CancellationToken cancellationToken = default)
    {
        _summaries.TryGetValue(modelId, out var summaries);
        return Task.FromResult(summaries ?? new List<DataModelSummary>());
    }
}
