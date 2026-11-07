using System.Globalization;
using System.Text;
using System.Text.Json;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Dapper;
using Npgsql;

namespace Cabinet.Database.Providers;

/// <summary>
/// Aggregates in SQL. Each part mirrors the query engine: custom fields match by name ignoring
/// case, a group's values are those of its newest event, numbers in text count as numbers, and
/// groups come out newest first.
/// </summary>
public sealed partial class PostgresLogStorage : IAggregatingLogStorage
{
    // What double.TryParse with NumberStyles.Float accepts, for text that holds a number
    private const string NumberPattern = @"^\s*[-+]?([0-9]+\.?[0-9]*|\.[0-9]+)([eE][-+]?[0-9]+)?\s*$";

    /// <inheritdoc />
    public async Task<AggregateResult?> AggregateAsync(AggregateRequest request, CancellationToken cancellationToken = default)
    {
        if (request.GroupBy.Any(field => !AggregateFields.CanPush(field))
            || request.Aggregates.Any(spec => spec.Field != null && !AggregateFields.CanPush(spec.Field)))
        {
            return null;
        }

        await EnsureInitializedAsync(cancellationToken);

        var parameters = new DynamicParameters();
        var where = new StringBuilder(" WHERE 1=1");
        AppendFilters(where, parameters, request.Filter);

        // The projection: one column per group and per aggregate input
        var columns = new List<string> { "timestamp AS __ts", "id AS __id" };

        // Derived from the columns above in a second step, so that a JSON lookup runs once a row
        var derived = new List<string>();
        var groups = new List<GroupColumn>();
        for (var i = 0; i < request.GroupBy.Count; i++)
        {
            var name = request.GroupBy[i];
            var (value, key, builtin) = FieldExpressions(name, $"g{i}", parameters);
            columns.Add($"{value} AS gv{i}");
            if (builtin != null)
            {
                columns.Add($"{key} AS gk{i}");
            }
            else
            {
                derived.Add($"COALESCE(gv{i} #>> '{{}}', '') AS gk{i}");
            }

            groups.Add(new GroupColumn(name, builtin, i));
        }

        if (request.Bucket is { } bucket)
        {
            if (bucket.TotalSeconds < 1 || bucket.Ticks % TimeSpan.TicksPerSecond != 0)
            {
                return null;
            }

            parameters.Add("BucketSeconds", bucket.TotalSeconds);
            parameters.Add("EpochOffset", (double)AggregateFields.EpochOffsetSeconds);
            columns.Add("to_timestamp(floor((extract(epoch FROM timestamp) + @EpochOffset) / @BucketSeconds) * @BucketSeconds - @EpochOffset) AS __bucket");
        }

        var aggregates = new List<string>();
        var fallbackChecks = new List<string>();
        for (var i = 0; i < request.Aggregates.Count; i++)
        {
            var spec = request.Aggregates[i];
            if (spec.Field == null)
            {
                aggregates.Add("count(*)");
                continue;
            }

            var (value, key, builtin) = FieldExpressions(spec.Field, $"a{i}", parameters);
            var present = builtin == "text" || builtin == "int" ? "true" : $"(av{i} IS NOT NULL AND jsonb_typeof(av{i}) <> 'null')";
            var number = builtin switch
            {
                "int" => $"av{i}::double precision",
                "text" => $"CASE WHEN av{i} ~ '{NumberPattern}' THEN av{i}::double precision END",
                _ => $"CASE jsonb_typeof(av{i}) WHEN 'number' THEN (av{i}::text)::double precision WHEN 'string' THEN CASE WHEN (av{i} #>> '{{}}') ~ '{NumberPattern}' THEN (av{i} #>> '{{}}')::double precision END END",
            };
            columns.Add($"{value} AS av{i}");
            if (builtin != null)
            {
                columns.Add($"{key} AS at{i}");
            }
            else
            {
                derived.Add($"av{i} #>> '{{}}' AS at{i}");
            }

            aggregates.Add(spec.Kind switch
            {
                AggregateKind.Count => $"count(*) FILTER (WHERE {present})",
                AggregateKind.DistinctCount => $"count(DISTINCT lower(at{i}))",
                AggregateKind.Sum => $"sum({number})",
                AggregateKind.Average => $"avg({number})",
                AggregateKind.Min => $"min({number})",
                AggregateKind.Max => $"max({number})",
                AggregateKind.Percentile => $"percentile_cont({(spec.Percentile / 100).ToString(CultureInfo.InvariantCulture)}) WITHIN GROUP (ORDER BY {number})",
                AggregateKind.SampleDeviation => $"stddev_samp({number})",
                AggregateKind.PopulationDeviation => $"stddev_pop({number})",
                AggregateKind.SampleVariance => $"var_samp({number})",
                AggregateKind.PopulationVariance => $"var_pop({number})",
                _ => throw new NotSupportedException(spec.Kind.ToString()),
            });

            // With no numbers, the engine's min and max compare text instead, which SQL can't match
            if (spec.Kind is AggregateKind.Min or AggregateKind.Max)
            {
                fallbackChecks.Add($"(count({number}) = 0 AND count(at{i}) > 0)");
            }
        }

        var keys = groups.Select(group => $"gk{group.Index}").ToList();
        if (request.Bucket != null)
        {
            keys.Insert(0, "__bucket");
        }

        var select = new List<string>();
        if (request.Bucket != null)
        {
            select.Add("__bucket");
        }

        select.AddRange(groups.Select(group => group.Builtin == null
            ? $"(array_agg(gv{group.Index} ORDER BY __ts DESC, __id DESC))[1]::text"
            : $"min(gv{group.Index})"));
        select.AddRange(groups.Select(group => $"min(gk{group.Index})"));
        select.AddRange(aggregates);
        select.Add("count(*)");
        select.AddRange(fallbackChecks);

        var groupBy = keys.Count == 0 ? string.Empty : $" GROUP BY {string.Join(", ", keys)}";
        var order = request.Bucket != null ? " ORDER BY __bucket ASC, max(__ts) DESC, max(__id) DESC" : keys.Count == 0 ? string.Empty : " ORDER BY max(__ts) DESC, max(__id) DESC";
        parameters.Add("GroupLimit", request.MaxGroups + 1);
        var source = $"SELECT {string.Join(", ", columns)} FROM log_events{where}";
        if (derived.Count > 0)
        {
            source = $"SELECT raw.*, {string.Join(", ", derived)} FROM ({source}) raw";
        }

        var sql = $"WITH src AS ({source}) SELECT {string.Join(", ", select)} FROM src{groupBy}{order} LIMIT @GroupLimit";

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var reader = await connection.ExecuteReaderAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));

        var rows = new List<Dictionary<string, object?>>();
        long events = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            var column = 0;
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (request.Bucket != null)
            {
                row["_time"] = DateTime.SpecifyKind(reader.GetDateTime(column++), DateTimeKind.Utc);
            }

            var values = new object?[groups.Count];
            for (var g = 0; g < groups.Count; g++)
            {
                values[g] = groups[g].Builtin switch
                {
                    "int" => reader.IsDBNull(column) ? null : (object)Convert.ToInt32(reader.GetValue(column), CultureInfo.InvariantCulture),
                    "text" => reader.IsDBNull(column) ? null : reader.GetString(column),
                    _ => reader.IsDBNull(column) ? null : Unwrap(reader.GetString(column)),
                };
                column++;
            }

            var groupKeys = new string?[groups.Count];
            for (var g = 0; g < groups.Count; g++)
            {
                groupKeys[g] = reader.IsDBNull(column) ? string.Empty : reader.GetString(column);
                column++;
            }

            for (var g = 0; g < groups.Count; g++)
            {
                // A timechart's split field comes out as text, as the engine writes it
                row[groups[g].Name] = request.Bucket != null ? groupKeys[g] : values[g];
            }

            foreach (var spec in request.Aggregates)
            {
                var value = reader.IsDBNull(column) ? null : reader.GetValue(column);
                column++;
                row[spec.OutputName] = spec.Kind is AggregateKind.Count or AggregateKind.DistinctCount
                    ? CountValue(Convert.ToInt64(value ?? 0L, CultureInfo.InvariantCulture))
                    : value == null ? null : Finite(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            }

            events += reader.GetInt64(column++);
            for (var check = 0; check < fallbackChecks.Count; check++)
            {
                if (reader.GetBoolean(column + check))
                {
                    return null;
                }
            }

            rows.Add(row);
        }

        return new AggregateResult { Rows = rows, EventsScanned = events };
    }

    /// <summary>
    /// For a field: its value, the text it groups by, and what kind of built-in column it is
    /// (int, text) or null for a custom field held in JSON.
    /// </summary>
    private static (string Value, string Key, string? Builtin) FieldExpressions(string name, string parameter, DynamicParameters parameters)
    {
        switch (AggregateFields.Builtin(name))
        {
            case "host":
                return ("COALESCE(server_id, '')", "COALESCE(server_id, '')", "text");
            case "source_type":
                return ("source_type", "source_type", "text");
            case "index":
                return ("index_name", "index_name", "text");
            case "source_file":
                return ("COALESCE(source_file, '')", "COALESCE(source_file, '')", "text");
            case "severity":
                return ("severity", "severity::text", "int");
        }

        parameters.Add(parameter, AggregateFields.CustomName(name));
        // The exact name first, which is cheap, then any key that differs only in case
        var value = $"COALESCE(fields -> @{parameter}, (SELECT e.value FROM jsonb_each(fields) e WHERE lower(e.key) = lower(@{parameter}) LIMIT 1))";
        return (value, $"COALESCE({value} #>> '{{}}', '')", null);
    }

    private static object? Unwrap(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonFieldValue.Unwrap(document.RootElement);
    }

    private static object CountValue(long count) => count <= int.MaxValue ? (int)count : count;

    private static object? Finite(double value) => double.IsFinite(value) ? value : null;

    private sealed record GroupColumn(string Name, string? Builtin, int Index);
}
