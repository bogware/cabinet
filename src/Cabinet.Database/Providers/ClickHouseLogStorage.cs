using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace Cabinet.Database.Providers;

/// <summary>
/// Events in ClickHouse, over its HTTP interface, for volumes past what one machine's disk holds
/// comfortably. Every value goes in as a typed query parameter, never in the SQL text. Times are
/// stored as ticks, so nothing is rounded. ClickHouse has no unique keys, so a batch's IDs are
/// checked against a Bloom-filter index on <c>id</c> first, and stores from this process take
/// turns; several instances writing the same IDs at the same moment could both store one.
/// Searches mean what they mean on every backend: a term must appear as a whole word, ignoring
/// case, which a Unicode-aware regular expression checks after a cheap substring test.
/// </summary>
public sealed partial class ClickHouseLogStorage : ILogStorage, IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<ClickHouseLogStorage> _logger;
    private readonly string _database;
    private readonly string _table;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile bool _initialized;

    // Fields are stored as written, so "É" is stored as É rather than É. Filters read
    // values with JSONExtractString, which unescapes, so rows stored before this match too.
    private static readonly JsonSerializerOptions FieldsJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <param name="url">The HTTP interface, such as http://clickhouse:8123.</param>
    /// <param name="database">The database to keep events in, created if missing.</param>
    /// <param name="table">The table, created if missing; tests give each run its own.</param>
    public ClickHouseLogStorage(
        string url,
        ILogger<ClickHouseLogStorage> logger,
        string database = "cabinet",
        string table = "events",
        string? username = null,
        string? password = null,
        bool allowInsecureSsl = false)
    {
        if (!Identifier().IsMatch(database) || !Identifier().IsMatch(table))
        {
            throw new ArgumentException("ClickHouse database and table names are letters, digits and underscores");
        }

        var handler = new HttpClientHandler();
        if (allowInsecureSsl)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        _http = new HttpClient(handler) { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(5) };
        if (!string.IsNullOrEmpty(username))
        {
            _http.DefaultRequestHeaders.Add("X-ClickHouse-User", username);
            _http.DefaultRequestHeaders.Add("X-ClickHouse-Key", password ?? string.Empty);
        }

        _logger = logger;
        _database = database;
        _table = table;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Identifier();

    private string Table => $"{_database}.{_table}";

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await ExecuteAsync($"CREATE DATABASE IF NOT EXISTS {_database}", [], cancellationToken);
            await ExecuteAsync(
                $"""
                CREATE TABLE IF NOT EXISTS {Table}
                (
                    id String,
                    ts Int64,
                    index_time Int64,
                    index_name LowCardinality(String),
                    host Nullable(String),
                    source_type LowCardinality(String),
                    severity UInt8,
                    raw String,
                    source_file Nullable(String),
                    line_number Int64,
                    parser Nullable(String),
                    tags Array(String),
                    fields String,
                    INDEX id_bloom id TYPE bloom_filter(0.001) GRANULARITY 1
                )
                ENGINE = MergeTree
                ORDER BY (index_name, ts, id)
                """,
                [],
                cancellationToken);
            _initialized = true;
            _logger.LogInformation("ClickHouse storage ready in {Table}", Table);
        }
        finally
        {
            _initLock.Release();
        }
    }

    // ─── HTTP ───────────────────────────────────────────────────

    /// <summary>A query parameter: its name, ClickHouse type and value, sent apart from the SQL.</summary>
    private readonly record struct Param(string Name, string Type, string Value);

    private static string Text(string? value) => value ?? string.Empty;

    /// <summary>A field filter's value as the text it's compared with; null matches a JSON null.</summary>
    private static string FilterText(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>An Array(String) parameter, in ClickHouse's literal form.</summary>
    private static string StringArray(IEnumerable<string> values) =>
        "[" + string.Join(",", values.Select(v => "'" + v.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'")) + "]";

    private HttpRequestMessage Request(string sql, IReadOnlyList<Param> parameters, string? settings = null)
    {
        var query = new StringBuilder("?output_format_json_quote_64bit_integers=0");
        if (settings != null)
        {
            query.Append('&').Append(settings);
        }

        foreach (var p in parameters)
        {
            query.Append("&param_").Append(p.Name).Append('=').Append(Uri.EscapeDataString(p.Value));
        }

        return new HttpRequestMessage(HttpMethod.Post, query.ToString()) { Content = new StringContent(sql, Encoding.UTF8, "text/plain") };
    }

    private async Task ExecuteAsync(string sql, IReadOnlyList<Param> parameters, CancellationToken cancellationToken, string? settings = null)
    {
        using var response = await _http.SendAsync(Request(sql, parameters, settings), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"ClickHouse returned {(int)response.StatusCode}: {body.Trim()[..Math.Min(500, body.Trim().Length)]}");
        }
    }

    /// <summary>Rows of a SELECT, one JSON object each, read as they arrive.</summary>
    private async IAsyncEnumerable<JsonElement> RowsAsync(string sql, IReadOnlyList<Param> parameters, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = Request(sql + " FORMAT JSONEachRow", parameters);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken), Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length > 0)
            {
                using var document = JsonDocument.Parse(line);
                yield return document.RootElement.Clone();
            }
        }
    }

    private async Task<long> ScalarAsync(string sql, IReadOnlyList<Param> parameters, CancellationToken cancellationToken)
    {
        await foreach (var row in RowsAsync(sql, parameters, cancellationToken))
        {
            foreach (var property in row.EnumerateObject())
            {
                return property.Value.ValueKind == JsonValueKind.String ? long.Parse(property.Value.GetString()!, CultureInfo.InvariantCulture) : property.Value.GetInt64();
            }
        }

        return 0;
    }

    // ─── Writing ────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<bool> StoreAsync(LogEvent logEvent, CancellationToken cancellationToken = default) =>
        await StoreBatchAsync([logEvent], cancellationToken) == 1;

    /// <inheritdoc />
    public async Task<int> StoreBatchAsync(IEnumerable<LogEvent> events, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var batch = new List<LogEvent>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var logEvent in events)
        {
            if (string.IsNullOrEmpty(logEvent.Id))
            {
                logEvent.Id = Ulid.NewUlid();
            }

            if (seen.Add(logEvent.Id))
            {
                batch.Add(logEvent);
            }
        }

        if (batch.Count == 0)
        {
            return 0;
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            // An ID already stored is left alone, as on every backend
            var existing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var chunk in batch.Chunk(5000))
            {
                await foreach (var row in RowsAsync(
                    $"SELECT id FROM {Table} WHERE id IN {{ids:Array(String)}}",
                    [new Param("ids", "Array(String)", StringArray(chunk.Select(e => e.Id)))],
                    cancellationToken))
                {
                    existing.Add(row.GetProperty("id").GetString()!);
                }
            }

            var fresh = batch.Where(e => !existing.Contains(e.Id)).ToList();
            if (fresh.Count == 0)
            {
                return 0;
            }

            var body = new StringBuilder();
            foreach (var e in fresh)
            {
                body.Append(JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["id"] = e.Id,
                    ["ts"] = Utc(e.Timestamp).Ticks,
                    ["index_time"] = Utc(e.IndexTime).Ticks,
                    ["index_name"] = e.Index ?? "main",
                    ["host"] = e.Host,
                    ["source_type"] = e.SourceType ?? string.Empty,
                    ["severity"] = (int)e.Severity,
                    ["raw"] = e.RawLine ?? string.Empty,
                    ["source_file"] = e.SourceFile,
                    ["line_number"] = e.LineNumber,
                    ["parser"] = e.ParserName,
                    ["tags"] = e.GetTagSnapshot(),
                    ["fields"] = JsonSerializer.Serialize(e.Fields, FieldsJson),
                })).Append('\n');
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, $"?query={Uri.EscapeDataString($"INSERT INTO {Table} FORMAT JSONEachRow")}")
            {
                Content = new StringContent(body.ToString(), Encoding.UTF8),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
            using var response = await _http.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            return fresh.Count;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static DateTime Utc(DateTime time) => time.Kind switch
    {
        DateTimeKind.Local => time.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(time, DateTimeKind.Utc),
        _ => time,
    };

    // ─── Reading ────────────────────────────────────────────────

    private const string Columns = "id, ts, index_time, index_name, host, source_type, severity, raw, source_file, line_number, parser, tags, fields";

    /// <summary>The WHERE clause for a query's filters, with its parameters.</summary>
    private static string Where(QueryContext context, List<Param> p)
    {
        var sql = new StringBuilder("1");

        // A word, whole, ignoring case, as SearchTerms defines it. The substring test is cheap
        // and necessary; the expression then checks the word's edges with Unicode letters.
        var terms = SearchTerms.Parse(context.SearchText);
        for (var i = 0; i < terms.Count; i++)
        {
            sql.Append($" AND positionCaseInsensitiveUTF8(raw, {{t{i}:String}}) > 0 AND match(raw, {{w{i}:String}})");
            p.Add(new Param($"t{i}", "String", terms[i]));
            p.Add(new Param($"w{i}", "String", $"(?i)(?:^|[^\\p{{L}}\\p{{N}}]){Regex.Escape(terms[i])}(?:$|[^\\p{{L}}\\p{{N}}])"));
        }

        if (context.StartTime is { } start)
        {
            sql.Append(" AND ts >= {start:Int64}");
            p.Add(new Param("start", "Int64", Number(Utc(start).Ticks)));
        }

        if (context.EndTime is { } end)
        {
            sql.Append(" AND ts <= {end:Int64}");
            p.Add(new Param("end", "Int64", Number(Utc(end).Ticks)));
        }

        if (!string.IsNullOrEmpty(context.SourceType))
        {
            sql.Append(" AND lowerUTF8(source_type) = lowerUTF8({sourceType:String})");
            p.Add(new Param("sourceType", "String", context.SourceType));
        }

        if (context.MinSeverity is { } severity)
        {
            sql.Append(" AND severity >= {severity:UInt8}");
            p.Add(new Param("severity", "UInt8", Number((int)severity)));
        }

        if (!string.IsNullOrWhiteSpace(context.Host))
        {
            sql.Append(" AND lowerUTF8(ifNull(host, '')) = lowerUTF8({host:String})");
            p.Add(new Param("host", "String", context.Host));
        }

        if (!string.IsNullOrEmpty(context.Index))
        {
            sql.Append(" AND index_name = {index:String}");
            p.Add(new Param("index", "String", context.Index));
        }

        if (context.AllowedIndexes is { } allowed)
        {
            var conditions = new List<string>();
            for (var i = 0; i < allowed.Count; i++)
            {
                var escaped = allowed[i].Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
                conditions.Add($"ilike(index_name, {{allowed{i}:String}})");
                p.Add(new Param($"allowed{i}", "String", escaped.EndsWith('*') ? escaped[..^1] + "%" : escaped));
            }

            sql.Append($" AND ({(conditions.Count == 0 ? "0" : string.Join(" OR ", conditions))})");
        }

        if (context.Tags is { Count: > 0 } tags)
        {
            sql.Append(" AND hasAll(tags, {tags:Array(String)})");
            p.Add(new Param("tags", "Array(String)", StringArray(tags)));
        }

        if (context.FieldFilters is { Count: > 0 } filters)
        {
            var n = 0;
            foreach (var (name, expected) in filters)
            {
                // A field named in any case, as LogEvent.Fields is, whose value as text equals one
                // of the options ignoring case; an array means any of its values. A string is
                // compared unescaped, whatever escaping it was stored with.
                var options = expected is object?[] any ? any : [expected];
                var alternatives = options.Select((_, k) => $"lowerUTF8(if(JSONType(kv.2) = 'String', JSONExtractString(kv.2), kv.2)) = lowerUTF8({{fs{n}_{k}:String}})");
                sql.Append($" AND arrayExists(kv -> lowerUTF8(kv.1) = lowerUTF8({{ff{n}:String}}) AND ({string.Join(" OR ", alternatives)}), JSONExtractKeysAndValuesRaw(fields))");
                p.Add(new Param($"ff{n}", "String", name));
                for (var k = 0; k < options.Length; k++)
                {
                    p.Add(new Param($"fs{n}_{k}", "String", FilterText(options[k])));
                }

                n++;
            }
        }

        return sql.ToString();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<LogEvent> QueryAsync(QueryContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var p = new List<Param>();
        var where = Where(context, p);
        var direction = context.SortDescending ? "DESC" : "ASC";
        if (context.After is { } after)
        {
            where += context.SortDescending ? " AND (ts, id) < ({afterTs:Int64}, {afterId:String})" : " AND (ts, id) > ({afterTs:Int64}, {afterId:String})";
            p.Add(new Param("afterTs", "Int64", Number(Utc(after.Timestamp).Ticks)));
            p.Add(new Param("afterId", "String", after.Id));
        }

        var order = context.SortField.Equals("severity", StringComparison.OrdinalIgnoreCase)
            ? $"severity {direction}, id {direction}"
            : $"ts {direction}, id {direction}";
        p.Add(new Param("limit", "UInt64", Number(Math.Max(0, context.Limit))));
        p.Add(new Param("skip", "UInt64", Number(Math.Max(0, context.Skip))));
        var sql = $"SELECT {Columns} FROM {Table} WHERE {where} ORDER BY {order} LIMIT {{limit:UInt64}} OFFSET {{skip:UInt64}}";
        await foreach (var row in RowsAsync(sql, p, cancellationToken))
        {
            yield return ToEvent(row);
        }
    }

    private static LogEvent ToEvent(JsonElement row)
    {
        string? Nullable(string name) => row.GetProperty(name).ValueKind == JsonValueKind.Null ? null : row.GetProperty(name).GetString();
        var logEvent = new LogEvent
        {
            Id = row.GetProperty("id").GetString()!,
            Timestamp = new DateTime(row.GetProperty("ts").GetInt64(), DateTimeKind.Utc),
            IndexTime = new DateTime(row.GetProperty("index_time").GetInt64(), DateTimeKind.Utc),
            Index = row.GetProperty("index_name").GetString()!,
            Host = Nullable("host"),
            SourceType = row.GetProperty("source_type").GetString()!,
            Severity = (LogSeverity)row.GetProperty("severity").GetInt32(),
            RawLine = row.GetProperty("raw").GetString()!,
            SourceFile = Nullable("source_file"),
            LineNumber = row.GetProperty("line_number").GetInt64(),
            ParserName = Nullable("parser"),
        };
        foreach (var tag in row.GetProperty("tags").EnumerateArray())
        {
            logEvent.Tags.Add(tag.GetString()!);
        }

        if (row.GetProperty("fields").GetString() is { Length: > 2 } fields)
        {
            foreach (var (name, value) in JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(fields)!)
            {
                logEvent.SetField(name, JsonFieldValue.Unwrap(value));
            }
        }

        return logEvent;
    }

    /// <inheritdoc />
    public async Task<LogEvent?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await foreach (var row in RowsAsync($"SELECT {Columns} FROM {Table} WHERE id = {{id:String}} LIMIT 1", [new Param("id", "String", id)], cancellationToken))
        {
            return ToEvent(row);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<long> GetCountAsync(QueryContext? context = null, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var p = new List<Param>();
        var where = Where(context ?? new QueryContext(), p);
        return await ScalarAsync($"SELECT count() AS n FROM {Table} WHERE {where}", p, cancellationToken);
    }

    // ─── Deleting ───────────────────────────────────────────────

    private async Task<int> DeleteWhereAsync(string where, List<Param> p, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var count = await ScalarAsync($"SELECT count() AS n FROM {Table} WHERE {where}", p, cancellationToken);
            if (count > 0)
            {
                // A lightweight delete: rows are gone from reads at once, and from disk at the next merge
                await ExecuteAsync($"DELETE FROM {Table} WHERE {where}", p, cancellationToken);
            }

            return (int)Math.Min(int.MaxValue, count);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public Task<int> PurgeOldEventsAsync(DateTime before, CancellationToken cancellationToken = default) =>
        DeleteWhereAsync("ts < {before:Int64}", [new Param("before", "Int64", Number(Utc(before).Ticks))], cancellationToken);

    /// <inheritdoc />
    public Task<int> PurgeIndexEventsAsync(string indexName, DateTime before, CancellationToken cancellationToken = default) =>
        DeleteWhereAsync(
            "index_name = {index:String} AND ts < {before:Int64}",
            [new Param("index", "String", indexName), new Param("before", "Int64", Number(Utc(before).Ticks))],
            cancellationToken);

    /// <inheritdoc />
    public Task<int> DeleteByIndexAsync(string indexName, CancellationToken cancellationToken = default) =>
        DeleteWhereAsync("index_name = {index:String}", [new Param("index", "String", indexName)], cancellationToken);

    // ─── Statistics ─────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<StorageStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var stats = new StorageStats();
        await foreach (var row in RowsAsync($"SELECT count() AS n, min(ts) AS first, max(ts) AS last FROM {Table}", [], cancellationToken))
        {
            stats.TotalEvents = row.GetProperty("n").GetInt64();
            if (stats.TotalEvents > 0)
            {
                stats.OldestEvent = new DateTime(row.GetProperty("first").GetInt64(), DateTimeKind.Utc);
                stats.NewestEvent = new DateTime(row.GetProperty("last").GetInt64(), DateTimeKind.Utc);
            }
        }

        await foreach (var row in RowsAsync($"SELECT source_type AS k, count() AS n FROM {Table} GROUP BY k", [], cancellationToken))
        {
            stats.EventsBySourceType[row.GetProperty("k").GetString()!] = row.GetProperty("n").GetInt64();
        }

        await foreach (var row in RowsAsync($"SELECT severity AS k, count() AS n FROM {Table} GROUP BY k", [], cancellationToken))
        {
            stats.EventsBySeverity[(LogSeverity)row.GetProperty("k").GetInt32()] = row.GetProperty("n").GetInt64();
        }

        await foreach (var row in RowsAsync($"SELECT index_name AS k, count() AS n FROM {Table} GROUP BY k", [], cancellationToken))
        {
            stats.EventsByIndex[row.GetProperty("k").GetString()!] = row.GetProperty("n").GetInt64();
        }

        stats.StorageSizeBytes = await ScalarAsync(
            "SELECT sum(bytes_on_disk) AS n FROM system.parts WHERE active AND database = {db:String} AND table = {table:String}",
            [new Param("db", "String", _database), new Param("table", "String", _table)],
            cancellationToken);
        return stats;
    }

    /// <summary>Drops the table, for tests that give each run its own.</summary>
    public Task DropAsync(CancellationToken cancellationToken = default) => ExecuteAsync($"DROP TABLE IF EXISTS {Table}", [], cancellationToken);

    public void Dispose()
    {
        _http.Dispose();
        _initLock.Dispose();
        _writeLock.Dispose();
    }
}
