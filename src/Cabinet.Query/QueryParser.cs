using System.Dynamic;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Cabinet.Query;

public class QueryParser : IQueryParser
{
    private readonly ILogger<QueryParser> _logger;

    public QueryParser(ILogger<QueryParser> logger)
    {
        _logger = logger;
    }

    public QueryPlan Parse(string query)
    {
        var plan = new QueryPlan();

        if (string.IsNullOrWhiteSpace(query))
        {
            throw new QueryParseException("Query cannot be empty");
        }

        // Split by pipe operator
        var segments = SplitByPipe(query);

        // First segment is always search if no explicit operator
        var firstSegment = segments[0].Trim();
        if (!HasOperatorPrefix(firstSegment))
        {
            plan.Operators.Add(new SearchOperator(firstSegment));
        }
        else
        {
            plan.Operators.Add(ParseOperator(firstSegment));
        }

        // Parse remaining operators
        for (int i = 1; i < segments.Length; i++)
        {
            var segment = segments[i].Trim();
            if (!string.IsNullOrEmpty(segment))
            {
                plan.Operators.Add(ParseOperator(segment));
            }
        }

        _logger.LogDebug("Parsed query into {Count} operators", plan.Operators.Count);
        return plan;
    }

    private string[] SplitByPipe(string query)
    {
        // Handle pipes within quotes
        var segments = new List<string>();
        var current = "";
        var inQuotes = false;

        for (int i = 0; i < query.Length; i++)
        {
            var c = query[i];

            if (c == '"' && (i == 0 || query[i - 1] != '\\'))
            {
                inQuotes = !inQuotes;
                current += c;
            }
            else if (c == '|' && !inQuotes)
            {
                segments.Add(current);
                current = "";
            }
            else
            {
                current += c;
            }
        }

        if (!string.IsNullOrEmpty(current))
        {
            segments.Add(current);
        }

        return segments.ToArray();
    }

    private bool HasOperatorPrefix(string segment)
    {
        var operators = new[] { "where", "stats", "fields", "sort", "head", "tail", "top", "timechart", "dedup" };
        var lower = segment.ToLowerInvariant();
        return operators.Any(op => lower.StartsWith(op + " ") || lower == op);
    }

    private QueryOperator ParseOperator(string segment)
    {
        var parts = segment.Split(' ', 2);
        var operatorName = parts[0].ToLowerInvariant();
        var args = parts.Length > 1 ? parts[1] : "";

        return operatorName switch
        {
            "search" => new SearchOperator(args),
            "where" => new WhereOperator(args),
            "stats" => new StatsOperator(args),
            "fields" => new FieldsOperator(args),
            "sort" => new SortOperator(args),
            "head" => new HeadOperator(ParseInt(args, 10)),
            "tail" => new TailOperator(ParseInt(args, 10)),
            "top" => new TopOperator(args),
            "timechart" => new TimechartOperator(args),
            "dedup" => new DedupOperator(args),
            _ => throw new QueryParseException($"Unknown operator: {operatorName}")
        };
    }

    private int ParseInt(string value, int defaultValue)
    {
        if (string.IsNullOrEmpty(value)) return defaultValue;
        return int.TryParse(value.Trim(), out var result) ? result : defaultValue;
    }
}

// Operator implementations

public class SearchOperator : QueryOperator
{
    private readonly string _pattern;

    public SearchOperator(string pattern)
    {
        _pattern = pattern?.Trim('"') ?? "*";
    }

    public override string Name => "search";

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken cancellationToken = default)
    {
        if (_pattern == "*") return Task.FromResult(input);

        var filtered = input.Where(evt =>
        {
            if (evt is IDictionary<string, object?> dict)
            {
                var rawLine = dict.ContainsKey("RawLine") ? dict["RawLine"]?.ToString() ?? "" : "";
                return rawLine.Contains(_pattern, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        });

        return Task.FromResult(filtered);
    }
}

public class WhereOperator : QueryOperator
{
    private readonly string _condition;

    public WhereOperator(string condition)
    {
        _condition = condition;
    }

    public override string Name => "where";

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken cancellationToken = default)
    {
        var filtered = input.Where(evt => EvaluateCondition(evt, _condition));
        return Task.FromResult(filtered);
    }

    private bool EvaluateCondition(dynamic evt, string condition)
    {
        if (evt is not IDictionary<string, object?> dict) return false;

        // Parse simple conditions: field=value, field!=value, field>value, etc.
        var match = Regex.Match(condition, @"(\w+)\s*([=!<>]+)\s*(.+)", RegexOptions.None, TimeSpan.FromSeconds(1));
        if (!match.Success) return false;

        var field = match.Groups[1].Value;
        var op = match.Groups[2].Value;
        var value = match.Groups[3].Value.Trim('"', '\'');

        if (!dict.ContainsKey(field)) return false;

        var fieldValue = dict[field]?.ToString() ?? "";

        return op switch
        {
            "=" => fieldValue.Equals(value, StringComparison.OrdinalIgnoreCase),
            "!=" => !fieldValue.Equals(value, StringComparison.OrdinalIgnoreCase),
            ">" => CompareNumeric(fieldValue, value) > 0,
            ">=" => CompareNumeric(fieldValue, value) >= 0,
            "<" => CompareNumeric(fieldValue, value) < 0,
            "<=" => CompareNumeric(fieldValue, value) <= 0,
            _ => false
        };
    }

    private int CompareNumeric(string a, string b)
    {
        if (double.TryParse(a, out var aNum) && double.TryParse(b, out var bNum))
        {
            return aNum.CompareTo(bNum);
        }
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }
}

public class StatsOperator : QueryOperator
{
    private readonly string _expression;

    public StatsOperator(string expression)
    {
        _expression = expression;
    }

    public override string Name => "stats";

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken cancellationToken = default)
    {
        var results = new List<dynamic>();

        // Parse stats expression: "count by field" or "count" or "sum(field) by group"
        if (_expression.Contains(" by "))
        {
            var parts = _expression.Split(" by ");
            var aggregation = parts[0].Trim();
            var groupBy = parts[1].Trim();

            var groups = input.GroupBy(evt =>
            {
                if (evt is IDictionary<string, object?> dict && dict.ContainsKey(groupBy))
                {
                    return dict[groupBy]?.ToString() ?? "null";
                }
                return "null";
            });

            foreach (var group in groups)
            {
                dynamic result = new ExpandoObject();
                var dict = (IDictionary<string, object?>)result;
                dict[groupBy] = group.Key;

                if (aggregation == "count")
                {
                    dict["count"] = group.Count();
                }
                else if (aggregation.StartsWith("sum("))
                {
                    var field = ExtractFieldFromFunction(aggregation);
                    dict["sum"] = CalculateSum(group, field);
                }
                else if (aggregation.StartsWith("avg("))
                {
                    var field = ExtractFieldFromFunction(aggregation);
                    dict["avg"] = CalculateAverage(group, field);
                }
                else if (aggregation.StartsWith("max("))
                {
                    var field = ExtractFieldFromFunction(aggregation);
                    dict["max"] = CalculateMax(group, field);
                }
                else if (aggregation.StartsWith("min("))
                {
                    var field = ExtractFieldFromFunction(aggregation);
                    dict["min"] = CalculateMin(group, field);
                }

                results.Add(result);
            }
        }
        else if (_expression == "count")
        {
            dynamic result = new ExpandoObject();
            var dict = (IDictionary<string, object?>)result;
            dict["count"] = input.Count();
            results.Add(result);
        }

        return Task.FromResult(results.AsEnumerable());
    }

    private string ExtractFieldFromFunction(string function)
    {
        var match = Regex.Match(function, @"\(([^)]+)\)", RegexOptions.None, TimeSpan.FromSeconds(1));
        return match.Success ? match.Groups[1].Value : "";
    }

    private double CalculateSum(IEnumerable<dynamic> group, string field)
    {
        return group.Sum(evt =>
        {
            if (evt is IDictionary<string, object?> dict && dict.ContainsKey(field))
            {
                if (double.TryParse(dict[field]?.ToString(), out var value))
                    return value;
            }
            return 0;
        });
    }

    private double CalculateAverage(IEnumerable<dynamic> group, string field)
    {
        var values = group.Select(evt =>
        {
            if (evt is IDictionary<string, object?> dict && dict.ContainsKey(field))
            {
                if (double.TryParse(dict[field]?.ToString(), out var value))
                    return value;
            }
            return 0;
        }).ToList();

        return values.Any() ? values.Average() : 0;
    }

    private double CalculateMax(IEnumerable<dynamic> group, string field)
    {
        return group.Max(evt =>
        {
            if (evt is IDictionary<string, object?> dict && dict.ContainsKey(field))
            {
                if (double.TryParse(dict[field]?.ToString(), out var value))
                    return value;
            }
            return double.MinValue;
        });
    }

    private double CalculateMin(IEnumerable<dynamic> group, string field)
    {
        return group.Min(evt =>
        {
            if (evt is IDictionary<string, object?> dict && dict.ContainsKey(field))
            {
                if (double.TryParse(dict[field]?.ToString(), out var value))
                    return value;
            }
            return double.MaxValue;
        });
    }
}

public class FieldsOperator : QueryOperator
{
    private readonly List<string> _fields;

    public FieldsOperator(string fieldsExpression)
    {
        _fields = fieldsExpression.Split(',').Select(f => f.Trim()).ToList();
    }

    public override string Name => "fields";

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken cancellationToken = default)
    {
        var results = input.Select(evt =>
        {
            if (evt is IDictionary<string, object?> dict)
            {
                dynamic result = new ExpandoObject();
                var newDict = (IDictionary<string, object?>)result;

                foreach (var field in _fields)
                {
                    if (dict.ContainsKey(field))
                    {
                        newDict[field] = dict[field];
                    }
                }

                return result;
            }
            return evt;
        });

        return Task.FromResult(results);
    }
}

public class SortOperator : QueryOperator
{
    private readonly string _field;
    private readonly bool _descending;

    public SortOperator(string expression)
    {
        if (expression.StartsWith("-"))
        {
            _descending = true;
            _field = expression.Substring(1).Trim();
        }
        else
        {
            _descending = false;
            _field = expression.Trim();
        }
    }

    public override string Name => "sort";

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken cancellationToken = default)
    {
        var sorted = _descending
            ? input.OrderByDescending(evt => GetFieldValue(evt, _field))
            : input.OrderBy(evt => GetFieldValue(evt, _field));

        return Task.FromResult(sorted.AsEnumerable());
    }

    private object? GetFieldValue(dynamic evt, string field)
    {
        if (evt is IDictionary<string, object?> dict && dict.ContainsKey(field))
        {
            return dict[field];
        }
        return null;
    }
}

public class HeadOperator : QueryOperator
{
    private readonly int _count;

    public HeadOperator(int count)
    {
        _count = count;
    }

    public override string Name => "head";

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(input.Take(_count));
    }
}

public class TailOperator : QueryOperator
{
    private readonly int _count;

    public TailOperator(int count)
    {
        _count = count;
    }

    public override string Name => "tail";

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken cancellationToken = default)
    {
        var materialized = input.ToList();
        return Task.FromResult(materialized.Skip(Math.Max(0, materialized.Count - _count)));
    }
}

public class TopOperator : QueryOperator
{
    private readonly int _count;
    private readonly string _field;

    public TopOperator(string expression)
    {
        var parts = expression.Split(' ');
        _count = int.TryParse(parts[0], out var c) ? c : 10;
        _field = parts.Length > 1 ? parts[1] : "count";
    }

    public override string Name => "top";

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken cancellationToken = default)
    {
        var grouped = input.GroupBy(evt =>
        {
            if (evt is IDictionary<string, object?> dict && dict.ContainsKey(_field))
            {
                return dict[_field]?.ToString() ?? "null";
            }
            return "null";
        })
        .Select(g =>
        {
            dynamic result = new ExpandoObject();
            var dict = (IDictionary<string, object?>)result;
            dict[_field] = g.Key;
            dict["count"] = g.Count();
            return result;
        })
        .OrderByDescending(r => ((IDictionary<string, object?>)r)["count"])
        .Take(_count);

        return Task.FromResult(grouped.AsEnumerable());
    }
}

public class TimechartOperator : QueryOperator
{
    private readonly string _expression;

    public TimechartOperator(string expression)
    {
        _expression = expression;
    }

    public override string Name => "timechart";

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken cancellationToken = default)
    {
        // Parse span (e.g., "span=1h count by severity")
        var spanMatch = Regex.Match(_expression, @"span=(\d+)([mhd])", RegexOptions.None, TimeSpan.FromSeconds(1));
        var span = TimeSpan.FromHours(1); // default

        if (spanMatch.Success)
        {
            var value = int.Parse(spanMatch.Groups[1].Value);
            var unit = spanMatch.Groups[2].Value;
            span = unit switch
            {
                "m" => TimeSpan.FromMinutes(value),
                "h" => TimeSpan.FromHours(value),
                "d" => TimeSpan.FromDays(value),
                _ => TimeSpan.FromHours(1)
            };
        }

        var results = new List<dynamic>();
        var buckets = new Dictionary<DateTime, Dictionary<string, int>>();

        foreach (var evt in input)
        {
            if (evt is IDictionary<string, object?> dict && dict.ContainsKey("Timestamp"))
            {
                if (dict["Timestamp"] is DateTime timestamp)
                {
                    var bucket = RoundToSpan(timestamp, span);

                    if (!buckets.ContainsKey(bucket))
                        buckets[bucket] = new Dictionary<string, int>();

                    // Simple count for now
                    if (!buckets[bucket].ContainsKey("count"))
                        buckets[bucket]["count"] = 0;
                    buckets[bucket]["count"]++;
                }
            }
        }

        foreach (var bucket in buckets.OrderBy(b => b.Key))
        {
            dynamic result = new ExpandoObject();
            var dict = (IDictionary<string, object?>)result;
            dict["_time"] = bucket.Key;
            foreach (var kv in bucket.Value)
            {
                dict[kv.Key] = kv.Value;
            }
            results.Add(result);
        }

        return Task.FromResult(results.AsEnumerable());
    }

    private DateTime RoundToSpan(DateTime timestamp, TimeSpan span)
    {
        var ticks = timestamp.Ticks / span.Ticks * span.Ticks;
        return new DateTime(ticks, timestamp.Kind);
    }
}

public class DedupOperator : QueryOperator
{
    private readonly string _field;

    public DedupOperator(string field)
    {
        _field = field;
    }

    public override string Name => "dedup";

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken cancellationToken = default)
    {
        var seen = new HashSet<string>();
        var results = new List<dynamic>();

        foreach (var evt in input)
        {
            if (evt is IDictionary<string, object?> dict && dict.ContainsKey(_field))
            {
                var value = dict[_field]?.ToString() ?? "null";
                if (seen.Add(value))
                {
                    results.Add(evt);
                }
            }
        }

        return Task.FromResult(results.AsEnumerable());
    }
}