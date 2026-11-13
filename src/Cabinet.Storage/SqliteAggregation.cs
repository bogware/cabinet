using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Cabinet.Core.Interfaces;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Cabinet.Storage;

/// <summary>
/// Aggregates in SQL, mirroring the query engine: custom fields match by name ignoring case, a
/// group's values are those of its newest event, numbers in text count as numbers, and groups
/// come out newest first. SQLite has no percentiles or deviations, so those stay in the engine.
/// </summary>
public partial class SqliteLogStorage : IAggregatingLogStorage
{
    // What double.TryParse with NumberStyles.Float accepts, for text that holds a number
    private static readonly Regex NumberText = new(@"^\s*[-+]?([0-9]+\.?[0-9]*|\.[0-9]+)([eE][-+]?[0-9]+)?\s*$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <inheritdoc />
    public async Task<AggregateResult?> AggregateAsync(AggregateRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ExactTextKeys
            || request.GroupBy.Any(field => !AggregateFields.CanPush(field))
            || request.Aggregates.Any(spec => (spec.Field != null && !AggregateFields.CanPush(spec.Field))
                || spec.Kind is AggregateKind.Percentile or AggregateKind.SampleDeviation or AggregateKind.PopulationDeviation
                    or AggregateKind.SampleVariance or AggregateKind.PopulationVariance)
            || (request.Bucket is { } span && span.Ticks <= 0))
        {
            return null;
        }

        await EnsureInitializedAsync();

        var parameters = (DynamicParameters)BuildQueryParameters(request.Filter);
        var filtered = BuildCountSql(request.Filter);
        const string CountPrefix = "SELECT COUNT(*) FROM ";
        if (!filtered.StartsWith(CountPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var columns = new List<string> { "timestamp AS __ts", "id AS __id" };
        var groups = new List<(string Name, string? Builtin, int Index)>();
        for (var i = 0; i < request.GroupBy.Count; i++)
        {
            var (value, type, key, builtin) = Field(request.GroupBy[i], $"g{i}", parameters);
            columns.Add($"{value} AS gv{i}");
            columns.Add($"{type} AS gt{i}");
            columns.Add($"{key} AS gk{i}");
            groups.Add((request.GroupBy[i], builtin, i));
        }

        if (request.Bucket is { } bucket)
        {
            parameters.Add("BucketTicks", bucket.Ticks);
            columns.Add("(timestamp / @BucketTicks) * @BucketTicks AS __bucket");
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

            var (value, type, _, builtin) = Field(spec.Field, $"a{i}", parameters);
            columns.Add($"{value} AS av{i}");
            columns.Add($"{type} AS at{i}");
            var present = builtin != null ? "1" : $"(at{i} IS NOT NULL AND at{i} <> 'null')";
            var text = $"(CASE WHEN at{i} IN ('true', 'false') THEN CASE av{i} WHEN 1 THEN 'True' ELSE 'False' END WHEN at{i} IS NULL OR at{i} = 'null' THEN NULL ELSE CAST(av{i} AS TEXT) END)";
            var number = $"(CASE WHEN at{i} IN ('integer', 'real') THEN CAST(av{i} AS REAL) WHEN at{i} = 'text' AND regexp_number(av{i}) THEN CAST(trim(av{i}) AS REAL) END)";
            aggregates.Add(spec.Kind switch
            {
                AggregateKind.Count => $"sum(CASE WHEN {present} THEN 1 ELSE 0 END)",
                AggregateKind.DistinctCount => $"count(DISTINCT lower({text}))",
                AggregateKind.Sum => $"sum({number})",
                AggregateKind.Average => $"avg({number})",
                AggregateKind.Min => $"min({number})",
                AggregateKind.Max => $"max({number})",
                _ => throw new NotSupportedException(spec.Kind.ToString()),
            });

            if (spec.Kind is AggregateKind.Min or AggregateKind.Max)
            {
                fallbackChecks.Add($"(count({number}) = 0 AND count({text}) > 0)");
            }
        }

        var partition = groups.Select(group => $"gk{group.Index}").ToList();
        if (request.Bucket != null)
        {
            partition.Insert(0, "__bucket");
        }

        // The newest event in each group gives its values
        var newest = partition.Count == 0 ? "1" : $"ROW_NUMBER() OVER (PARTITION BY {string.Join(", ", partition)} ORDER BY __ts DESC, __id DESC)";
        var select = new List<string>();
        if (request.Bucket != null)
        {
            select.Add("__bucket");
        }

        foreach (var group in groups)
        {
            select.Add($"max(CASE WHEN __rn = 1 THEN gv{group.Index} END)");
            select.Add($"max(CASE WHEN __rn = 1 THEN gt{group.Index} END)");
            select.Add($"max(gk{group.Index})");
        }

        select.AddRange(aggregates);
        select.Add("count(*)");
        select.AddRange(fallbackChecks.Select(check => $"CASE WHEN {check} THEN 1 ELSE 0 END"));

        var groupBy = partition.Count == 0 ? string.Empty : $" GROUP BY {string.Join(", ", partition)}";
        var order = request.Bucket != null ? " ORDER BY __bucket ASC, max(__ts) DESC, max(__id) DESC" : partition.Count == 0 ? string.Empty : " ORDER BY max(__ts) DESC, max(__id) DESC";
        parameters.Add("GroupLimit", request.MaxGroups + 1);
        var source = $"SELECT {string.Join(", ", columns)} FROM {filtered[CountPrefix.Length..]}";
        var sql = $"WITH src AS ({source}), ranked AS (SELECT *, {newest} AS __rn FROM src) SELECT {string.Join(", ", select)} FROM ranked{groupBy}{order} LIMIT @GroupLimit";

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        connection.CreateFunction("regexp_number", (string? text) => text != null && NumberText.IsMatch(text));
        await using var reader = await connection.ExecuteReaderAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));

        var rows = new List<Dictionary<string, object?>>();
        long events = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            var column = 0;
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (request.Bucket != null)
            {
                row["_time"] = new DateTime(reader.GetInt64(column++), DateTimeKind.Utc);
            }

            foreach (var group in groups)
            {
                var value = reader.IsDBNull(column) ? null : reader.GetValue(column);
                var type = reader.IsDBNull(column + 1) ? null : reader.GetString(column + 1);
                var key = reader.IsDBNull(column + 2) ? string.Empty : reader.GetString(column + 2);
                column += 3;

                // A timechart's split field comes out as text, as the engine writes it
                row[group.Name] = request.Bucket != null ? key : group.Builtin switch
                {
                    "int" => value == null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture),
                    "text" => value?.ToString(),
                    _ => Typed(value, type),
                };
            }

            foreach (var spec in request.Aggregates)
            {
                var value = reader.IsDBNull(column) ? null : reader.GetValue(column);
                column++;
                row[spec.OutputName] = spec.Kind is AggregateKind.Count or AggregateKind.DistinctCount
                    ? CountValue(Convert.ToInt64(value ?? 0L, CultureInfo.InvariantCulture))
                    : value == null ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }

            events += reader.GetInt64(column++);
            for (var check = 0; check < fallbackChecks.Count; check++)
            {
                if (reader.GetInt64(column + check) != 0)
                {
                    return null;
                }
            }

            rows.Add(row);
        }

        return new AggregateResult { Rows = rows, EventsScanned = events };
    }

    /// <summary>Value, JSON type, grouping text, and the kind of built-in column, or null for a custom field.</summary>
    private static (string Value, string Type, string Key, string? Builtin) Field(string name, string parameter, DynamicParameters parameters)
    {
        switch (AggregateFields.Builtin(name))
        {
            case "host":
                return ("COALESCE(server_id, '')", "'text'", "COALESCE(server_id, '')", "text");
            case "source_type":
                return ("source_type", "'text'", "source_type", "text");
            case "index":
                return ("index_name", "'text'", "index_name", "text");
            case "source_file":
                return ("COALESCE(source_file, '')", "'text'", "COALESCE(source_file, '')", "text");
            case "severity":
                return ("severity", "'integer'", "CAST(severity AS TEXT)", "int");
        }

        var field = AggregateFields.CustomName(name);
        parameters.Add(parameter, field);
        var lookup = $"FROM json_each(log_events.fields) j WHERE lower(j.key) = lower(@{parameter}) LIMIT 1";
        var value = $"(SELECT j.value {lookup})";
        var type = $"(SELECT j.type {lookup})";

        // The exact name first, which is cheap, then any key that differs only in case. A JSON
        // path can't hold every name, so others always take the slow way.
        if (!field.Contains('"') && !field.Contains('\\'))
        {
            var path = $"'$.\"' || @{parameter} || '\"'";
            value = $"(CASE WHEN json_type(log_events.fields, {path}) IS NOT NULL THEN json_extract(log_events.fields, {path}) ELSE {value} END)";
            type = $"COALESCE(json_type(log_events.fields, {path}), {type})";
        }

        var key = $"COALESCE(CASE {type} WHEN 'true' THEN 'True' WHEN 'false' THEN 'False' WHEN 'null' THEN NULL ELSE CAST({value} AS TEXT) END, '')";
        return (value, type, key, null);
    }

    private static object? Typed(object? value, string? type) => type switch
    {
        null or "null" => null,
        "true" => true,
        "false" => false,
        "integer" => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        "real" => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        _ => value?.ToString(),
    };

    private static object CountValue(long count) => count <= int.MaxValue ? (int)count : count;
}
