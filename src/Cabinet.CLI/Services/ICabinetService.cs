using Cabinet.Core.Models;
using Cabinet.Core.Interfaces;
using Cabinet.Query;

namespace Cabinet.CLI.Services;

/// <summary>
/// Abstraction for Cabinet operations (local or remote)
/// </summary>
public interface ICabinetService
{
    // Storage operations
    Task<IEnumerable<LogEvent>> GetEventsAsync(
        Dictionary<string, object>? filter = null,
        string? search = null,
        int limit = 100);

    Task<int> StoreBatchAsync(IEnumerable<LogEvent> events);
    Task<long> GetEventCountAsync(Dictionary<string, object>? filter = null);
    Task InitializeAsync();

    // Query operations
    Task<IEnumerable<LogEvent>> ExecuteQueryAsync(string query, Dictionary<string, object>? parameters = null);
    Task<bool> ValidateQueryAsync(string query);

    // Health operations
    Task<bool> CheckHealthAsync();
    Task<string> GetVersionAsync();
}

/// <summary>
/// Local implementation using direct access to services
/// </summary>
public class LocalCabinetService : ICabinetService
{
    private readonly ILogStorage _storage;
    private readonly IQueryEngine _queryEngine;

    public LocalCabinetService(
        ILogStorage storage,
        IQueryEngine queryEngine)
    {
        _storage = storage;
        _queryEngine = queryEngine;
    }

    public async Task<IEnumerable<LogEvent>> GetEventsAsync(
        Dictionary<string, object>? filter = null,
        string? search = null,
        int limit = 100)
    {
        return await _storage.GetEventsAsync(filter ?? new Dictionary<string, object>(), search, limit: limit);
    }

    public async Task<int> StoreBatchAsync(IEnumerable<LogEvent> events)
    {
        return await _storage.StoreBatchAsync(events);
    }

    public async Task<long> GetEventCountAsync(Dictionary<string, object>? filter = null)
    {
        return await _storage.GetEventCountAsync(filter ?? new Dictionary<string, object>());
    }

    public async Task InitializeAsync()
    {
        await _storage.InitializeAsync();
    }

    public async Task<IEnumerable<LogEvent>> ExecuteQueryAsync(string query, Dictionary<string, object>? parameters = null)
    {
        return await _queryEngine.ExecuteAsync(query, parameters);
    }

    public Task<bool> ValidateQueryAsync(string query)
    {
        try
        {
            var parser = new Cabinet.Query.QueryParser(Microsoft.Extensions.Logging.Abstractions.NullLogger<Cabinet.Query.QueryParser>.Instance);
            parser.Parse(query);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> CheckHealthAsync()
    {
        return Task.FromResult(true); // Local is always healthy if initialized
    }

    public Task<string> GetVersionAsync()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version?.ToString() ?? "1.0.0";
        return Task.FromResult(version);
    }
}

/// <summary>
/// Remote implementation using API client
/// </summary>
public class RemoteCabinetService : ICabinetService
{
    private readonly CabinetApiClient _apiClient;

    public RemoteCabinetService(CabinetApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public async Task<IEnumerable<LogEvent>> GetEventsAsync(
        Dictionary<string, object>? filter = null,
        string? search = null,
        int limit = 100)
    {
        var response = await _apiClient.GetLogsAsync(
            pageSize: limit,
            search: search,
            source: filter?.GetValueOrDefault("source_type")?.ToString(),
            severity: filter?.GetValueOrDefault("severity")?.ToString()
        );
        return response.Data;
    }

    public async Task<int> StoreBatchAsync(IEnumerable<LogEvent> events)
    {
        var logs = events.Select(e => e.RawLine).ToList();
        var response = await _apiClient.IngestLogsAsync(logs);
        return response.TotalIngested;
    }

    public async Task<long> GetEventCountAsync(Dictionary<string, object>? filter = null)
    {
        var response = await _apiClient.GetLogsAsync(
            pageSize: 1,
            source: filter?.GetValueOrDefault("source_type")?.ToString(),
            severity: filter?.GetValueOrDefault("severity")?.ToString()
        );
        return response.TotalCount;
    }

    public Task InitializeAsync()
    {
        // API doesn't need initialization
        return Task.CompletedTask;
    }

    public async Task<IEnumerable<LogEvent>> ExecuteQueryAsync(string query, Dictionary<string, object>? parameters = null)
    {
        var response = await _apiClient.ExecuteQueryAsync(query, parameters);
        return response.Results;
    }

    public async Task<bool> ValidateQueryAsync(string query)
    {
        var response = await _apiClient.ValidateQueryAsync(query);
        return response.IsValid;
    }

    public async Task<bool> CheckHealthAsync()
    {
        try
        {
            var health = await _apiClient.GetHealthAsync();
            return health.Status == "Healthy";
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> GetVersionAsync()
    {
        var version = await _apiClient.GetVersionAsync();
        return version.Version;
    }
}