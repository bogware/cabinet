using Cabinet.Core.Models;

namespace Cabinet.Core.Interfaces;

/// <summary>
/// Persistent storage for data model definitions and their acceleration summaries.
/// Allows acceleration state to survive restarts.
/// </summary>
public interface IDataModelStorage
{
    /// <summary>Initialize storage (create tables if needed)</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Save or update a data model definition</summary>
    Task SaveModelAsync(DataModel model, CancellationToken cancellationToken = default);

    /// <summary>Get a data model by ID</summary>
    Task<DataModel?> GetModelAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>List all data model definitions</summary>
    Task<IReadOnlyList<DataModel>> GetAllModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Delete a data model and its summaries</summary>
    Task<bool> DeleteModelAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Save pre-aggregated summaries for a data model (replaces existing)</summary>
    Task SaveSummariesAsync(string modelId, IReadOnlyList<DataModelSummary> summaries, CancellationToken cancellationToken = default);

    /// <summary>Load pre-aggregated summaries for a data model</summary>
    Task<List<DataModelSummary>> GetSummariesAsync(string modelId, CancellationToken cancellationToken = default);
}
