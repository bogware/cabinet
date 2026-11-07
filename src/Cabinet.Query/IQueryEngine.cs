using Cabinet.Core.Models;

namespace Cabinet.Query;

/// <summary>
/// Query engine interface for executing DSL queries
/// </summary>
public interface IQueryEngine
{
    Task<QueryResult> ExecuteAsync(QueryExecutionContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute a query and stream results as IAsyncEnumerable without materializing all results in memory.
    /// For simple search queries (no pipeline stages), results stream directly from storage.
    /// For queries with pipeline stages (aggregation, sort, etc.), results are materialized internally
    /// then yielded one at a time.
    /// </summary>
    IAsyncEnumerable<dynamic> ExecuteStreamAsync(
        QueryExecutionContext context,
        CancellationToken cancellationToken = default);
}

public class QueryExecutionContext
{
    public required string Query { get; set; }
    public QueryMode Mode { get; set; } = QueryMode.Indexed;
    public string? Path { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int Limit { get; set; } = 10_000;

    /// <summary>
    /// Index to scope the query to.
    /// When set, only events in this index are returned.
    /// </summary>
    public string? Index { get; set; }

    /// <summary>
    /// Maximum query execution time. Defaults to 60 seconds.
    /// The query engine will cancel execution after this timeout.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
}

public enum QueryMode
{
    Indexed,
    Direct
}

public class QueryResult
{
    public List<dynamic> Events { get; set; } = new();
    public List<string>? Columns { get; set; }
    public QueryStatistics? Statistics { get; set; }
}

public class QueryStatistics
{
    public long EventsScanned { get; set; }
    public long EventsMatched { get; set; }
    public TimeSpan ExecutionTime { get; set; }
    public Dictionary<string, long>? FieldCardinality { get; set; }

    /// <summary>
    /// True when more events matched than the engine could read, so filters and aggregates
    /// only saw the newest portion and their results may be incomplete.
    /// </summary>
    public bool Truncated { get; set; }

    /// <summary>
    /// A median or percentile came from a sample of its values, because there were too many to keep.
    /// </summary>
    public bool Approximate { get; set; }
}

public class QueryParseException : Exception
{
    public string? Position { get; set; }
    public QueryParseException(string message, string? position = null) : base(
        position != null ? $"{message} at {position}" : message)
    {
        Position = position;
    }
}

public interface IQueryParser
{
    QueryPlan Parse(string query);
}

public class QueryPlan
{
    public List<QueryOperator> Operators { get; set; } = new();
}

public abstract class QueryOperator
{
    public abstract string Name { get; }
    public abstract Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken cancellationToken = default);
}