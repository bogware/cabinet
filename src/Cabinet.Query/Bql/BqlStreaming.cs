using System.Globalization;
using Cabinet.Core.Interfaces;

namespace Cabinet.Query.Bql;

/// <summary>
/// Limits on what one query can hold in memory. Time is limited separately, by
/// <see cref="QueryExecutionContext.Timeout"/>.
/// </summary>
public sealed class QueryLimits
{
    /// <summary>
    /// Rows kept for stages that need all their input at once, such as <c>sort</c> without
    /// <c>head</c>, <c>eventstats</c> and <c>transaction</c>. Beyond this the input is cut off and
    /// the result marked truncated. Filters and aggregates don't keep rows, so this doesn't limit them.
    /// </summary>
    public int MaxRowsInMemory { get; set; } = QueryContext.MaxLimit;

    /// <summary>Groups an aggregate can produce before the query fails.</summary>
    public int MaxGroups { get; set; } = 100_000;

    /// <summary>
    /// Values kept across <c>dc</c>, <c>median</c> and percentile aggregates before the query fails.
    /// </summary>
    public long MaxRetainedValues { get; set; } = 20_000_000;

    /// <summary>
    /// Values kept for one <c>median</c> or percentile. Beyond this it samples, and the result is
    /// marked approximate.
    /// </summary>
    public int MaxExactPercentileValues { get; set; } = 100_000;

    /// <summary>
    /// Events read by one query before it stops and is marked truncated. 0 means no limit, leaving
    /// the query's timeout to stop it.
    /// </summary>
    public long MaxEventsScanned { get; set; }
}

/// <summary>A query went over a limit, in a way that would make its answer wrong if it continued.</summary>
public sealed class QueryLimitExceededException : QueryParseException
{
    public QueryLimitExceededException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Part of a query failed in a way that would make its answer wrong, such as a subsearch, join or
/// macro that couldn't run. Reported to the caller instead of returning a quietly different answer.
/// </summary>
public sealed class QueryExecutionException : QueryParseException
{
    public QueryExecutionException(string message)
        : base(message)
    {
    }
}

/// <summary>What one query has used so far.</summary>
internal sealed class QueryBudget
{
    private long _retainedValues;

    public QueryBudget(QueryLimits limits)
    {
        Limits = limits;
    }

    public QueryLimits Limits { get; }

    public bool Truncated { get; set; }

    public bool Approximate { get; set; }

    public void RetainValue()
    {
        if (++_retainedValues > Limits.MaxRetainedValues)
        {
            throw new QueryLimitExceededException(
                $"The query keeps more than {Limits.MaxRetainedValues:N0} distinct or percentile values. Narrow the time range or group by fewer fields.");
        }
    }

    public void CheckGroupCount(int count)
    {
        if (count > Limits.MaxGroups)
        {
            throw new QueryLimitExceededException(
                $"The query produces more than {Limits.MaxGroups:N0} groups. Narrow the time range or group by fewer fields.");
        }
    }
}

/// <summary>
/// A query's first stateful stage. Row stages feed it a row at a time, so what it holds in memory
/// depends on what it computes, not on how many events it reads.
/// </summary>
internal interface IRowSink
{
    /// <summary>True when more rows can't change the result, so reading can stop.</summary>
    bool IsDone { get; }

    /// <summary>Rows this sink would still take, or null if it takes any number.</summary>
    int? Remaining { get; }

    void Add(object row);

    IEnumerable<dynamic> Complete();
}

/// <summary>Keeps the first rows, for <c>head</c> and for pipelines ending in filters.</summary>
internal sealed class TakeSink : IRowSink
{
    private readonly int _count;
    private readonly List<dynamic> _rows = new();

    public TakeSink(int count)
    {
        _count = count;
    }

    public bool IsDone => _rows.Count >= _count;

    public int? Remaining => _count - _rows.Count;

    public void Add(object row) => _rows.Add(row);

    public IEnumerable<dynamic> Complete() => _rows;
}

/// <summary>Keeps rows for stages that need all their input, up to a limit.</summary>
internal sealed class MaterializeSink : IRowSink
{
    private readonly QueryBudget _budget;
    private readonly List<dynamic> _rows = new();

    public MaterializeSink(QueryBudget budget)
    {
        _budget = budget;
    }

    public bool IsDone => _rows.Count > _budget.Limits.MaxRowsInMemory;

    public int? Remaining => null;

    public void Add(object row) => _rows.Add(row);

    public IEnumerable<dynamic> Complete()
    {
        if (_rows.Count > _budget.Limits.MaxRowsInMemory)
        {
            _rows.RemoveRange(_budget.Limits.MaxRowsInMemory, _rows.Count - _budget.Limits.MaxRowsInMemory);
            _budget.Truncated = true;
        }

        return _rows;
    }
}

/// <summary>Keeps the last rows, for <c>tail</c>.</summary>
internal sealed class TailSink : IRowSink
{
    private readonly int _count;
    private readonly Queue<dynamic> _rows = new();

    public TailSink(int count)
    {
        _count = count;
    }

    public bool IsDone => _count == 0;

    public int? Remaining => null;

    public void Add(object row)
    {
        _rows.Enqueue(row);
        if (_rows.Count > _count)
        {
            _rows.Dequeue();
        }
    }

    public IEnumerable<dynamic> Complete() => _rows;
}

/// <summary>
/// Keeps the first rows in a sort order, for <c>sort</c> followed by <c>head</c>, without keeping
/// every row. Rows that compare equal stay in the order they arrived, as in a full sort.
/// </summary>
internal sealed class TopRowsSink : IRowSink
{
    private const int MinCompactionSize = 1024;

    private readonly LegacySortOperator _sort;
    private readonly int _count;
    private List<dynamic> _rows = new();

    public TopRowsSink(LegacySortOperator sort, int count)
    {
        _sort = sort;
        _count = count;
    }

    public bool IsDone => _count == 0;

    public int? Remaining => null;

    public void Add(object row)
    {
        _rows.Add(row);
        if (_rows.Count >= Math.Max(_count * 2, MinCompactionSize))
        {
            Compact();
        }
    }

    public IEnumerable<dynamic> Complete()
    {
        Compact();
        return _rows;
    }

    private void Compact()
    {
        _rows = _sort.Order(_rows).Take(_count).ToList();
    }
}

/// <summary>Computes <c>stats</c> and two-axis <c>chart</c> columns, one row at a time.</summary>
internal sealed class StatsSink : IRowSink
{
    private readonly List<AggregateFunction> _aggregations;
    private readonly List<string> _groupByFields;
    private readonly QueryBudget _budget;
    private readonly Dictionary<string, Group> _groups = new();
    private readonly List<Group> _ordered = new();

    public StatsSink(List<AggregateFunction> aggregations, List<string> groupByFields, QueryBudget budget)
    {
        _aggregations = aggregations;
        _groupByFields = groupByFields;
        _budget = budget;

        // With no grouping there's always one row, even with no input.
        if (groupByFields.Count == 0)
        {
            _ordered.Add(new Group(string.Empty, null, aggregations, budget));
        }
    }

    public bool IsDone => false;

    public int? Remaining => null;

    public void Add(object row)
    {
        var dict = (IDictionary<string, object?>)row;
        Group group;
        if (_groupByFields.Count == 0)
        {
            group = _ordered[0];
        }
        else
        {
            // As in SPL, an event without every by-field isn't counted
            if (!BqlGroupKey.HasAll(dict, _groupByFields))
            {
                return;
            }

            var key = BqlGroupKey.For(dict, _groupByFields);
            if (!_groups.TryGetValue(key, out group!))
            {
                var values = new object?[_groupByFields.Count];
                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = dict.TryGetValue(_groupByFields[i], out var value) ? value : null;
                }

                group = new Group(key, values, _aggregations, _budget);
                _groups[key] = group;
                _ordered.Add(group);
                _budget.CheckGroupCount(_ordered.Count);
            }
        }

        foreach (var accumulator in group.Accumulators)
        {
            accumulator.Add(dict);
        }
    }

    public IEnumerable<dynamic> Complete()
    {
        var results = new List<dynamic>(_ordered.Count);
        foreach (var group in _ordered)
        {
            var row = new BqlRow();
            for (var i = 0; i < _groupByFields.Count; i++)
            {
                row[_groupByFields[i]] = group.Values![i];
            }

            for (var i = 0; i < _aggregations.Count; i++)
            {
                row[_aggregations[i].OutputName] = group.Accumulators[i].Result();
            }

            results.Add(row);
        }

        return results;
    }

    private sealed class Group
    {
        public Group(string key, object?[]? values, List<AggregateFunction> aggregations, QueryBudget budget)
        {
            Key = key;
            Values = values;
            Accumulators = aggregations.Select(aggregate => new AggregateAccumulator(aggregate, budget)).ToArray();
        }

        public string Key { get; }

        public object?[]? Values { get; }

        public AggregateAccumulator[] Accumulators { get; }
    }
}

/// <summary>Computes <c>timechart</c> one row at a time.</summary>
internal sealed class TimechartSink : IRowSink
{
    private readonly TimeSpan _span;
    private readonly AggregateFunction _function;
    private readonly string? _byField;
    private readonly QueryBudget _budget;
    private readonly Dictionary<long, Dictionary<string, AggregateAccumulator>> _buckets = new();
    private int _groupCount;

    public TimechartSink(TimeSpan span, AggregateFunction function, string? byField, QueryBudget budget)
    {
        _span = span;
        _function = function;
        _byField = byField;
        _budget = budget;
    }

    public bool IsDone => false;

    public int? Remaining => null;

    /// <summary>
    /// The row's time: <c>_time</c>, which eval or bin may have changed, else the event's own.
    /// </summary>
    private static DateTime? TimeOf(IDictionary<string, object?> row)
    {
        if (row.TryGetValue("_time", out var time) && time != null)
        {
            var epoch = time switch
            {
                double d => d,
                long l => l,
                int i => i,
                string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => (double?)null,
            };
            if (BqlTime.FromValue(time, epoch) is { } fromTime && fromTime != DateTime.MinValue)
            {
                return fromTime;
            }
        }

        return row.TryGetValue("Timestamp", out var value) && value is DateTime timestamp && timestamp != DateTime.MinValue ? timestamp : null;
    }

    public void Add(object row)
    {
        var dict = (IDictionary<string, object?>)row;
        if (TimeOf(dict) is not { } timestamp)
        {
            return;
        }

        var bucketTicks = timestamp.Ticks / _span.Ticks * _span.Ticks;
        if (!_buckets.TryGetValue(bucketTicks, out var bucket))
        {
            bucket = new Dictionary<string, AggregateAccumulator>();
            _buckets[bucketTicks] = bucket;
        }

        var key = _byField == null
            ? string.Empty
            : dict.TryGetValue(_byField, out var byValue) ? byValue?.ToString() ?? string.Empty : string.Empty;
        if (!bucket.TryGetValue(key, out var accumulator))
        {
            accumulator = new AggregateAccumulator(_function, _budget);
            bucket[key] = accumulator;
            _budget.CheckGroupCount(++_groupCount);
        }

        accumulator.Add(dict);
    }

    public IEnumerable<dynamic> Complete()
    {
        var results = new List<dynamic>(_groupCount);
        foreach (var (ticks, bucket) in _buckets.OrderBy(pair => pair.Key))
        {
            var time = new DateTime(ticks, DateTimeKind.Utc);
            foreach (var (key, accumulator) in bucket)
            {
                var row = new BqlRow { ["_time"] = time };
                if (_byField != null)
                {
                    row[_byField] = key;
                }

                row[_function.OutputName] = accumulator.Result();
                results.Add(row);
            }
        }

        return results;
    }
}

/// <summary>Computes <c>top</c> and <c>rare</c>, one row at a time.</summary>
internal sealed class ValueCountSink : IRowSink
{
    private readonly string _field;
    private readonly string? _byField;
    private readonly QueryBudget _budget;
    private readonly Func<IEnumerable<KeyValuePair<string, long>>, string?, IEnumerable<dynamic>> _complete;
    private readonly Dictionary<string, Dictionary<string, long>> _groups = new();
    private int _valueCount;

    public ValueCountSink(
        string field,
        string? byField,
        QueryBudget budget,
        Func<IEnumerable<KeyValuePair<string, long>>, string?, IEnumerable<dynamic>> complete)
    {
        _field = field;
        _byField = byField;
        _budget = budget;
        _complete = complete;
    }

    public bool IsDone => false;

    public int? Remaining => null;

    public void Add(object row)
    {
        var dict = (IDictionary<string, object?>)row;
        if (!dict.TryGetValue(_field, out var value) || value == null)
        {
            return;
        }

        var groupKey = _byField == null
            ? string.Empty
            : dict.TryGetValue(_byField, out var byValue) ? byValue?.ToString() ?? string.Empty : string.Empty;
        if (!_groups.TryGetValue(groupKey, out var counts))
        {
            counts = new Dictionary<string, long>();
            _groups[groupKey] = counts;
        }

        var key = value.ToString() ?? string.Empty;
        if (counts.TryGetValue(key, out var count))
        {
            counts[key] = count + 1;
        }
        else
        {
            counts[key] = 1;
            _budget.CheckGroupCount(++_valueCount);
        }
    }

    public IEnumerable<dynamic> Complete()
    {
        var results = new List<dynamic>();
        foreach (var (group, counts) in _groups)
        {
            results.AddRange(_complete(counts, _byField == null ? null : group));
        }

        return results;
    }
}

/// <summary>Computes a two-axis <c>chart</c>, one row at a time.</summary>
internal sealed class ChartSink : IRowSink
{
    private readonly List<AggregateFunction> _aggregations;
    private readonly string _overField;
    private readonly string _byField;
    private readonly QueryBudget _budget;
    private readonly SortedSet<string> _columns = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedDictionary<string, Dictionary<string, AggregateAccumulator[]>> _rows = new(StringComparer.OrdinalIgnoreCase);
    private int _cellCount;

    public ChartSink(List<AggregateFunction> aggregations, string overField, string byField, QueryBudget budget)
    {
        _aggregations = aggregations;
        _overField = overField;
        _byField = byField;
        _budget = budget;
    }

    public bool IsDone => false;

    public int? Remaining => null;

    public void Add(object row)
    {
        var dict = (IDictionary<string, object?>)row;
        var over = Value(dict, _overField);
        var column = Value(dict, _byField);
        _columns.Add(column);

        if (!_rows.TryGetValue(over, out var cells))
        {
            cells = new Dictionary<string, AggregateAccumulator[]>(StringComparer.OrdinalIgnoreCase);
            _rows[over] = cells;
        }

        if (!cells.TryGetValue(column, out var accumulators))
        {
            accumulators = _aggregations.Select(aggregate => new AggregateAccumulator(aggregate, _budget)).ToArray();
            cells[column] = accumulators;
            _budget.CheckGroupCount(++_cellCount);
        }

        foreach (var accumulator in accumulators)
        {
            accumulator.Add(dict);
        }
    }

    public IEnumerable<dynamic> Complete()
    {
        var results = new List<dynamic>(_rows.Count);
        foreach (var (over, cells) in _rows)
        {
            var row = new BqlRow { [_overField] = over };
            foreach (var column in _columns)
            {
                cells.TryGetValue(column, out var accumulators);
                for (var i = 0; i < _aggregations.Count; i++)
                {
                    var aggregate = _aggregations[i];
                    var name = _aggregations.Count == 1 ? column : $"{aggregate.OutputName}: {column}";
                    row[name] = accumulators != null ? accumulators[i].Result() : aggregate.Name == "count" ? 0 : null;
                }
            }

            results.Add(row);
        }

        return results;
    }

    private static string Value(IDictionary<string, object?> row, string field) =>
        row.TryGetValue(field, out var value) ? value?.ToString() ?? "NULL" : "NULL";
}

/// <summary>
/// One aggregate function over a stream of rows. Everything that computes an aggregate goes
/// through this, so <c>stats</c>, <c>timechart</c>, <c>chart</c>, <c>eventstats</c> and
/// <c>streamstats</c> all give the same answer.
/// </summary>
internal sealed class AggregateAccumulator
{
    private readonly AggregateFunction _function;
    private readonly QueryBudget _budget;
    private readonly Kind _kind;
    private readonly double _percentile;

    private long _count;
    private double _sum;
    private double _mean;
    private double _squaredDeviations;
    private double _min = double.PositiveInfinity;
    private double _max = double.NegativeInfinity;
    private string? _minText;
    private string? _maxText;
    private DateTime? _minTime;
    private DateTime? _maxTime;
    private HashSet<string>? _distinct;
    private List<double>? _values;
    private Random? _sampler;
    private SortedSet<string>? _valueSet;
    private List<object?>? _list;
    private object? _first;
    private bool _hasFirst;
    private object? _last;
    private DateTime? _earliestTime;
    private object? _earliestValue;
    private DateTime? _latestTime;
    private object? _latestValue;
    private Dictionary<string, (long Count, object? Value, long Order)>? _modes;

    /// <summary>Most values list() keeps, as in SPL.</summary>
    public const int MaxListValues = 100;

    public AggregateAccumulator(AggregateFunction function, QueryBudget budget)
    {
        _function = function;
        _budget = budget;
        _kind = KindOf(function.Name, out _percentile);
    }

    private enum Kind
    {
        Count,
        DistinctCount,
        Sum,
        Average,
        Min,
        Max,
        Median,
        Percentile,
        SampleDeviation,
        PopulationDeviation,
        SampleVariance,
        PopulationVariance,
        Values,
        List,
        First,
        Last,
        Earliest,
        Latest,
        Range,
        Mode,
    }

    public void Add(IDictionary<string, object?> row)
    {
        if (_function.Expression is { } expression)
        {
            // count(eval(...)) counts rows where it's true; the others aggregate its value
            if (_kind == Kind.Count)
            {
                if (BqlExpressionEvaluator.EvaluateBool(expression, row))
                {
                    _count++;
                }

                return;
            }

            row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [_function.Field!] = BqlExpressionEvaluator.Evaluate(expression, row),
                ["_time"] = RowTime(row),
            };
        }

        switch (_kind)
        {
            case Kind.Values or Kind.List or Kind.First or Kind.Last or Kind.Earliest or Kind.Latest or Kind.Mode:
                AddValue(row);
                return;

            case Kind.Count:
                if (_function.Field == null || (row.TryGetValue(_function.Field, out var counted) && counted != null))
                {
                    _count++;
                }

                return;

            case Kind.DistinctCount:
                if (row.TryGetValue(_function.Field ?? string.Empty, out var distinct) && distinct?.ToString() is { } text)
                {
                    _distinct ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (_distinct.Add(text))
                    {
                        _budget.RetainValue();
                    }
                }

                return;
        }

        var number = LegacyStatsOperator.GetNumericValue(row, _function.Field ?? string.Empty);
        if (number is not { } value)
        {
            if (_kind is Kind.Min or Kind.Max && row.TryGetValue(_function.Field ?? string.Empty, out var raw))
            {
                // Times compare as times; as text, "9:59 PM" would come after "10:16 PM"
                if (AsTime(raw) is { } time)
                {
                    AddTime(time);
                }
                else if (raw?.ToString() is { } rawText)
                {
                    AddText(rawText);
                }
            }

            return;
        }

        _count++;
        _sum += value;
        _min = Math.Min(_min, value);
        _max = Math.Max(_max, value);

        // Welford's method: variance without keeping the values
        var delta = value - _mean;
        _mean += delta / _count;
        _squaredDeviations += delta * (value - _mean);

        if (_kind is Kind.Median or Kind.Percentile)
        {
            AddToSample(value);
        }
    }

    public object? Result()
    {
        switch (_kind)
        {
            case Kind.Count:
                return CountValue(_count);
            case Kind.DistinctCount:
                return _distinct?.Count ?? 0;
            case Kind.Values:
                return _valueSet is { Count: > 0 } set ? set.Cast<object?>().ToList() : null;
            case Kind.List:
                return _list is { Count: > 0 } list ? list.ToList() : null;
            case Kind.First:
                return _first;
            case Kind.Last:
                return _last;
            case Kind.Earliest:
                return _earliestValue;
            case Kind.Latest:
                return _latestValue;
            case Kind.Mode:
                return _modes is { Count: > 0 } modes
                    ? modes.Values.OrderByDescending(m => m.Count).ThenBy(m => m.Order).First().Value
                    : null;
        }

        if (_count == 0)
        {
            return _kind switch
            {
                Kind.Min => _minTime is { } earliest ? earliest : _minText,
                Kind.Max => _maxTime is { } latest ? latest : _maxText,
                _ => null,
            };
        }

        object? result = _kind switch
        {
            Kind.Sum => _sum,
            Kind.Average => _sum / _count,
            Kind.Min => _min,
            Kind.Max => _max,
            Kind.Range => _max - _min,
            Kind.Median => Percentile(50, medianStyle: true),
            Kind.Percentile => Percentile(_percentile, medianStyle: false),
            Kind.SampleDeviation => _count == 1 ? double.NaN : Math.Sqrt(_squaredDeviations / (_count - 1)),
            Kind.PopulationDeviation => Math.Sqrt(_squaredDeviations / _count),
            Kind.SampleVariance => _count == 1 ? double.NaN : _squaredDeviations / (_count - 1),
            Kind.PopulationVariance => _squaredDeviations / _count,
            _ => null,
        };

        // The sample deviation of a single value is undefined, which isn't a JSON number
        return result is double number && !double.IsFinite(number) ? null : result;
    }

    /// <summary>A count, as an int while it fits, which is what stages and clients expect.</summary>
    public static object CountValue(long count) => count <= int.MaxValue ? (int)count : count;

    private static Kind KindOf(string name, out double percentile)
    {
        percentile = 95;
        var lower = name.ToLowerInvariant();

        if (lower == "percentile")
        {
            return Kind.Percentile;
        }

        if (lower.StartsWith("perc", StringComparison.Ordinal)
            && double.TryParse(lower[4..], NumberStyles.Any, CultureInfo.InvariantCulture, out var perc))
        {
            percentile = Math.Clamp(perc, 0, 100);
            return Kind.Percentile;
        }

        if (lower.Length > 1 && lower[0] == 'p' && char.IsDigit(lower[1])
            && double.TryParse(lower[1..], NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
        {
            percentile = Math.Clamp(p, 0, 100);
            return Kind.Percentile;
        }

        return name switch
        {
            "count" => Kind.Count,
            "dc" or "distinct_count" => Kind.DistinctCount,
            "sum" => Kind.Sum,
            "avg" or "mean" => Kind.Average,
            "min" => Kind.Min,
            "max" => Kind.Max,
            "median" => Kind.Median,
            "stdev" => Kind.SampleDeviation,
            "stdevp" => Kind.PopulationDeviation,
            "variance" => Kind.SampleVariance,
            "varp" => Kind.PopulationVariance,
            "values" => Kind.Values,
            "list" => Kind.List,
            "first" => Kind.First,
            "last" => Kind.Last,
            "earliest" => Kind.Earliest,
            "latest" => Kind.Latest,
            "range" => Kind.Range,
            "mode" => Kind.Mode,
            _ => throw new QueryParseException($"Unknown aggregate function: {name}"),
        };
    }

    private static DateTime? RowTime(IDictionary<string, object?> row) =>
        (row.TryGetValue("_time", out var time) ? AsTime(time) : null)
        ?? (row.TryGetValue("Timestamp", out var timestamp) ? AsTime(timestamp) : null);

    /// <summary>For the functions that keep values rather than a number.</summary>
    private void AddValue(IDictionary<string, object?> row)
    {
        if (!row.TryGetValue(_function.Field ?? string.Empty, out var value) || value == null)
        {
            return;
        }

        switch (_kind)
        {
            case Kind.Values:
                _valueSet ??= new SortedSet<string>(StringComparer.Ordinal);
                if (_valueSet.Add(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty))
                {
                    _budget.RetainValue();
                }

                break;

            case Kind.List:
                _list ??= new List<object?>();
                if (_list.Count < MaxListValues)
                {
                    _list.Add(value);
                    _budget.RetainValue();
                }

                break;

            case Kind.First:
                if (!_hasFirst)
                {
                    _first = value;
                    _hasFirst = true;
                }

                break;

            case Kind.Last:
                _last = value;
                break;

            case Kind.Earliest or Kind.Latest:
                if (RowTime(row) is { } time)
                {
                    if (_kind == Kind.Earliest && (_earliestTime is not { } earliest || time < earliest))
                    {
                        (_earliestTime, _earliestValue) = (time, value);
                    }
                    else if (_kind == Kind.Latest && (_latestTime is not { } latest || time > latest))
                    {
                        (_latestTime, _latestValue) = (time, value);
                    }
                }

                break;

            case Kind.Mode:
                _modes ??= new Dictionary<string, (long, object?, long)>(StringComparer.Ordinal);
                var key = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                if (_modes.TryGetValue(key, out var seen))
                {
                    _modes[key] = (seen.Count + 1, seen.Value, seen.Order);
                }
                else
                {
                    _modes[key] = (1, value, _modes.Count);
                    _budget.RetainValue();
                }

                break;
        }
    }

    private static DateTime? AsTime(object? value) => value switch
    {
        DateTime time => time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : time,
        DateTimeOffset offset => offset.UtcDateTime,
        _ => null,
    };

    private void AddTime(DateTime time)
    {
        if (_minTime is not { } min || time < min)
        {
            _minTime = time;
        }

        if (_maxTime is not { } max || time > max)
        {
            _maxTime = time;
        }
    }

    private void AddText(string text)
    {
        if (_minText == null || StringComparer.OrdinalIgnoreCase.Compare(text, _minText) < 0)
        {
            _minText = text;
        }

        if (_maxText == null || StringComparer.OrdinalIgnoreCase.Compare(text, _maxText) >= 0)
        {
            _maxText = text;
        }
    }

    /// <summary>
    /// Keeps every value up to a limit, then a uniform sample (reservoir sampling), which estimates
    /// a percentile well without keeping everything.
    /// </summary>
    private void AddToSample(double value)
    {
        _values ??= new List<double>();
        var capacity = _budget.Limits.MaxExactPercentileValues;
        if (_values.Count < capacity)
        {
            _values.Add(value);
            _budget.RetainValue();
            return;
        }

        _budget.Approximate = true;

        // A fixed seed gives the same answer for the same input.
        _sampler ??= new Random(capacity);
        var slot = _sampler.NextInt64(_count);
        if (slot < capacity)
        {
            _values[(int)slot] = value;
        }
    }

    private double Percentile(double percentile, bool medianStyle)
    {
        var sorted = _values!;
        sorted.Sort();

        if (medianStyle)
        {
            var mid = sorted.Count / 2;
            return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
        }

        if (sorted.Count == 1)
        {
            return sorted[0];
        }

        var index = percentile / 100.0 * (sorted.Count - 1);
        var lower = Math.Clamp((int)Math.Floor(index), 0, sorted.Count - 1);
        var upper = Math.Clamp((int)Math.Ceiling(index), 0, sorted.Count - 1);
        return lower == upper ? sorted[lower] : sorted[lower] + ((index - lower) * (sorted[upper] - sorted[lower]));
    }
}

/// <summary>
/// Orders field values, which can be of mixed types once a field holds numbers in some events and
/// text in others. Numbers compare as numbers. Nulls sort first, then numbers, then everything else.
/// </summary>
internal sealed class BqlValueComparer : IComparer<object?>
{
    public static readonly BqlValueComparer Instance = new();

    public int Compare(object? x, object? y)
    {
        if (x == null || y == null)
        {
            return x == null ? y == null ? 0 : -1 : 1;
        }

        var xNumber = AsNumber(x);
        var yNumber = AsNumber(y);
        if (xNumber.HasValue && yNumber.HasValue)
        {
            return xNumber.Value.CompareTo(yNumber.Value);
        }

        if (xNumber.HasValue != yNumber.HasValue)
        {
            return xNumber.HasValue ? -1 : 1;
        }

        if (x is DateTime xTime && y is DateTime yTime)
        {
            return xTime.CompareTo(yTime);
        }

        return string.Compare(x.ToString(), y.ToString(), StringComparison.Ordinal);
    }

    private static double? AsNumber(object value) => value switch
    {
        int i => i,
        long l => l,
        double d => d,
        float f => f,
        decimal m => (double)m,
        short s => s,
        _ => null,
    };
}

/// <summary>Settings for regular expressions in searches.</summary>
public static class QueryRegex
{
    /// <summary>
    /// How long a search's pattern (<c>rex</c>, <c>regex</c>) can spend on one event. Set once at
    /// startup from <c>Limits:QueryRegexTimeoutMs</c>.
    /// </summary>
    public static TimeSpan PerEventTimeout { get; set; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// Stops a slow regular expression from using a query's whole time limit: each event gets a short
/// allowance, and a pattern that keeps running out fails the query.
/// </summary>
internal sealed class RegexTimeoutTracker
{
    /// <summary>How long a pattern can spend on one event.</summary>
    public static TimeSpan PerEvent => QueryRegex.PerEventTimeout;

    private const int MaxTimeouts = 8;

    private readonly string _pattern;
    private int _timeouts;

    public RegexTimeoutTracker(string pattern)
    {
        _pattern = pattern;
    }

    public void Record()
    {
        if (Interlocked.Increment(ref _timeouts) >= MaxTimeouts)
        {
            throw new QueryLimitExceededException(
                $"The regular expression '{_pattern}' is too slow: it ran out of time on {MaxTimeouts} events.");
        }
    }
}
