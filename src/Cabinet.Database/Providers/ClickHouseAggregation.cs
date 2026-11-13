using System.Globalization;
using System.Text.Json;
using Cabinet.Core.Interfaces;

namespace Cabinet.Database.Providers;

/// <summary>
/// Counts in ClickHouse: events, by built-in fields, and in time buckets, with the pipeline's
/// answer exactly. Groups take their values from their newest event and come out newest first,
/// buckets oldest first, and a missing host counts as empty, as the query engine has it. Other
/// aggregates, and custom fields, are left to the pipeline.
/// </summary>
public sealed partial class ClickHouseLogStorage : IAggregatingLogStorage
{
    /// <inheritdoc />
    public async Task<AggregateResult?> AggregateAsync(AggregateRequest request, CancellationToken cancellationToken = default)
    {
        var columns = request.GroupBy.Select(AggregateFields.Builtin).ToList();
        if (columns.Any(c => c == null)
            || request.Aggregates.Any(a => a.Kind != AggregateKind.Count || a.Field != null)
            || request.Bucket is { Ticks: <= 0 }
            || request.Filter.After != null)
        {
            return null;
        }

        await InitializeAsync(cancellationToken);
        var p = new List<Param>();
        var where = Where(request.Filter, p);
        var select = new List<string>();
        var groupBy = new List<string>();
        if (request.Bucket is { } span)
        {
            select.Add("intDiv(ts, {bucket:Int64}) * {bucket:Int64} AS bucket");
            groupBy.Add("bucket");
            p.Add(new Param("bucket", "Int64", span.Ticks.ToString(CultureInfo.InvariantCulture)));
        }

        for (var i = 0; i < columns.Count; i++)
        {
            var expression = columns[i] switch
            {
                "host" => "ifNull(host, '')",
                "source_type" => "source_type",
                "index" => "index_name",
                "source_file" => "ifNull(source_file, '')",
                _ => "severity",
            };
            select.Add($"{expression} AS g{i}");
            groupBy.Add($"g{i}");
        }

        select.Add("count() AS n");
        var grouping = groupBy.Count == 0 ? string.Empty : " GROUP BY " + string.Join(", ", groupBy);
        var newest = groupBy.Count == 0 ? string.Empty : "max((ts, id)) DESC";
        var order = request.Bucket != null ? " ORDER BY bucket ASC" + (columns.Count > 0 ? ", " + newest : string.Empty) : columns.Count > 0 ? " ORDER BY " + newest : string.Empty;
        p.Add(new Param("groups", "UInt64", (request.MaxGroups + 1).ToString(CultureInfo.InvariantCulture)));
        var sql = $"SELECT {string.Join(", ", select)} FROM {Table} WHERE {where}{grouping}{order} LIMIT {{groups:UInt64}}";

        var rows = new List<Dictionary<string, object?>>();
        long events = 0;
        await foreach (var row in RowsAsync(sql, p, cancellationToken))
        {
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (request.Bucket != null)
            {
                values["_time"] = new DateTime(row.GetProperty("bucket").GetInt64(), DateTimeKind.Utc);
            }

            for (var i = 0; i < columns.Count; i++)
            {
                var cell = row.GetProperty($"g{i}");
                object? value = cell.ValueKind == JsonValueKind.Number ? cell.GetInt32() : cell.GetString();

                // A timechart's split field comes out as text, as the engine writes it
                values[request.GroupBy[i]] = request.Bucket != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : value;
            }

            var count = row.GetProperty("n").GetInt64();
            events += count;
            foreach (var aggregate in request.Aggregates)
            {
                values[aggregate.OutputName] = count <= int.MaxValue ? (int)count : count;
            }

            rows.Add(values);
        }

        // With no grouping, stats has a row even when nothing matched; ClickHouse gives one too
        return new AggregateResult { Rows = rows, EventsScanned = events };
    }
}
