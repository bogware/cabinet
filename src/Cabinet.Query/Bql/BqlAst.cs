namespace Cabinet.Query.Bql;

// ─── Top-level query ──────────────────────────────────────────

/// <summary>
/// Root AST node: a pipeline of stages with an optional time range
/// </summary>
public sealed class BqlQuery
{
    public List<BqlStage> Stages { get; } = new();
    public BqlTimeRange? TimeRange { get; set; }
}

/// <summary>
/// Absolute or relative time range for a query
/// </summary>
public sealed class BqlTimeRange
{
    public DateTime? Start { get; set; }
    public DateTime? End { get; set; }
}

// ─── Pipeline stages ──────────────────────────────────────────

/// <summary>
/// Abstract base for all pipeline stages
/// </summary>
public abstract class BqlStage
{
    public abstract string StageName { get; }
}

/// <summary>
/// Full-text search: find/search terms
/// </summary>
public sealed class SearchStage : BqlStage
{
    public override string StageName => "search";
    public string SearchText { get; }

    public SearchStage(string searchText)
    {
        SearchText = searchText;
    }
}

/// <summary>
/// Filter: where condition (supports AND/OR/NOT/parentheses)
/// </summary>
public sealed class WhereStage : BqlStage
{
    public override string StageName => "where";
    public BqlExpression Condition { get; }

    public WhereStage(BqlExpression condition)
    {
        Condition = condition;
    }
}

/// <summary>
/// Aggregation: stats func(field), ... by field1, field2
/// </summary>
public sealed class StatsStage : BqlStage
{
    public override string StageName => "stats";
    public List<AggregateFunction> Aggregations { get; } = new();
    public List<string> GroupByFields { get; } = new();
}

/// <summary>
/// Sort: sort by field asc/desc (BQL) or sort ±field (SPL)
/// </summary>
public sealed class SortStage : BqlStage
{
    public override string StageName => "sort";
    public List<SortField> Fields { get; } = new();
}

/// <summary>
/// Limit results: limit N / head N
/// </summary>
public sealed class LimitStage : BqlStage
{
    public override string StageName => "limit";
    public int Count { get; }

    public LimitStage(int count)
    {
        Count = count;
    }
}

/// <summary>
/// Tail: last N results
/// </summary>
public sealed class TailStage : BqlStage
{
    public override string StageName => "tail";
    public int Count { get; }

    public TailStage(int count)
    {
        Count = count;
    }
}

/// <summary>
/// A single eval assignment: fieldName = expression
/// </summary>
public sealed class EvalAssignment
{
    public string FieldName { get; }
    public BqlExpression Expression { get; }

    public EvalAssignment(string fieldName, BqlExpression expression)
    {
        FieldName = fieldName;
        Expression = expression;
    }
}

/// <summary>
/// Compute/eval: create or transform fields with expressions.
/// Supports multiple comma-separated assignments: eval x = a + b, y = c * d
/// </summary>
public sealed class EvalStage : BqlStage
{
    public override string StageName => "eval";
    public List<EvalAssignment> Assignments { get; } = new();

    public EvalStage(string fieldName, BqlExpression expression)
    {
        Assignments.Add(new EvalAssignment(fieldName, expression));
    }

    public EvalStage(List<EvalAssignment> assignments)
    {
        Assignments.AddRange(assignments);
    }
}

/// <summary>
/// Regex extraction: extract/rex named groups from a field
/// </summary>
public sealed class RexStage : BqlStage
{
    public override string StageName => "rex";
    public string Pattern { get; }
    public string SourceField { get; }

    public RexStage(string pattern, string sourceField = "RawLine")
    {
        Pattern = pattern;
        SourceField = sourceField;
    }
}

/// <summary>
/// A single rename mapping: oldName as newName
/// </summary>
public sealed class RenameMapping
{
    public string OldName { get; }
    public string NewName { get; }

    public RenameMapping(string oldName, string newName)
    {
        OldName = oldName;
        NewName = newName;
    }
}

/// <summary>
/// Rename: rename field as newname.
/// Supports multiple comma-separated mappings: rename old1 as new1, old2 as new2
/// </summary>
public sealed class RenameStage : BqlStage
{
    public override string StageName => "rename";
    public List<RenameMapping> Mappings { get; } = new();

    public RenameStage(string oldName, string newName)
    {
        Mappings.Add(new RenameMapping(oldName, newName));
    }

    public RenameStage(List<RenameMapping> mappings)
    {
        Mappings.AddRange(mappings);
    }
}

/// <summary>
/// Field projection: select/table/fields f1, f2, f3
/// </summary>
public sealed class TableStage : BqlStage
{
    public override string StageName => "table";
    public List<string> Fields { get; }

    public TableStage(List<string> fields)
    {
        Fields = fields;
    }
}

/// <summary>
/// Dedup: unique by field / dedup field1, field2.
/// Supports multiple comma-separated fields for composite dedup keys.
/// </summary>
public sealed class DedupStage : BqlStage
{
    public override string StageName => "dedup";
    public List<string> Fields { get; }

    public DedupStage(string field)
    {
        Fields = new List<string> { field };
    }

    public DedupStage(List<string> fields)
    {
        Fields = fields;
    }
}

/// <summary>
/// Top N values: top N field
/// </summary>
public sealed class TopStage : BqlStage
{
    public override string StageName => "top";
    public int Count { get; }
    public string Field { get; }

    public TopStage(int count, string field)
    {
        Count = count;
        Field = field;
    }
}

/// <summary>
/// Time-series buckets: timechart span=Xh agg by field
/// </summary>
public sealed class TimechartStage : BqlStage
{
    public override string StageName => "timechart";
    public TimeSpan Span { get; }
    public string Aggregation { get; }
    public string? AggregationField { get; }
    public string? ByField { get; }

    public TimechartStage(TimeSpan span, string aggregation, string? byField = null, string? aggregationField = null)
    {
        Span = span;
        Aggregation = aggregation;
        ByField = byField;
        AggregationField = aggregationField;
    }
}

// ─── Aggregate functions ──────────────────────────────────────

/// <summary>
/// An aggregation function (count, sum, avg, min, max)
/// </summary>
public sealed class AggregateFunction
{
    public string Name { get; }
    public string? Field { get; }
    public string? Alias { get; set; }

    public AggregateFunction(string name, string? field = null)
    {
        Name = name.ToLowerInvariant();
        Field = field;
    }
}

/// <summary>
/// A sort field with direction
/// </summary>
public sealed class SortField
{
    public string Field { get; }
    public bool Descending { get; }

    public SortField(string field, bool descending = false)
    {
        Field = field;
        Descending = descending;
    }
}

// ─── Expressions (for where, eval) ───────────────────────────

/// <summary>
/// Abstract base for all expressions
/// </summary>
public abstract class BqlExpression { }

/// <summary>
/// Binary logical: left AND/OR right
/// </summary>
public sealed class LogicalExpression : BqlExpression
{
    public BqlExpression Left { get; }
    public string Operator { get; }
    public BqlExpression Right { get; }

    public LogicalExpression(BqlExpression left, string op, BqlExpression right)
    {
        Left = left;
        Operator = op.ToUpperInvariant();
        Right = right;
    }
}

/// <summary>
/// Unary NOT
/// </summary>
public sealed class NotExpression : BqlExpression
{
    public BqlExpression Operand { get; }

    public NotExpression(BqlExpression operand)
    {
        Operand = operand;
    }
}

/// <summary>
/// Comparison: field op value (=, !=, >, >=, &lt;, &lt;=)
/// </summary>
public sealed class ComparisonExpression : BqlExpression
{
    public BqlExpression Left { get; }
    public string Operator { get; }
    public BqlExpression Right { get; }

    public ComparisonExpression(BqlExpression left, string op, BqlExpression right)
    {
        Left = left;
        Operator = op;
        Right = right;
    }
}

/// <summary>
/// Arithmetic: left +/-/*// right
/// </summary>
public sealed class ArithmeticExpression : BqlExpression
{
    public BqlExpression Left { get; }
    public string Operator { get; }
    public BqlExpression Right { get; }

    public ArithmeticExpression(BqlExpression left, string op, BqlExpression right)
    {
        Left = left;
        Operator = op;
        Right = right;
    }
}

/// <summary>
/// Field reference
/// </summary>
public sealed class FieldExpression : BqlExpression
{
    public string Name { get; }

    public FieldExpression(string name)
    {
        Name = name;
    }
}

/// <summary>
/// Literal value (string, number, bool, null)
/// </summary>
public sealed class LiteralExpression : BqlExpression
{
    public object? Value { get; }

    public LiteralExpression(object? value)
    {
        Value = value;
    }
}

/// <summary>
/// Function call: name(args)
/// </summary>
public sealed class FunctionExpression : BqlExpression
{
    public string Name { get; }
    public List<BqlExpression> Arguments { get; }

    public FunctionExpression(string name, List<BqlExpression> arguments)
    {
        Name = name.ToLowerInvariant();
        Arguments = arguments;
    }
}

/// <summary>
/// IN expression: field IN (val1, val2, val3)
/// </summary>
public sealed class InExpression : BqlExpression
{
    public BqlExpression Field { get; }
    public List<BqlExpression> Values { get; }

    public InExpression(BqlExpression field, List<BqlExpression> values)
    {
        Field = field;
        Values = values;
    }
}

/// <summary>
/// LIKE expression: field LIKE "%pattern%"
/// </summary>
public sealed class LikeExpression : BqlExpression
{
    public BqlExpression Field { get; }
    public string Pattern { get; }

    public LikeExpression(BqlExpression field, string pattern)
    {
        Field = field;
        Pattern = pattern;
    }
}

// ─── P3: Additional pipeline stages ─────────────────────────

/// <summary>
/// Field aliases: fieldalias name=original_field, name2=original_field2
/// </summary>
public sealed class FieldAliasStage : BqlStage
{
    public override string StageName => "fieldalias";
    public Dictionary<string, string> Aliases { get; }

    public FieldAliasStage(Dictionary<string, string> aliases)
    {
        Aliases = aliases;
    }
}

/// <summary>
/// Transaction: group events into transactions by shared field values
/// </summary>
public sealed class TransactionStage : BqlStage
{
    public override string StageName => "transaction";
    public List<string> Fields { get; }
    public TimeSpan? MaxSpan { get; }
    public int? MaxEvents { get; }

    public TransactionStage(List<string> fields, TimeSpan? maxSpan = null, int? maxEvents = null)
    {
        Fields = fields;
        MaxSpan = maxSpan;
        MaxEvents = maxEvents;
    }
}

// ─── EventStats / StreamStats stages ────────────────────────

/// <summary>
/// EventStats: compute aggregations and append results to every event (like stats but non-reducing).
/// Syntax: | eventstats count, avg(duration) by host
/// </summary>
public sealed class EventStatsStage : BqlStage
{
    public override string StageName => "eventstats";
    public List<AggregateFunction> Aggregations { get; } = new();
    public List<string> GroupByFields { get; } = new();
}

/// <summary>
/// StreamStats: compute running/cumulative statistics as events stream through.
/// Syntax: | streamstats window=10 count as running_count by host
/// </summary>
public sealed class StreamStatsStage : BqlStage
{
    public override string StageName => "streamstats";
    public List<AggregateFunction> Aggregations { get; } = new();
    public List<string> GroupByFields { get; } = new();
    public int Window { get; set; }
}

// ─── Stub stages (not yet implemented) ──────────────────────

/// <summary>
/// Lookup stage — enrich events from a lookup table.
/// Syntax: | lookup &lt;filename&gt; &lt;field&gt; [OUTPUT &lt;field1&gt;, &lt;field2&gt;]
/// </summary>
public sealed class LookupStage : BqlStage
{
    public override string StageName => "lookup";
    public required string LookupSource { get; set; }
    public required string KeyField { get; set; }
    public List<string> OutputFields { get; set; } = new();
}

/// <summary>
/// Append stage — append results of a subsearch to the current result set.
/// Syntax: | append [&lt;subsearch&gt;]
/// </summary>
public sealed class AppendStage : BqlStage
{
    public override string StageName => "append";
    public required string SubsearchQuery { get; set; }
}

/// <summary>
/// Join stage — SQL-style join between the current result set and a subsearch.
/// Syntax: | join type=&lt;inner|outer|left&gt; &lt;field&gt; [&lt;subsearch&gt;]
/// </summary>
public sealed class JoinStage : BqlStage
{
    public override string StageName => "join";
    public required string JoinField { get; set; }
    public string JoinType { get; set; } = "inner";
    public required string SubsearchQuery { get; set; }
}

// ─── Splunk parity stages ────────────────────────────────────

/// <summary>
/// Fill null values: fillnull [value=X] [field1, field2, ...]
/// Replaces null/missing field values with a default value.
/// </summary>
public sealed class FillNullStage : BqlStage
{
    public override string StageName => "fillnull";
    public string DefaultValue { get; set; } = "0";
    public List<string> Fields { get; set; } = new();
}

/// <summary>
/// Bucket/bin: bucket field [span=X] [as alias]
/// Groups numeric or time values into discrete buckets.
/// </summary>
public sealed class BucketStage : BqlStage
{
    public override string StageName => "bucket";
    public string Field { get; set; } = string.Empty;
    public TimeSpan? Span { get; set; }
    public double? NumericSpan { get; set; }
    public string? Alias { get; set; }
}

/// <summary>
/// Make results: makeresults [count=N]
/// Generates synthetic events with _time field.
/// </summary>
public sealed class MakeResultsStage : BqlStage
{
    public override string StageName => "makeresults";
    public int Count { get; set; } = 1;
}

/// <summary>
/// MV expand: mvexpand field [limit=N]
/// Expands multi-valued fields into separate events.
/// </summary>
public sealed class MvExpandStage : BqlStage
{
    public override string StageName => "mvexpand";
    public string Field { get; set; } = string.Empty;
    public int Limit { get; set; } = 1000;
}

/// <summary>
/// Chart: chart agg by field1, field2
/// Creates chart-ready data (alias for stats with two group-by fields creating columns).
/// </summary>
public sealed class ChartStage : BqlStage
{
    public override string StageName => "chart";
    public List<AggregateFunction> Aggregations { get; } = new();
    public string? OverField { get; set; }
    public string? ByField { get; set; }
}

/// <summary>
/// Fill down: filldown field1, field2, ...
/// Propagates non-null values downward through events.
/// </summary>
public sealed class FillDownStage : BqlStage
{
    public override string StageName => "filldown";
    public List<string> Fields { get; set; } = new();
}

/// <summary>
/// Add column totals: addcoltotals [fieldname=label] [field1, field2, ...]
/// Appends a row with column totals for numeric fields.
/// </summary>
public sealed class AddColTotalsStage : BqlStage
{
    public override string StageName => "addcoltotals";
    public string LabelField { get; set; } = "TOTALS";
    public List<string> Fields { get; set; } = new();
}

/// <summary>
/// Input lookup: inputlookup &lt;lookup_name&gt;
/// Reads all rows from a lookup table as events.
/// </summary>
public sealed class InputLookupStage : BqlStage
{
    public override string StageName => "inputlookup";
    public string LookupName { get; set; } = string.Empty;
}

/// <summary>
/// Rare: rare [N] field — least common values (inverse of top).
/// </summary>
public sealed class RareStage : BqlStage
{
    public override string StageName => "rare";
    public int Count { get; set; } = 10;
    public string Field { get; set; } = string.Empty;
}

/// <summary>
/// Regex filter: regex field=pattern — keep only events where field matches regex.
/// </summary>
public sealed class RegexStage : BqlStage
{
    public override string StageName => "regex";
    public string Field { get; set; } = "RawLine";
    public string Pattern { get; set; } = string.Empty;
    public bool Negate { get; set; }
}

/// <summary>
/// Field summary: fieldsummary — show statistics about each field (count, distinct count, min, max, etc.)
/// </summary>
public sealed class FieldSummaryStage : BqlStage
{
    public override string StageName => "fieldsummary";
    public int MaxValues { get; set; } = 10;
}

/// <summary>
/// XY Series: xyseries row_field column_field value_field — pivot data into tabular form.
/// </summary>
public sealed class XyseriesStage : BqlStage
{
    public override string StageName => "xyseries";
    public string RowField { get; set; } = string.Empty;
    public string ColumnField { get; set; } = string.Empty;
    public string ValueField { get; set; } = string.Empty;
}

/// <summary>
/// Untable: untable row_field column_field value_field — unpivot tabular data back to rows.
/// </summary>
public sealed class UntableStage : BqlStage
{
    public override string StageName => "untable";
    public string RowField { get; set; } = string.Empty;
    public string ColumnField { get; set; } = string.Empty;
    public string ValueField { get; set; } = string.Empty;
}

// ─── Macro and Subsearch stages ─────────────────────────────

/// <summary>
/// Macro stage: macro &lt;name&gt; [arg1, arg2, ...] — expand a named macro from knowledge storage.
/// The macro definition is a BQL pipeline fragment that replaces this stage at execution time.
/// Parameters in the macro definition use $param$ syntax and are substituted with the provided arguments.
/// </summary>
public sealed class MacroStage : BqlStage
{
    public override string StageName => "macro";
    public string MacroName { get; }
    public List<string> Arguments { get; }

    public MacroStage(string macroName, List<string>? arguments = null)
    {
        MacroName = macroName;
        Arguments = arguments ?? new List<string>();
    }
}

/// <summary>
/// Subsearch stage: subsearch [&lt;inner_query&gt;] — execute an inner query and use its results
/// to filter the outer pipeline by matching field values.
/// The inner query is enclosed in [...] brackets and executed first; its result fields
/// are used to create an IN-style filter on the outer result set.
/// </summary>
public sealed class SubsearchStage : BqlStage
{
    public override string StageName => "subsearch";
    public string SubsearchQuery { get; }

    public SubsearchStage(string subsearchQuery)
    {
        SubsearchQuery = subsearchQuery;
    }
}
