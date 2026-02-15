using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cabinet.Core.Models;

namespace Cabinet.CLI.Services;

/// <summary>
/// Client for communicating with Cabinet API
/// </summary>
public class CabinetApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly JsonSerializerOptions _jsonOptions;

    public CabinetApiClient(string baseUrl, string apiKey)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl)
        };
        _apiKey = apiKey;

        _httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    // Log Operations
    public async Task<IngestResponse> IngestLogsAsync(List<string> logs)
    {
        var request = new { logs };
        var response = await PostAsync<IngestResponse>("/api/v1/logs/ingest", request);
        return response ?? new IngestResponse();
    }

    public async Task<PagedResponse<LogEvent>> GetLogsAsync(
        int page = 1,
        int pageSize = 100,
        string? search = null,
        string? source = null,
        string? severity = null)
    {
        var query = $"?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrEmpty(search)) query += $"&search={Uri.EscapeDataString(search)}";
        if (!string.IsNullOrEmpty(source)) query += $"&source={Uri.EscapeDataString(source)}";
        if (!string.IsNullOrEmpty(severity)) query += $"&severity={Uri.EscapeDataString(severity)}";

        var response = await GetAsync<PagedResponse<LogEvent>>($"/api/v1/logs{query}");
        return response ?? new PagedResponse<LogEvent>();
    }

    public async Task<LogEvent?> GetLogAsync(string id)
    {
        return await GetAsync<LogEvent>($"/api/v1/logs/{id}");
    }

    // Query Operations
    public async Task<QueryResponse> ExecuteQueryAsync(string query, Dictionary<string, object>? parameters = null)
    {
        var request = new { query, parameters };
        var response = await PostAsync<QueryResponse>("/api/v1/query/execute", request);
        return response ?? new QueryResponse();
    }

    public async Task<ValidationResponse> ValidateQueryAsync(string query)
    {
        var request = new { query };
        var response = await PostAsync<ValidationResponse>("/api/v1/query/validate", request);
        return response ?? new ValidationResponse();
    }

    public async Task<List<SavedQuery>> GetSavedQueriesAsync()
    {
        var response = await GetAsync<List<SavedQuery>>("/api/v1/query/saved");
        return response ?? new List<SavedQuery>();
    }

    // Detection Operations
    public async Task<DetectionResponse> RunScumscanAsync(List<LogEvent> events)
    {
        var request = new { events };
        var response = await PostAsync<DetectionResponse>("/api/v1/detection/scumscan", request);
        return response ?? new DetectionResponse();
    }

    public async Task<DetectionResponse> RunHackwatchAsync(List<LogEvent> events)
    {
        var request = new { events };
        var response = await PostAsync<DetectionResponse>("/api/v1/detection/hackwatch", request);
        return response ?? new DetectionResponse();
    }

    public async Task<DetectionResponse> RunAdminabuseAsync(List<LogEvent> events)
    {
        var request = new { events };
        var response = await PostAsync<DetectionResponse>("/api/v1/detection/adminabuse", request);
        return response ?? new DetectionResponse();
    }

    public async Task<ComprehensiveAnalysisResponse> AnalyzeComprehensiveAsync(List<LogEvent> events)
    {
        var request = new { events };
        var response = await PostAsync<ComprehensiveAnalysisResponse>("/api/v1/detection/analyze", request);
        return response ?? new ComprehensiveAnalysisResponse();
    }

    // Health Operations
    public async Task<HealthResponse> GetHealthAsync()
    {
        var response = await GetAsync<HealthResponse>("/api/health");
        return response ?? new HealthResponse();
    }

    public async Task<VersionInfo> GetVersionAsync()
    {
        var response = await GetAsync<VersionInfo>("/api/health/version");
        return response ?? new VersionInfo();
    }

    public async Task<ApiStatus> GetStatusAsync()
    {
        var response = await GetAsync<ApiStatus>("/api/health/status");
        return response ?? new ApiStatus();
    }

    // Helper Methods
    private async Task<T?> GetAsync<T>(string endpoint)
    {
        try
        {
            var response = await _httpClient.GetAsync(endpoint);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<T>(json, _jsonOptions);
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException($"API request failed: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new ApiException($"Failed to parse API response: {ex.Message}", ex);
        }
    }

    private async Task<T?> PostAsync<T>(string endpoint, object data)
    {
        try
        {
            var json = JsonSerializer.Serialize(data, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(endpoint, content);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<T>(responseJson, _jsonOptions);
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException($"API request failed: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new ApiException($"Failed to parse API response: {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}

// DTOs for API responses
public class IngestResponse
{
    public int TotalReceived { get; set; }
    public int TotalIngested { get; set; }
    public List<string> Errors { get; set; } = new();
}

public class PagedResponse<T>
{
    public IEnumerable<T> Data { get; set; } = Enumerable.Empty<T>();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public long TotalCount { get; set; }
    public int TotalPages { get; set; }
}

public class QueryResponse
{
    public string Query { get; set; } = string.Empty;
    public IEnumerable<LogEvent> Results { get; set; } = Enumerable.Empty<LogEvent>();
    public int Count { get; set; }
    public int ExecutionTimeMs { get; set; }
    public DateTime Timestamp { get; set; }
}

public class ValidationResponse
{
    public bool IsValid { get; set; }
    public string Query { get; set; } = string.Empty;
    public string? Error { get; set; }
    public int OperatorCount { get; set; }
    public List<string> Operators { get; set; } = new();
}

public class SavedQuery
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class DetectionResponse
{
    public string DetectionType { get; set; } = string.Empty;
    public int TotalAnalyzed { get; set; }
    public int TotalDetections { get; set; }
    public List<DetectionResult> Results { get; set; } = new();
}

public class ComprehensiveAnalysisResponse
{
    public int TotalAnalyzed { get; set; }
    public int TotalDetections { get; set; }
    public List<DetectionResult> ScumscanResults { get; set; } = new();
    public List<DetectionResult> HackwatchResults { get; set; } = new();
    public List<DetectionResult> AdminabuseResults { get; set; } = new();
    public RiskAssessment RiskAssessment { get; set; } = new();
}

public class RiskAssessment
{
    public string Level { get; set; } = string.Empty;
    public int Score { get; set; }
    public List<string> Factors { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
}

public class HealthResponse
{
    public string Status { get; set; } = string.Empty;
    public double TotalDuration { get; set; }
    public List<HealthCheckResult> Checks { get; set; } = new();
}

public class HealthCheckResult
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public double Duration { get; set; }
    public string? Description { get; set; }
}

public class VersionInfo
{
    public string Version { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = string.Empty;
    public DateTime BuildDate { get; set; }
    public string Environment { get; set; } = string.Empty;
}

public class ApiStatus
{
    public string Status { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public Dictionary<string, ServiceStatus> Services { get; set; } = new();
    public Dictionary<string, bool> Features { get; set; } = new();
}

public class ServiceStatus
{
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
}

public class ApiException : Exception
{
    public ApiException(string message) : base(message) { }
    public ApiException(string message, Exception innerException) : base(message, innerException) { }
}