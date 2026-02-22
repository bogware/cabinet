using System.Diagnostics;
using System.Dynamic;
using System.Runtime.CompilerServices;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cabinet.Query;

public class QueryEngine : IQueryEngine
{
    private readonly ILogStorage _storage;
    private readonly IQueryParser _parser;
    private readonly ILogger<QueryEngine> _logger;

    public QueryEngine(
        ILogStorage storage,
        IQueryParser parser,
        ILogger<QueryEngine> logger)
    {
        _storage = storage;
        _parser = parser;
        _logger = logger;
    }

    public async Task<QueryResult> ExecuteAsync(QueryExecutionContext context, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new QueryResult();

        try
        {
            // Parse the query
            var plan = _parser.Parse(context.Query);

            // Build storage query context
            var storageContext = new QueryContext
            {
                StartTime = context.StartTime,
                EndTime = context.EndTime,
                Limit = (int)Math.Min((long)context.Limit * 10, int.MaxValue) // Overfetch for pipeline processing
            };

            // Execute initial search/fetch
            IEnumerable<dynamic> events;

            if (context.Mode == QueryMode.Direct && !string.IsNullOrEmpty(context.Path))
            {
                // Direct mode - read from files
                events = await ReadDirectAsync(context.Path, plan, cancellationToken);
            }
            else
            {
                // Indexed mode - query from storage
                events = await ReadStorageAsync(storageContext, plan, cancellationToken);
            }

            // Execute pipeline operators
            foreach (var op in plan.Operators)
            {
                events = await op.ExecuteAsync(events, cancellationToken);
            }

            // Materialize results
            result.Events = events.Take(context.Limit).ToList();

            // Extract columns
            if (result.Events.Any())
            {
                result.Columns = ExtractColumns(result.Events.First());
            }

            // Calculate statistics
            result.Statistics = new QueryStatistics
            {
                EventsScanned = result.Events.Count,
                EventsMatched = result.Events.Count,
                ExecutionTime = stopwatch.Elapsed
            };

            _logger.LogInformation("Query executed in {Ms}ms, returned {Count} events",
                stopwatch.ElapsedMilliseconds, result.Events.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Query execution failed");
            throw new QueryParseException($"Query execution failed: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Stream query results by materializing via ExecuteAsync, then yielding one at a time.
    /// </summary>
    public async IAsyncEnumerable<dynamic> ExecuteStreamAsync(
        QueryExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(context, cancellationToken);
        foreach (var evt in result.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return evt;
        }
    }

    private async Task<IEnumerable<dynamic>> ReadStorageAsync(
        QueryContext context,
        QueryPlan plan,
        CancellationToken cancellationToken)
    {
        var events = new List<dynamic>();

        await foreach (var evt in _storage.QueryAsync(context, cancellationToken))
        {
            events.Add(ConvertToExpando(evt));
        }

        return events;
    }

    private async Task<IEnumerable<dynamic>> ReadDirectAsync(
        string path,
        QueryPlan plan,
        CancellationToken cancellationToken)
    {
        var events = new List<dynamic>();

        // Simple file reading for direct mode
        if (File.Exists(path))
        {
            var lines = await File.ReadAllLinesAsync(path, cancellationToken);
            foreach (var line in lines)
            {
                dynamic evt = new ExpandoObject();
                evt.RawLine = line;
                evt.Timestamp = DateTime.UtcNow;
                evt.SourceFile = path;
                events.Add(evt);
            }
        }

        return events;
    }

    private dynamic ConvertToExpando(LogEvent logEvent)
    {
        dynamic expando = new ExpandoObject();
        var dict = (IDictionary<string, object?>)expando;

        dict["Id"] = logEvent.Id;
        dict["Timestamp"] = logEvent.Timestamp;
        dict["SourceType"] = logEvent.SourceType;
        dict["Host"] = logEvent.Host;
        dict["Index"] = logEvent.Index;
        dict["Severity"] = logEvent.Severity.ToString();
        dict["RawLine"] = logEvent.RawLine;
        dict["SourceFile"] = logEvent.SourceFile;
        dict["LineNumber"] = logEvent.LineNumber;

        // Add fields
        foreach (var field in logEvent.Fields)
        {
            dict[field.Key] = field.Value;
        }

        // Add tags as comma-separated string
        if (logEvent.Tags.Any())
        {
            dict["Tags"] = string.Join(", ", logEvent.Tags);
        }

        return expando;
    }

    private List<string> ExtractColumns(dynamic evt)
    {
        if (evt is IDictionary<string, object?> dict)
        {
            return dict.Keys.ToList();
        }

        return new List<string> { "Timestamp", "Severity", "RawLine" };
    }
}