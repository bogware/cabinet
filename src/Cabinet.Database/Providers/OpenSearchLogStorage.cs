using System.Runtime.CompilerServices;
using System.Text.Json;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.Extensions.Logging;
using OpenSearch.Client;
using OpenSearch.Net;

namespace Cabinet.Database.Providers;

/// <summary>
/// OpenSearch ILogStorage implementation for distributed, full-text search at scale.
/// Supports both OpenSearch and Elasticsearch-compatible backends.
/// </summary>
public sealed partial class OpenSearchLogStorage : ILogStorage, IDisposable
{
    private const int SearchPageSize = 1000;

    private readonly ILogger<OpenSearchLogStorage> _logger;
    private readonly OpenSearchClient _client;
    private readonly string _indexPrefix;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly int _shards;
    private readonly int _replicas;
    private readonly int _fieldLimit;
    private volatile bool _initialized;

    public OpenSearchLogStorage(
        string connectionUri,
        ILogger<OpenSearchLogStorage> logger,
        string indexPrefix = "cabinet-logs",
        string? username = null,
        string? password = null,
        bool allowInsecureSsl = false,
        int shards = 1,
        int? replicas = null,
        int fieldLimit = 10_000)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _indexPrefix = indexPrefix;

        // Several nodes, comma-separated, share the requests and cover for one that's down
        var uris = connectionUri.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(u => new Uri(u)).ToList();
        IConnectionPool pool = uris.Count == 1 ? new SingleNodeConnectionPool(uris[0]) : new StaticConnectionPool(uris);

        // One shard per daily index suits most volumes; more only spreads small indexes thin. A
        // replica needs a second node, so one node gets none and stays green.
        _shards = Math.Clamp(shards, 1, 64);
        _replicas = Math.Clamp(replicas ?? (uris.Count > 1 ? 1 : 0), 0, 8);

        // Each custom field name is a mapped field. OpenSearch's default of 1,000 rejects events once
        // logs have used that many names in a day.
        _fieldLimit = Math.Clamp(fieldLimit, 1_000, 100_000);
        var settings = new ConnectionSettings(pool)
            .DefaultIndex($"{_indexPrefix}-*")
            .EnableHttpCompression()
            .RequestTimeout(TimeSpan.FromSeconds(60));

        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            settings = settings.BasicAuthentication(username, password);
        }

        // Only bypass SSL validation when explicitly configured (development/self-signed certs)
        if (allowInsecureSsl)
        {
            var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            if (string.Equals(env, "Production", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError(
                    "SECURITY: AllowInsecureSsl is enabled in Production environment. " +
                    "This allows MITM attacks. Configure proper SSL certificates instead.");
            }
            _logger.LogWarning("OpenSearch SSL certificate validation is DISABLED. Do not use this in production.");
            settings = settings.ServerCertificateValidationCallback(CertificateValidations.AllowAll);
        }

        _client = new OpenSearchClient(settings);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            _logger.LogInformation("Initializing OpenSearch storage");

            // Create index template for time-based indices
            var templateName = $"{_indexPrefix}-template";
            var putTemplateResponse = await _client.Indices.PutTemplateAsync(
                templateName, t => t
                    .IndexPatterns($"{_indexPrefix}-*")
                    .Settings(s => s
                        .NumberOfShards(_shards)
                        .NumberOfReplicas(_replicas)
                        .Setting("index.mapping.total_fields.limit", _fieldLimit)
                        .Setting("index.refresh_interval", "5s")
                        .Analysis(a => a
                            .Tokenizers(tk => tk.Pattern("cabinet_words", pt => pt.Pattern(@"[^\p{L}\p{N}]+")))
                            .Analyzers(an => an.Custom("cabinet_search", c => c.Tokenizer("cabinet_words").Filters("lowercase")))))
                    .Map<OpenSearchLogEvent>(m => m
                        // Every custom field as text. Mapped dynamically, a field that holds a number in one
                        // event and text in another would make OpenSearch reject the second event.
                        .DynamicTemplates(templates => templates.DynamicTemplate("fields_as_keywords", template => template
                            .PathMatch("fields.*")
                            .MatchMappingType("*")
                            .Mapping(mapping => mapping.Keyword(keyword => keyword.IgnoreAbove(8191)))))
                        .Properties(p => p
                            .Keyword(k => k.Name(n => n.Id))
                            .Date(d => d.Name(n => n.Timestamp))
                            .Date(d => d.Name(n => n.IndexTime))
                            .Keyword(k => k.Name(n => n.SourceType))
                            .Number(n => n.Name(nm => nm.Severity).Type(NumberType.Integer))
                            .Keyword(k => k.Name(n => n.Host))
                            .Text(tx => tx.Name(n => n.RawLine)
                                .Analyzer("cabinet_search")
                                .Fields(f => f.Keyword(kw => kw.Name("keyword").IgnoreAbove(256))))
                            .Keyword(k => k.Name(n => n.SourceFile))
                            .Number(n => n.Name(nm => nm.LineNumber).Type(NumberType.Long))
                            .Keyword(k => k.Name(n => n.ParserName))
                            .Object<Dictionary<string, object?>>(o => o.Name(n => n.Fields).Enabled())
                            .Keyword(k => k.Name(n => n.Tags))
                            .Keyword(k => k.Name(n => n.IndexName)))),
                cancellationToken);

            if (!putTemplateResponse.IsValid)
            {
                _logger.LogWarning("Failed to create index template: {Error}", putTemplateResponse.OriginalException?.Message);
            }

            // Create initial index
            var currentIndex = GetCurrentIndexName();
            var existsResponse = await _client.Indices.ExistsAsync(currentIndex, ct: cancellationToken);
            if (!existsResponse.Exists)
            {
                var createResponse = await _client.Indices.CreateAsync(currentIndex, ct: cancellationToken);
                if (!createResponse.IsValid)
                {
                    _logger.LogWarning("Failed to create initial index: {Error}", createResponse.OriginalException?.Message);
                }
            }

            _initialized = true;
            _logger.LogInformation("OpenSearch storage initialized");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<bool> StoreAsync(LogEvent logEvent, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var doc = MapToDocument(logEvent);
        var indexName = GetIndexNameForDate(logEvent.Timestamp);

        // Create, not index, so an event ID that's already stored isn't overwritten
        var response = await _client.IndexAsync(doc, i => i
            .Index(indexName)
            .Id(logEvent.Id)
            .OpType(OpenSearch.Net.OpType.Create), cancellationToken);

        return response.IsValid;
    }

    public async Task<int> StoreBatchAsync(IEnumerable<LogEvent> events, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var eventList = events.ToList();
        if (eventList.Count == 0) return 0;

        var bulkResponse = await _client.BulkAsync(b =>
        {
            foreach (var evt in eventList)
            {
                var doc = MapToDocument(evt);
                var indexName = GetIndexNameForDate(evt.Timestamp);
                // Create, not index, so an event ID that's already stored isn't overwritten
                b.Create<OpenSearchLogEvent>(i => i
                    .Index(indexName)
                    .Id(evt.Id)
                    .Document(doc));
            }
            return b;
        }, cancellationToken);

        if (!bulkResponse.IsValid && !bulkResponse.Errors)
        {
            throw new InvalidOperationException(
                $"OpenSearch bulk request failed: {bulkResponse.ServerError?.ToString() ?? bulkResponse.OriginalException?.Message}",
                bulkResponse.OriginalException);
        }

        if (bulkResponse.Errors)
        {
            // 409 means the event ID is already stored, which is what a retry should find.
            var failed = bulkResponse.ItemsWithErrors.Where(item => item.Status != 409).ToList();
            if (failed.Count > 0)
            {
                // Fail the whole request. The sender retries, and events already stored are ignored.
                throw new InvalidOperationException(
                    $"OpenSearch rejected {failed.Count} of {eventList.Count} events: {failed[0].Error?.Reason}");
            }

            return eventList.Count - bulkResponse.ItemsWithErrors.Count();
        }

        _logger.LogDebug("Bulk stored {Count} events", eventList.Count);
        return eventList.Count;
    }

    public async IAsyncEnumerable<LogEvent> QueryAsync(
        QueryContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        // OpenSearch rejects from + size beyond index.max_result_window (10,000 by default),
        // so read in pages with search_after. Skipped rows have to be read and discarded.
        var searchDescriptor = BuildSearchDescriptor(context);
        var toSkip = context.Skip;
        var toReturn = context.Limit;

        // Sort values are the date in whole milliseconds and the ID
        IReadOnlyCollection<object>? after = context.After == null
            ? null
            : new object[] { new DateTimeOffset(DateTime.SpecifyKind(context.After.Timestamp, DateTimeKind.Utc)).ToUnixTimeMilliseconds(), context.After.Id };

        while (toReturn > 0)
        {
            var pageSize = Math.Min(SearchPageSize, toSkip + toReturn);
            var cursor = after;
            var response = await _client.SearchAsync<OpenSearchLogEvent>(s =>
            {
                searchDescriptor(s);
                s.From(null).Size(pageSize);
                if (cursor != null)
                {
                    s.SearchAfter(cursor.ToArray());
                }

                return s;
            }, cancellationToken);

            ThrowIfFailed(response, "query");

            // Failed shards return 200 with partial hits, which would read as missing events.
            if (response.Shards?.Failed > 0)
            {
                throw new InvalidOperationException(
                    $"OpenSearch query failed on {response.Shards.Failed} shards: {response.Shards.Failures.FirstOrDefault()?.Reason?.Reason}");
            }

            var hits = response.Hits.ToList();
            foreach (var hit in hits)
            {
                if (toSkip > 0)
                {
                    toSkip--;
                    continue;
                }

                if (toReturn == 0)
                {
                    yield break;
                }

                toReturn--;
                yield return MapFromDocument(hit.Source);
            }

            if (hits.Count < pageSize)
            {
                yield break;
            }

            after = hits[^1].Sorts;
        }
    }

    public async Task<LogEvent?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        // Search across all time-based indices
        var response = await _client.SearchAsync<OpenSearchLogEvent>(s => s
            .Index($"{_indexPrefix}-*")
            .Query(q => q.Term(t => t.Field(f => f.Id).Value(id)))
            .Size(1), cancellationToken);

        ThrowIfFailed(response, "lookup");

        if (!response.IsValid || !response.Hits.Any())
            return null;

        return MapFromDocument(response.Hits.First().Source);
    }

    public async Task<long> GetCountAsync(QueryContext? context = null, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        if (context == null)
        {
            var countResponse = await _client.CountAsync<OpenSearchLogEvent>(c => c
                .Index($"{_indexPrefix}-*"), cancellationToken);
            ThrowIfFailed(countResponse, "count");
            return countResponse.Count;
        }

        var searchDescriptor = BuildSearchDescriptor(context);
        // Override to only get count
        var response = await _client.SearchAsync<OpenSearchLogEvent>(s =>
        {
            searchDescriptor(s);
            s.Size(0).TrackTotalHits(true);
            return s;
        }, cancellationToken);

        ThrowIfFailed(response, "count");
        if (response.Shards?.Failed > 0)
        {
            throw new InvalidOperationException(
                $"OpenSearch count failed on {response.Shards.Failed} shards: {response.Shards.Failures.FirstOrDefault()?.Reason?.Reason}");
        }

        return response.Total;
    }

    public async Task<int> PurgeOldEventsAsync(DateTime before, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var response = await _client.DeleteByQueryAsync<OpenSearchLogEvent>(d => d
            .Index($"{_indexPrefix}-*")
            .Query(q => q.DateRange(r => r
                .Field(f => f.Timestamp)
                .LessThan(before))), cancellationToken);

        ThrowIfFailed(response, "purge");
        var deleted = (int)response.Deleted;
        _logger.LogInformation("Purged {Count} events older than {Before}", deleted, before);
        return deleted;
    }

    public async Task<int> PurgeIndexEventsAsync(string indexName, DateTime before, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var response = await _client.DeleteByQueryAsync<OpenSearchLogEvent>(d => d
            .Index($"{_indexPrefix}-*")
            .Conflicts(Conflicts.Proceed)
            .Query(q => q.Bool(b => b.Filter(
                f => f.Term(t => t.Field(e => e.IndexName).Value(indexName)),
                f => f.DateRange(r => r.Field(e => e.Timestamp).LessThan(before))))), cancellationToken);

        ThrowIfFailed(response, "purge index");
        return (int)response.Deleted;
    }

    public async Task<int> DeleteByIndexAsync(string indexName, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var response = await _client.DeleteByQueryAsync<OpenSearchLogEvent>(d => d
            .Index($"{_indexPrefix}-*")
            .Query(q => q.Term(t => t
                .Field(f => f.IndexName)
                .Value(indexName))), cancellationToken);

        var deleted = (int)response.Deleted;
        _logger.LogInformation("Deleted {Count} events from index '{Index}'", deleted, indexName);
        return deleted;
    }

    public async Task<StorageStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var stats = new StorageStats();

        var countResponse = await _client.CountAsync<OpenSearchLogEvent>(c => c
            .Index($"{_indexPrefix}-*"), cancellationToken);
        stats.TotalEvents = countResponse.Count;

        if (stats.TotalEvents > 0)
        {
            // Get date range
            var dateRangeResponse = await _client.SearchAsync<OpenSearchLogEvent>(s => s
                .Index($"{_indexPrefix}-*")
                .Size(0)
                .Aggregations(a => a
                    .Min("min_ts", m => m.Field(f => f.Timestamp))
                    .Max("max_ts", m => m.Field(f => f.Timestamp))), cancellationToken);

            if (dateRangeResponse.IsValid)
            {
                var minAgg = dateRangeResponse.Aggregations.Min("min_ts");
                var maxAgg = dateRangeResponse.Aggregations.Max("max_ts");
                if (minAgg?.Value != null)
                    stats.OldestEvent = DateTimeOffset.FromUnixTimeMilliseconds((long)minAgg.Value).UtcDateTime;
                if (maxAgg?.Value != null)
                    stats.NewestEvent = DateTimeOffset.FromUnixTimeMilliseconds((long)maxAgg.Value).UtcDateTime;
            }

            // Get index stats for storage size
            var indicesResponse = await _client.Indices.StatsAsync($"{_indexPrefix}-*", ct: cancellationToken);
            if (indicesResponse.IsValid && indicesResponse.Indices != null)
            {
                long totalSize = 0;
                foreach (var indexStats in indicesResponse.Indices.Values)
                {
                    totalSize += (long)(indexStats?.Total?.Store?.SizeInBytes ?? 0);
                }
                stats.StorageSizeBytes = totalSize;
            }
        }

        return stats;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized)
            await InitializeAsync(cancellationToken);
    }

    private static void ThrowIfFailed(IResponse response, string operation)
    {
        if (!response.IsValid)
        {
            throw new InvalidOperationException(
                $"OpenSearch {operation} failed: {response.ServerError?.Error?.Reason ?? response.OriginalException?.Message}",
                response.OriginalException);
        }
    }

    private string GetCurrentIndexName() => GetIndexNameForDate(DateTime.UtcNow);

    private string GetIndexNameForDate(DateTime timestamp) =>
        $"{_indexPrefix}-{timestamp:yyyy.MM}";

    private Func<SearchDescriptor<OpenSearchLogEvent>, ISearchRequest> BuildSearchDescriptor(QueryContext context)
    {
        return s =>
        {
            s.Index($"{_indexPrefix}-*");

            var queries = new List<Func<QueryContainerDescriptor<OpenSearchLogEvent>, QueryContainer>>();

            if (context.AllowedIndexes is { } allowed)
            {
                queries.Add(q => q.Bool(b => b
                    .MinimumShouldMatch(1)
                    .Should(allowed.Select(pattern => (Func<QueryContainerDescriptor<OpenSearchLogEvent>, QueryContainer>)(sq =>
                        sq.Wildcard(w => w.Field(f => f.IndexName).Value(pattern).CaseInsensitive(true)))).ToArray())));
            }

            var terms = SearchTerms.Parse(context.SearchText);
            if (terms.Count > 0)
            {
                queries.Add(q => q.Match(m => m.Field(f => f.RawLine).Operator(Operator.And).Query(string.Join(' ', terms))));
            }

            if (context.StartTime.HasValue || context.EndTime.HasValue)
            {
                queries.Add(q => q.DateRange(r =>
                {
                    r.Field(f => f.Timestamp);
                    if (context.StartTime.HasValue) r.GreaterThanOrEquals(context.StartTime.Value);
                    if (context.EndTime.HasValue) r.LessThanOrEquals(context.EndTime.Value);
                    return r;
                }));
            }

            if (!string.IsNullOrEmpty(context.SourceType))
            {
                queries.Add(q => q.Term(t => t.Field(f => f.SourceType).Value(context.SourceType).CaseInsensitive()));
            }

            if (context.MinSeverity.HasValue)
            {
                queries.Add(q => q.Range(r => r.Field(f => f.Severity).GreaterThanOrEquals((int)context.MinSeverity.Value)));
            }

            if (!string.IsNullOrWhiteSpace(context.Host))
            {
                queries.Add(q => q.Term(t => t.Field(f => f.Host).Value(context.Host).CaseInsensitive()));
            }

            if (!string.IsNullOrEmpty(context.Index))
            {
                queries.Add(q => q.Term(t => t.Field(f => f.IndexName).Value(context.Index)));
            }

            if (context.Tags != null && context.Tags.Count > 0)
            {
                foreach (var tag in context.Tags)
                {
                    queries.Add(q => q.Term(t => t.Field(f => f.Tags).Value(tag)));
                }
            }

            // Apply arbitrary field filters (e.g., from QueryContext.FieldFilters)
            if (context.FieldFilters != null)
            {
                foreach (var (key, value) in context.FieldFilters)
                {
                    if (value == null) continue;
                    // Validate field name to prevent path injection (only alphanumeric, underscore, dot)
                    if (!System.Text.RegularExpressions.Regex.IsMatch(key, @"^[a-zA-Z0-9_][a-zA-Z0-9_.]*$"))
                    {
                        _logger.LogWarning("Skipping invalid field filter key: {Key}", key);
                        continue;
                    }
                    var fieldPath = $"fields.{key}";
                    var fieldValue = value.ToString();
                    queries.Add(q => q.Term(t => t.Field(fieldPath).Value(fieldValue)));
                }
            }

            if (queries.Count > 0)
            {
                s.Query(q => q.Bool(b => b.Must(queries.ToArray())));
            }
            else
            {
                s.Query(q => q.MatchAll());
            }

            // Sort
            var sortField = context.SortField.ToLowerInvariant() switch
            {
                "severity" => "severity",
                "sourcetype" or "source_type" => "sourceType",
                _ => "timestamp"
            };
            s.Sort(so => context.SortDescending
                ? so.Descending(sortField).Descending("id")
                : so.Ascending(sortField).Ascending("id"));

            return s;
        };
    }

    private static OpenSearchLogEvent MapToDocument(LogEvent evt) => new()
    {
        Id = evt.Id,
        Timestamp = evt.Timestamp,
        IndexTime = evt.IndexTime,
        SourceType = evt.SourceType,
        Severity = (int)evt.Severity,
        Host = evt.Host,
        RawLine = evt.RawLine,
        SourceFile = evt.SourceFile,
        LineNumber = evt.LineNumber,
        ParserName = evt.ParserName,
        Fields = evt.Fields.ToDictionary(f => f.Key, f => f.Value),
        Tags = evt.Tags.ToList(),
        IndexName = evt.Index
    };

    private static LogEvent MapFromDocument(OpenSearchLogEvent doc)
    {
        var evt = new LogEvent
        {
            Id = doc.Id,
            Timestamp = doc.Timestamp,
            IndexTime = doc.IndexTime,
            SourceType = doc.SourceType,
            Severity = (LogSeverity)doc.Severity,
            Host = doc.Host ?? "",
            RawLine = doc.RawLine,
            SourceFile = doc.SourceFile ?? "",
            LineNumber = doc.LineNumber,
            ParserName = doc.ParserName ?? "",
            Index = doc.IndexName ?? "main"
        };

        if (doc.Fields != null)
        {
            foreach (var (key, value) in doc.Fields)
            {
                evt.SetField(key, value);
            }
        }

        if (doc.Tags != null)
        {
            foreach (var tag in doc.Tags)
                evt.Tags.Add(tag);
        }

        return evt;
    }

    public void Dispose()
    {
        // OpenSearchClient doesn't need explicit disposal, but we follow the pattern
    }
}

/// <summary>
/// OpenSearch document model for log events
/// </summary>
public class OpenSearchLogEvent
{
    public string Id { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public DateTime IndexTime { get; set; }
    public string SourceType { get; set; } = "Generic";
    public int Severity { get; set; }
    public string? Host { get; set; }
    public string RawLine { get; set; } = "";
    public string? SourceFile { get; set; }
    public long LineNumber { get; set; }
    public string? ParserName { get; set; }
    public Dictionary<string, object?>? Fields { get; set; }
    public List<string>? Tags { get; set; }
    public string IndexName { get; set; } = "main";
}
