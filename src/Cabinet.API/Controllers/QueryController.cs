using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.API.Middleware;
using Cabinet.Query;
using Cabinet.Query.Bql;
using Cabinet.Query.NaturalLanguage;

namespace Cabinet.API.Controllers;

/// <summary>
/// Query execution API
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class QueryController : ControllerBase
{
    private readonly IQueryEngine _queryEngine;
    private readonly IQueryParser _queryParser;
    private readonly ISavedQueryStorage _savedQueryStorage;
    private readonly IAuditLogger _auditLogger;
    private readonly INaturalLanguageTranslator? _nlTranslator;
    private readonly Services.QueryCacheService? _queryCache;
    private readonly ILogger<QueryController> _logger;

    public QueryController(
        IQueryEngine queryEngine,
        IQueryParser queryParser,
        ISavedQueryStorage savedQueryStorage,
        IAuditLogger auditLogger,
        ILogger<QueryController> logger,
        INaturalLanguageTranslator? nlTranslator = null,
        Services.QueryCacheService? queryCache = null)
    {
        _queryEngine = queryEngine;
        _queryParser = queryParser;
        _savedQueryStorage = savedQueryStorage;
        _auditLogger = auditLogger;
        _nlTranslator = nlTranslator;
        _queryCache = queryCache;
        _logger = logger;
    }

    /// <summary>
    /// Execute a query
    /// </summary>
    [HttpPost("execute")]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(QueryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ExecuteQuery([FromBody] QueryRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return Problem(detail: "Query is required", statusCode: 400);
        }

        if (request.Query.Length > 10_000)
        {
            return Problem(detail: "Query too long. Maximum length is 10,000 characters.", statusCode: 400);
        }

        try
        {
            // Enforce a 60-second query timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

            var startTime = DateTime.UtcNow;

            // Check query cache for dashboard refresh acceleration
            var useCache = request.UseCache;
            if (useCache && _queryCache != null)
            {
                var cached = _queryCache.TryGet(request.Query);
                if (cached != null)
                {
                    var cacheTime = DateTime.UtcNow - startTime;
                    Response.Headers["X-Cache"] = "HIT";
                    return Ok(new QueryResponse
                    {
                        Query = request.Query,
                        Results = cached.Events.Cast<object>().Take(50_000).ToList(),
                        Count = Math.Min(cached.Events.Count, 50_000),
                        ExecutionTimeMs = (int)cacheTime.TotalMilliseconds,
                        Timestamp = DateTime.UtcNow
                    });
                }
            }

            var context = new QueryExecutionContext
            {
                Query = request.Query,
                Limit = 50_000
            };
            var result = await _queryEngine.ExecuteAsync(context, timeoutCts.Token);
            var executionTime = DateTime.UtcNow - startTime;

            // Cache the result for future requests
            if (_queryCache != null)
            {
                _queryCache.Set(request.Query, result);
                Response.Headers["X-Cache"] = "MISS";
            }

            // Cap results to prevent OOM from unbounded queries.
            // Upstream protection: QueryExecutionContext.Limit (set to 50K above) is propagated
            // to QueryContext.Limit via BqlOptimizer, which clamps to max 100K. The storage layer
            // (ILogStorage) enforces this limit natively, so result.Events is already bounded.
            // This secondary cap is defense-in-depth in case the optimizer or storage bypasses the limit.
            const int MaxResultCount = 50_000;
            var wasTruncated = result.Events.Count > MaxResultCount;
            var resultList = wasTruncated
                ? result.Events.Take(MaxResultCount).Cast<object>().ToList()
                : result.Events.Cast<object>().ToList();

            // Add truncation header so clients know results were capped
            var truncated = wasTruncated || result.Statistics?.Truncated == true;
            if (truncated)
                Response.Headers["X-Result-Truncated"] = "true";

            // Audit log the query
            await _auditLogger.LogAsync(new AuditEvent
            {
                PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
                Action = AuditAction.Search,
                ResourceType = "Query",
                Description = $"Executed query: {request.Query[..Math.Min(200, request.Query.Length)]}",
                SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
            });

            return Ok(new QueryResponse
            {
                Query = request.Query,
                Results = resultList,
                Count = resultList.Count,
                ExecutionTimeMs = (int)executionTime.TotalMilliseconds,
                Timestamp = DateTime.UtcNow
            });
        }
        catch (QueryParseException ex)
        {
            return Problem(detail: $"Invalid query syntax: {ex.Message}", statusCode: 400);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Problem(detail: "Query execution timed out (60s limit)", statusCode: 408, title: "Request Timeout");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Query execution failed");
            return Problem(detail: "Query execution failed", statusCode: 500);
        }
    }

    /// <summary>
    /// Stream query results as newline-delimited JSON (NDJSON).
    /// Each result is a complete JSON object on its own line, enabling
    /// incremental parsing without waiting for the full result set.
    /// For simple search queries, results stream directly from storage
    /// without materializing all events in memory.
    /// </summary>
    [HttpPost("stream")]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task StreamQuery([FromBody] QueryRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(
                new ProblemDetails { Detail = "Query is required", Status = 400, Title = "Bad Request" },
                cancellationToken);
            return;
        }

        if (request.Query.Length > 10_000)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(
                new ProblemDetails { Detail = "Query too long. Maximum length is 10,000 characters.", Status = 400, Title = "Bad Request" },
                cancellationToken);
            return;
        }

        // Validate query syntax before starting the stream to enable proper error responses.
        // Once streaming begins, the HTTP status code is committed and cannot be changed.
        try
        {
            _queryParser.Parse(request.Query);
        }
        catch (QueryParseException ex)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(
                new ProblemDetails { Detail = $"Invalid query syntax: {ex.Message}", Status = 400, Title = "Bad Request" },
                cancellationToken);
            return;
        }

        // Begin NDJSON streaming response
        Response.ContentType = "application/x-ndjson";
        Response.Headers.CacheControl = "no-cache";

        // Enforce a 5-minute timeout on streaming queries to prevent resource exhaustion
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        var context = new QueryExecutionContext
        {
            Query = request.Query,
            Limit = 100_000,
            Timeout = TimeSpan.FromMinutes(5)
        };

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        try
        {
            await foreach (var evt in _queryEngine.ExecuteStreamAsync(context, timeoutCts.Token))
            {
                object eventObj = evt;
                var line = JsonSerializer.Serialize(eventObj, eventObj.GetType(), jsonOptions);
                await Response.WriteAsync(line, timeoutCts.Token);
                await Response.WriteAsync("\n", timeoutCts.Token);
                await Response.Body.FlushAsync(timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Query timeout — stream ends; client sees incomplete NDJSON
            _logger.LogWarning("Stream query timed out: {Query}",
                request.Query[..Math.Min(200, request.Query.Length)]);
        }
        catch (Exception ex)
        {
            // Error during streaming — can't change status code since headers are committed
            _logger.LogError(ex, "Stream query failed during execution: {Query}",
                request.Query[..Math.Min(200, request.Query.Length)]);
        }
    }

    /// <summary>
    /// Validate a query without executing
    /// </summary>
    [HttpPost("validate")]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(ValidationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult ValidateQuery([FromBody] QueryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return Problem(detail: "Query is required", statusCode: 400);

        if (request.Query.Length > 10_000)
            return Problem(detail: "Query too long. Maximum length is 10,000 characters.", statusCode: 400);

        try
        {
            // Use the same BqlParser that ExecuteQuery uses — ensures consistent validation
            var plan = _queryParser.Parse(request.Query);

            return Ok(new ValidationResponse
            {
                IsValid = true,
                Query = request.Query,
                OperatorCount = plan.Operators.Count,
                Operators = plan.Operators.Select(o => o.Name).ToList()
            });
        }
        catch (Exception ex)
        {
            return Ok(new ValidationResponse
            {
                IsValid = false,
                Query = request.Query,
                Error = ex.Message
            });
        }
    }

    /// <summary>
    /// Get saved queries
    /// </summary>
    [HttpGet("saved")]
    [RequireCapability(Capability.SavedQueries)]
    [ProducesResponseType(typeof(IReadOnlyList<SavedQuery>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSavedQueries(
        [FromQuery] int offset = 0,
        [FromQuery] int count = 100)
    {
        count = Math.Clamp(count, 1, 1000);
        offset = Math.Max(0, offset);

        // Note: GetAllAsync returns the full list; Skip/Take happens in-memory.
        // For production scale, ISavedQueryStorage should accept offset/count parameters
        // to push pagination to the database. This is acceptable for v1.0 since saved
        // queries are a low-cardinality object (typically < 100 per deployment).
        var queries = await _savedQueryStorage.GetAllAsync();
        Response.Headers["X-Total-Count"] = queries.Count.ToString();
        return Ok(queries.Skip(offset).Take(count));
    }

    /// <summary>
    /// Save a query
    /// </summary>
    [HttpPost("saved")]
    [RequireCapability(Capability.SavedQueries)]
    [ProducesResponseType(typeof(SavedQuery), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [Authorize(Policy = "RequireWritePermission")]
    public async Task<IActionResult> SaveQuery([FromBody] SaveQueryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Problem(detail: "Name is required", statusCode: 400);
        if (string.IsNullOrWhiteSpace(request.Query))
            return Problem(detail: "Query is required", statusCode: 400);

        // Validate BQL syntax before saving
        try
        {
            _queryParser.Parse(request.Query);
        }
        catch (QueryParseException ex)
        {
            return Problem(detail: $"Invalid query syntax: {ex.Message}", statusCode: 400);
        }

        var savedQuery = new SavedQuery
        {
            Name = request.Name,
            Query = request.Query,
            Description = request.Description,
            CreatedBy = User.Identity?.Name ?? "unknown",
            CreatedAt = DateTime.UtcNow
        };

        await _savedQueryStorage.SaveAsync(savedQuery);
        _logger.LogInformation("Saved query '{Name}' (id: {Id})", savedQuery.Name, savedQuery.Id);

        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Create,
            ResourceType = "SavedQuery",
            ResourceId = savedQuery.Id,
            Description = $"Saved query '{savedQuery.Name}'",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return CreatedAtAction(nameof(GetSavedQuery), new { id = savedQuery.Id }, savedQuery);
    }

    /// <summary>
    /// Get a saved query by ID
    /// </summary>
    [HttpGet("saved/{id}")]
    [RequireCapability(Capability.SavedQueries)]
    [ProducesResponseType(typeof(SavedQuery), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSavedQuery(string id)
    {
        var query = await _savedQueryStorage.GetByIdAsync(id);
        if (query == null)
            return Problem(detail: "Saved query not found", statusCode: 404);

        // Track usage in background — GET should be idempotent from the client's perspective
        _ = Task.Run(async () =>
        {
            try
            {
                query.IncrementUsageCount();
                query.LastUsedAt = DateTime.UtcNow;
                await _savedQueryStorage.SaveAsync(query);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to update usage tracking for saved query {Id}", id);
            }
        });

        return Ok(query);
    }

    /// <summary>
    /// Update a saved query
    /// </summary>
    [HttpPut("saved/{id}")]
    [RequireCapability(Capability.SavedQueries)]
    [ProducesResponseType(typeof(SavedQuery), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [Authorize(Policy = "RequireWritePermission")]
    public async Task<IActionResult> UpdateSavedQuery(string id, [FromBody] SaveQueryRequest request)
    {
        var existing = await _savedQueryStorage.GetByIdAsync(id);
        if (existing == null)
            return Problem(detail: "Saved query not found", statusCode: 404);

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            if (request.Name.Length > 200)
                return Problem(detail: "Name must be 200 characters or fewer", statusCode: 400);
            existing.Name = request.Name;
        }

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            if (request.Query.Length > 10_000)
                return Problem(detail: "Query too long. Maximum length is 10,000 characters.", statusCode: 400);

            // Validate BQL syntax before updating
            try
            {
                _queryParser.Parse(request.Query);
            }
            catch (QueryParseException ex)
            {
                return Problem(detail: $"Invalid query syntax: {ex.Message}", statusCode: 400);
            }
            existing.Query = request.Query;
        }

        if (request.Description != null)
            existing.Description = request.Description;

        await _savedQueryStorage.SaveAsync(existing);

        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Update,
            ResourceType = "SavedQuery",
            ResourceId = id,
            Description = $"Updated saved query '{existing.Name}'",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return Ok(existing);
    }

    /// <summary>
    /// Delete a saved query
    /// </summary>
    [HttpDelete("saved/{id}")]
    [RequireCapability(Capability.SavedQueries)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Authorize(Policy = "RequireWritePermission")]
    public async Task<IActionResult> DeleteSavedQuery(string id)
    {
        var deleted = await _savedQueryStorage.DeleteAsync(id);
        if (!deleted)
            return Problem(detail: "Saved query not found", statusCode: 404);

        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Delete,
            ResourceType = "SavedQuery",
            ResourceId = id,
            Description = $"Deleted saved query",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return NoContent();
    }

    /// <summary>
    /// Get query suggestions
    /// </summary>
    [HttpGet("suggestions")]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(List<QuerySuggestion>), StatusCodes.Status200OK)]
    public IActionResult GetSuggestions([FromQuery] string? partial = null)
    {
        var suggestions = new List<QuerySuggestion>
        {
            new() { Text = "search", Description = "Full-text search", Category = "Search" },
            new() { Text = "find", Description = "Full-text search (BQL)", Category = "Search" },
            new() { Text = "where", Description = "Filter events by condition", Category = "Filter" },
            new() { Text = "stats", Description = "Aggregation: count, sum, avg, min, max, dc, median, stdev, variance, percentile", Category = "Aggregate" },
            new() { Text = "timechart", Description = "Time-series aggregation by span", Category = "Aggregate" },
            new() { Text = "top", Description = "Find most common values", Category = "Aggregate" },
            new() { Text = "sort", Description = "Sort results by field", Category = "Order" },
            new() { Text = "head", Description = "Limit to first N results", Category = "Order" },
            new() { Text = "tail", Description = "Last N results", Category = "Order" },
            new() { Text = "limit", Description = "Limit result count", Category = "Order" },
            new() { Text = "dedup", Description = "Remove duplicate events", Category = "Transform" },
            new() { Text = "eval", Description = "Create computed fields", Category = "Transform" },
            new() { Text = "rex", Description = "Regex field extraction", Category = "Transform" },
            new() { Text = "rename", Description = "Rename fields", Category = "Transform" },
            new() { Text = "fields", Description = "Select specific fields", Category = "Transform" },
            new() { Text = "table", Description = "Project fields (SPL)", Category = "Transform" },
            new() { Text = "fieldalias", Description = "Create field aliases", Category = "Transform" },
            new() { Text = "transaction", Description = "Group events into transactions", Category = "Transform" },
            new() { Text = "eventstats", Description = "Append aggregation values to each event", Category = "Aggregate" },
            new() { Text = "streamstats", Description = "Running/cumulative statistics", Category = "Aggregate" },
            new() { Text = "fillnull", Description = "Fill null values with defaults", Category = "Transform" },
            new() { Text = "bucket", Description = "Numeric/time binning", Category = "Transform" },
            new() { Text = "makeresults", Description = "Generate synthetic events", Category = "Generate" },
            new() { Text = "mvexpand", Description = "Expand multi-valued fields", Category = "Transform" },
            new() { Text = "chart", Description = "Create chart-ready data", Category = "Aggregate" },
            new() { Text = "filldown", Description = "Propagate non-null values downward", Category = "Transform" },
            new() { Text = "addcoltotals", Description = "Add column totals row", Category = "Aggregate" },
            new() { Text = "inputlookup", Description = "Read from lookup table", Category = "Generate" },
            new() { Text = "lookup", Description = "Enrich events from lookup table", Category = "Transform" },
            new() { Text = "rare", Description = "Find least common values (inverse of top)", Category = "Aggregate" },
            new() { Text = "regex", Description = "Filter events by regex pattern", Category = "Filter" },
            new() { Text = "fieldsummary", Description = "Show statistics about each field", Category = "Aggregate" },
            new() { Text = "xyseries", Description = "Pivot data into tabular form", Category = "Transform" },
            new() { Text = "untable", Description = "Unpivot tabular data back to rows", Category = "Transform" },
            new() { Text = "convert", Description = "Convert field values (ctime, memk, dur2sec)", Category = "Transform" }
        };

        if (!string.IsNullOrWhiteSpace(partial))
        {
            // Limit partial query length to prevent abuse
            var safePartial = partial.Length > 256 ? partial[..256] : partial;
            suggestions = suggestions
                .Where(s => s.Text.StartsWith(safePartial, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return Ok(suggestions);
    }

    /// <summary>
    /// Translate natural language to BQL query
    /// </summary>
    [HttpPost("translate")]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(TranslateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> TranslateQuery([FromBody] TranslateRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            return Problem(detail: "Text is required", statusCode: 400);

        if (_nlTranslator == null)
            return Problem(detail: "Natural language translation is not configured", statusCode: 400);

        var result = await _nlTranslator.TranslateAsync(request.Text, cancellationToken);

        return Ok(new TranslateResponse
        {
            OriginalText = request.Text,
            BqlQuery = result.BqlQuery ?? request.Text,
            Success = result.Success,
            Confidence = result.Confidence,
            IsNaturalLanguage = _nlTranslator.IsNaturalLanguage(request.Text)
        });
    }

    /// <summary>
    /// Explain a query's execution plan without running it
    /// </summary>
    [HttpPost("explain")]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(ExplainResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult ExplainQuery([FromBody] QueryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return Problem(detail: "Query is required", statusCode: 400);

        try
        {
            var plan = _queryParser.Parse(request.Query);

            var stages = plan.Operators.Select(op => new ExplainStage
            {
                Name = op.Name,
                Type = op.GetType().Name.Replace("Legacy", "").Replace("Operator", "")
            }).ToList();

            return Ok(new ExplainResponse
            {
                Query = request.Query,
                Stages = stages,
                StageCount = stages.Count,
                HasSearch = stages.Any(s => s.Name == "search"),
                HasAggregation = stages.Any(s => s.Name is "stats" or "timechart" or "eventstats" or "streamstats" or "chart" or "top" or "rare" or "fieldsummary"),
                HasSort = stages.Any(s => s.Name == "sort"),
                HasLimit = stages.Any(s => s.Name is "limit" or "head" or "tail")
            });
        }
        catch (QueryParseException ex)
        {
            return Problem(detail: $"Invalid query syntax: {ex.Message}", statusCode: 400);
        }
    }

    /// <summary>
    /// Export query results as CSV or JSON
    /// </summary>
    [HttpPost("export")]
    [RequireCapability(Capability.Export)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ExportQuery([FromBody] ExportRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return Problem(detail: "Query is required", statusCode: 400);

        if (request.Query.Length > 10_000)
            return Problem(detail: "Query too long. Maximum length is 10,000 characters.", statusCode: 400);

        // Clamp MaxRows to prevent OOM from unbounded export requests
        request.MaxRows = Math.Clamp(request.MaxRows, 1, 100_000);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(120)); // 2 min for exports

            // Use QueryExecutionContext to preserve aggregation data (ExpandoObjects).
            // The string-based extension method converts to LogEvent, which loses stats columns.
            var context = new QueryExecutionContext
            {
                Query = request.Query,
                Limit = request.MaxRows,
                Timeout = TimeSpan.FromSeconds(120)
            };
            var queryResult = await _queryEngine.ExecuteAsync(context, timeoutCts.Token);
            var resultList = queryResult.Events.Take(request.MaxRows).ToList();

            await _auditLogger.LogAsync(new AuditEvent
            {
                PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
                Action = AuditAction.Export,
                ResourceType = "Query",
                Description = $"Exported {resultList.Count} results from: {request.Query[..Math.Min(200, request.Query.Length)]}",
                SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
            });

            if (request.Format.Equals("csv", StringComparison.OrdinalIgnoreCase))
            {
                // Stream CSV directly to response body to avoid materializing large exports in memory
                Response.ContentType = "text/csv";
                Response.Headers.ContentDisposition = "attachment; filename=export.csv";
                await WriteCsvToStreamAsync(Response.Body, resultList);
                return new EmptyResult();
            }

            // Default: JSON
            return Ok(new ExportResponse
            {
                Format = request.Format,
                Count = resultList.Count,
                Data = resultList
            });
        }
        catch (QueryParseException ex)
        {
            return Problem(detail: $"Invalid query syntax: {ex.Message}", statusCode: 400);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Problem(detail: "Export timed out (120s limit)", statusCode: 408, title: "Request Timeout");
        }
    }

    /// <summary>
    /// Get field summary statistics for an index or query
    /// </summary>
    [HttpPost("fieldsummary")]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(QueryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> FieldSummary(
        [FromBody] FieldSummaryRequest request,
        CancellationToken cancellationToken)
    {
        // Build a BQL query that pipes into fieldsummary
        var baseQuery = string.IsNullOrWhiteSpace(request.Query) ? "find *" : request.Query;
        var bqlQuery = $"{baseQuery} | fieldsummary";

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

            var startTime = DateTime.UtcNow;

            // Use QueryExecutionContext path (not the extension method) to preserve
            // raw ExpandoObject results from fieldsummary — the extension method
            // converts to LogEvent which loses aggregation fields.
            var context = new QueryExecutionContext
            {
                Query = bqlQuery,
                Limit = 10_000
            };
            var result = await _queryEngine.ExecuteAsync(context, timeoutCts.Token);
            var executionTime = DateTime.UtcNow - startTime;
            var resultList = result.Events.Take(10_000).Cast<object>().ToList();

            return Ok(new QueryResponse
            {
                Query = bqlQuery,
                Results = resultList,
                Count = resultList.Count,
                ExecutionTimeMs = (int)executionTime.TotalMilliseconds,
                Timestamp = DateTime.UtcNow
            });
        }
        catch (QueryParseException ex)
        {
            return Problem(detail: $"Invalid query syntax: {ex.Message}", statusCode: 400);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Problem(detail: "Field summary timed out (60s limit)", statusCode: 408, title: "Request Timeout");
        }
    }

    /// <summary>
    /// Stream dynamic results (ExpandoObject or LogEvent) as CSV directly to the response body.
    /// Avoids materializing the entire CSV in memory for large exports.
    /// </summary>
    private static async Task WriteCsvToStreamAsync(Stream stream, List<dynamic> events)
    {
        if (events.Count == 0) return;

        using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8, bufferSize: 8192, leaveOpen: true);

        // Collect all unique keys across all events
        var allKeys = new List<string>();
        var keySet = new HashSet<string>(StringComparer.Ordinal);

        foreach (dynamic evt in events)
        {
            IEnumerable<string> keys;
            if (evt is IDictionary<string, object?> dict)
                keys = dict.Keys;
            else if (evt is LogEvent le)
                keys = new[] { "Id", "Timestamp", "Severity", "Host", "SourceType", "Index", "RawLine" }
                    .Concat(le.Fields.Keys);
            else
                continue;

            foreach (var key in keys)
            {
                if (keySet.Add(key))
                    allKeys.Add(key);
            }
        }

        await writer.WriteLineAsync(string.Join(",", allKeys.Select(EscapeCsvField)));

        foreach (dynamic evt in events)
        {
            var values = new List<string>();
            if (evt is IDictionary<string, object?> dict)
            {
                foreach (var key in allKeys)
                    values.Add(EscapeCsvField(dict.TryGetValue(key, out var v) ? CsvValue(v) : ""));
            }
            else if (evt is LogEvent le)
            {
                foreach (var key in allKeys)
                {
                    var val = key switch
                    {
                        "Id" => le.Id,
                        "Timestamp" => le.Timestamp.ToString("o"),
                        "Severity" => le.Severity.ToString(),
                        "Host" => le.Host ?? "",
                        "SourceType" => le.SourceType,
                        "Index" => le.Index,
                        "RawLine" => le.RawLine,
                        _ => le.Fields.TryGetValue(key, out var f) ? f?.ToString() ?? "" : ""
                    };
                    values.Add(EscapeCsvField(val));
                }
            }
            await writer.WriteLineAsync(string.Join(",", values));
        }

        await writer.FlushAsync();
    }

    /// <summary>Text for one CSV cell. Multi-value fields join with semicolons.</summary>
    internal static string CsvValue(object? value) => value switch
    {
        null => "",
        string text => text,
        IEnumerable<object?> values => string.Join(";", values),
        _ => value.ToString() ?? "",
    };

    private static string EscapeCsvField(string value)
    {
        // Prevent CSV formula injection by prefixing dangerous characters with a single quote
        if (value.Length > 0 && (value[0] == '=' || value[0] == '+' || value[0] == '-' || value[0] == '@' || value[0] == '\t' || value[0] == '\r'))
            value = "'" + value;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}

// DTOs
public class QueryRequest
{
    public string Query { get; set; } = string.Empty;
    public Dictionary<string, object>? Parameters { get; set; }
    /// <summary>
    /// When true, returns cached results if available (for dashboard refresh acceleration).
    /// Default: false. Cached results use a configurable TTL (default 30s).
    /// </summary>
    public bool UseCache { get; set; }
}

public class QueryResponse
{
    public string Query { get; set; } = string.Empty;
    public IEnumerable<object> Results { get; set; } = Enumerable.Empty<object>();
    public int Count { get; set; }
    public int ExecutionTimeMs { get; set; }
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// True when more events matched than could be read, so results may be incomplete.
    /// </summary>
    public bool Truncated { get; set; }
}

public class ValidationResponse
{
    public bool IsValid { get; set; }
    public string Query { get; set; } = string.Empty;
    public string? Error { get; set; }
    public int OperatorCount { get; set; }
    public List<string> Operators { get; set; } = new();
}

public class SaveQueryRequest
{
    public string Name { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class QuerySuggestion
{
    public string Text { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
}

public class TranslateRequest
{
    public string Text { get; set; } = string.Empty;
}

public class TranslateResponse
{
    public string OriginalText { get; set; } = string.Empty;
    public string BqlQuery { get; set; } = string.Empty;
    public bool Success { get; set; }
    public double Confidence { get; set; }
    public bool IsNaturalLanguage { get; set; }
}

public class ExplainResponse
{
    public string Query { get; set; } = string.Empty;
    public List<ExplainStage> Stages { get; set; } = new();
    public int StageCount { get; set; }
    public bool HasSearch { get; set; }
    public bool HasAggregation { get; set; }
    public bool HasSort { get; set; }
    public bool HasLimit { get; set; }
}

public class ExplainStage
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}

public class ExportRequest
{
    public string Query { get; set; } = string.Empty;
    public string Format { get; set; } = "json";
    public int MaxRows { get; set; } = 100_000;
    public Dictionary<string, object>? Parameters { get; set; }
}

public class ExportResponse
{
    public string Format { get; set; } = string.Empty;
    public int Count { get; set; }
    public IEnumerable<object> Data { get; set; } = Enumerable.Empty<object>();
}

public class FieldSummaryRequest
{
    public string? Query { get; set; }
}