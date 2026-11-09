using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;

namespace Cabinet.Storage;

/// <summary>Search jobs as documents. Results are a separate document, since polling reads every job.</summary>
public sealed class DocumentSearchJobStorage : ISearchJobStorage
{
    private const string Jobs = "search-jobs";
    private const string Results = "search-job-results";

    private readonly IDocumentStore _documents;

    public DocumentSearchJobStorage(IDocumentStore documents)
    {
        _documents = documents;
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken = default) => _documents.InitializeAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<SearchJob> CreateAsync(SearchJob job, CancellationToken cancellationToken = default)
    {
        await UpdateAsync(job, cancellationToken);
        return job;
    }

    /// <inheritdoc />
    public async Task<SearchJob?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var job = await _documents.GetAsync<SearchJob>(Jobs, id, cancellationToken);
        if (job != null)
        {
            job.ResultsJson = await _documents.GetAsync<string>(Results, id, cancellationToken);
        }

        return job;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchJob>> GetAllAsync(string? owner = null, CancellationToken cancellationToken = default)
    {
        var jobs = await _documents.ListAsync<SearchJob>(Jobs, cancellationToken);
        return owner == null ? jobs : jobs.Where(job => job.Owner == owner).ToList();
    }

    /// <inheritdoc />
    public async Task UpdateAsync(SearchJob job, CancellationToken cancellationToken = default)
    {
        if (job.ResultsJson != null)
        {
            await _documents.PutAsync(Results, job.Id, job.ResultsJson, cancellationToken);
        }

        var results = job.ResultsJson;
        job.ResultsJson = null;
        try
        {
            await _documents.PutAsync(Jobs, job.Id, job, cancellationToken);
        }
        finally
        {
            job.ResultsJson = results;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await _documents.DeleteAsync(Results, id, cancellationToken);
        return await _documents.DeleteAsync(Jobs, id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var purged = 0;
        foreach (var job in await _documents.ListAsync<SearchJob>(Jobs, cancellationToken))
        {
            if (job.Status is SearchJobStatus.Completed or SearchJobStatus.Failed or SearchJobStatus.Cancelled
                && (job.CompletedAt ?? job.CreatedAt) + job.Ttl < now
                && await DeleteAsync(job.Id, cancellationToken))
            {
                purged++;
            }
        }

        return purged;
    }
}

/// <summary>Data models and their summaries as documents.</summary>
public sealed class DocumentDataModelStorage : IDataModelStorage
{
    private const string Models = "data-models";
    private const string Summaries = "data-model-summaries";

    private readonly IDocumentStore _documents;

    public DocumentDataModelStorage(IDocumentStore documents)
    {
        _documents = documents;
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken = default) => _documents.InitializeAsync(cancellationToken);

    /// <inheritdoc />
    public Task SaveModelAsync(DataModel model, CancellationToken cancellationToken = default) =>
        _documents.PutAsync(Models, model.Id, model, cancellationToken);

    /// <inheritdoc />
    public Task<DataModel?> GetModelAsync(string id, CancellationToken cancellationToken = default) =>
        _documents.GetAsync<DataModel>(Models, id, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<DataModel>> GetAllModelsAsync(CancellationToken cancellationToken = default) =>
        _documents.ListAsync<DataModel>(Models, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> DeleteModelAsync(string id, CancellationToken cancellationToken = default)
    {
        await _documents.DeleteAsync(Summaries, id, cancellationToken);
        return await _documents.DeleteAsync(Models, id, cancellationToken);
    }

    /// <inheritdoc />
    public Task SaveSummariesAsync(string modelId, IReadOnlyList<DataModelSummary> summaries, CancellationToken cancellationToken = default) =>
        _documents.PutAsync(Summaries, modelId, summaries, cancellationToken);

    /// <inheritdoc />
    public async Task<List<DataModelSummary>> GetSummariesAsync(string modelId, CancellationToken cancellationToken = default) =>
        await _documents.GetAsync<List<DataModelSummary>>(Summaries, modelId, cancellationToken) ?? new List<DataModelSummary>();
}

/// <summary>Alert history as documents, keeping the newest.</summary>
public sealed class DocumentAlertHistoryStorage : IAlertHistoryStorage
{
    private const string Alerts = "alerts";

    /// <summary>Alerts kept by default. A noisy rule shouldn't grow this without limit.</summary>
    public const int MaxAlerts = 10_000;

    private readonly IDocumentStore _documents;
    private readonly int _maxAlerts;
    private int _savedSincePrune;

    public DocumentAlertHistoryStorage(IDocumentStore documents, int maxAlerts = MaxAlerts)
    {
        _documents = documents;
        _maxAlerts = Math.Max(1, maxAlerts);
    }

    /// <inheritdoc />
    public Task InitializeAsync() => _documents.InitializeAsync();

    /// <inheritdoc />
    public async Task<AlertRecord> SaveAlertAsync(AlertRecord alert)
    {
        await _documents.PutAsync(Alerts, alert.Id, alert);

        // Listing is the costly part, so only check now and then
        if (Interlocked.Increment(ref _savedSincePrune) >= 500)
        {
            Interlocked.Exchange(ref _savedSincePrune, 0);
            var all = await _documents.ListAsync<AlertRecord>(Alerts);
            foreach (var old in all.OrderByDescending(a => a.FiredAt).Skip(_maxAlerts))
            {
                await _documents.DeleteAsync(Alerts, old.Id);
            }
        }

        return alert;
    }

    /// <inheritdoc />
    public async Task<List<AlertRecord>> GetAlertsAsync(int offset = 0, int count = 100, string? severity = null, bool? acknowledged = null)
    {
        var alerts = await FilteredAsync(severity, acknowledged);
        return alerts.OrderByDescending(a => a.FiredAt).Skip(offset).Take(count).ToList();
    }

    /// <inheritdoc />
    public Task<AlertRecord?> GetAlertByIdAsync(string id) => _documents.GetAsync<AlertRecord>(Alerts, id);

    /// <inheritdoc />
    public async Task<AlertRecord?> AcknowledgeAlertAsync(string id, string acknowledgedBy)
    {
        var alert = await GetAlertByIdAsync(id);
        if (alert == null)
        {
            return null;
        }

        alert.Acknowledged = true;
        alert.AcknowledgedBy = acknowledgedBy;
        alert.AcknowledgedAt = DateTime.UtcNow;
        await _documents.PutAsync(Alerts, id, alert);
        return alert;
    }

    /// <inheritdoc />
    public async Task<int> GetAlertCountAsync(string? severity = null, bool? acknowledged = null) =>
        (await FilteredAsync(severity, acknowledged)).Count();

    private async Task<IEnumerable<AlertRecord>> FilteredAsync(string? severity, bool? acknowledged)
    {
        IEnumerable<AlertRecord> alerts = await _documents.ListAsync<AlertRecord>(Alerts);
        if (severity != null)
        {
            alerts = alerts.Where(a => a.Severity.Equals(severity, StringComparison.OrdinalIgnoreCase));
        }

        if (acknowledged.HasValue)
        {
            alerts = alerts.Where(a => a.Acknowledged == acknowledged.Value);
        }

        return alerts;
    }
}

/// <summary>Field extractions as documents.</summary>
public sealed class DocumentFieldExtractionStorage : IFieldExtractionStorage
{
    private const string Extractions = "field-extractions";

    private readonly IDocumentStore _documents;

    public DocumentFieldExtractionStorage(IDocumentStore documents)
    {
        _documents = documents;
    }

    /// <inheritdoc />
    public Task InitializeAsync() => _documents.InitializeAsync();

    /// <inheritdoc />
    public async Task<FieldExtraction> SaveAsync(FieldExtraction extraction)
    {
        extraction.UpdatedAt = DateTime.UtcNow;
        await _documents.PutAsync(Extractions, extraction.Id, extraction);
        return extraction;
    }

    /// <inheritdoc />
    public async Task<List<FieldExtraction>> GetAllAsync() =>
        (await _documents.ListAsync<FieldExtraction>(Extractions)).ToList();

    /// <inheritdoc />
    public Task<FieldExtraction?> GetByIdAsync(string id) => _documents.GetAsync<FieldExtraction>(Extractions, id);

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string id) => _documents.DeleteAsync(Extractions, id);
}
