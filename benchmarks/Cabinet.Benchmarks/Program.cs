using System.Diagnostics;
using System.Globalization;
using Cabinet.Core.Configuration;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Database.Providers;
using Cabinet.Parsers;
using Cabinet.Query;
using Cabinet.Query.Bql;
using Cabinet.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// Usage: Cabinet.Benchmarks <sqlite|memory|postgres|opensearch> <events> [connection]
//
// Generates log lines, parses them with the real parsers, stores them through ILogStorage, then
// runs queries through the real engine. Prints a Markdown table. Results are in docs/BENCHMARKS.md.
var backend = args.Length > 0 ? args[0] : "sqlite";
var eventCount = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 100_000;
var connection = args.Length > 2 ? args[2] : null;

const int BatchSize = 1000;
const int QueryRuns = 3;
const int Hosts = 20;
const int NeedleEvery = 10_000;

var end = DateTime.UtcNow;
var span = TimeSpan.FromDays(7);
var databasePath = Path.Combine(Path.GetTempPath(), $"cabinet-bench-{Guid.NewGuid():N}.db");
var openSearchPrefix = $"cabinet-bench-{Guid.NewGuid():N}";

ILogStorage storage = backend switch
{
    "memory" => new InMemoryStorage(maxEvents: eventCount + 1),
    "sqlite" => CreateSqlite(databasePath),
    "postgres" => new PostgresLogStorage(connection ?? throw new ArgumentException("Pass a connection string"), NullLogger<PostgresLogStorage>.Instance),
    "opensearch" => new OpenSearchLogStorage(connection ?? throw new ArgumentException("Pass a URL"), NullLogger<OpenSearchLogStorage>.Instance, indexPrefix: openSearchPrefix),
    _ => throw new ArgumentException($"Unknown backend: {backend}"),
};
await storage.InitializeAsync();

var lineParser = new ServiceCollection()
    .AddLogging()
    .AddCabinetParsers()
    .BuildServiceProvider()
    .GetRequiredService<LogLineParser>();

var benchIndex = $"bench-{Guid.NewGuid():N}";

// Parsing, on its own, single-threaded
var parseWatch = Stopwatch.StartNew();
var parseSample = Math.Min(eventCount, 200_000);
for (var i = 0; i < parseSample; i++)
{
    await lineParser.ParseAsync(Line(i));
}

parseWatch.Stop();
var parsedPerSecond = parseSample / parseWatch.Elapsed.TotalSeconds;

// Storing, from one writer in batches, including parsing
var ingestWatch = Stopwatch.StartNew();
var batch = new List<LogEvent>(BatchSize);
for (var i = 0; i < eventCount; i++)
{
    var logEvent = await lineParser.ParseAsync(Line(i));
    logEvent.Index = benchIndex;
    batch.Add(logEvent);
    if (batch.Count == BatchSize)
    {
        await storage.StoreBatchAsync(batch);
        batch.Clear();
    }
}

if (batch.Count > 0)
{
    await storage.StoreBatchAsync(batch);
}

ingestWatch.Stop();
var ingestedPerSecond = eventCount / ingestWatch.Elapsed.TotalSeconds;

// OpenSearch only shows events after a refresh, which happens every 5 seconds
if (backend == "opensearch")
{
    await Task.Delay(TimeSpan.FromSeconds(10));
}

var engine = new BqlQueryEngine(storage, new BqlParser(), new BqlOptimizer(), NullLogger<BqlQueryEngine>.Instance);
var lastHour = end.AddHours(-1);

(string Name, string Query, DateTime? Start)[] queries =
[
    ("Newest 100", "find * | head 100", null),
    ("Newest 100 on one host", "find * | where host = \"web-03\" | head 100", null),
    ("Text search for a rare word, first 100", "find \"needle\" | head 100", null),
    ("Errors and worse, first 100", "find * | where severity >= error | head 100", null),
    ("Count everything", "find * | stats count", null),
    ("Last hour: count by host", "find * | stats count by host", lastHour),
    ("All events: count by host", "find * | stats count by host", null),
    ("All events: hourly timechart", "find * | timechart span=1h count", null),
    ("All events: avg and p95 by host", "find * | stats avg(duration_ms) as a, p95(duration_ms) as p by host", null),
    ("All events: top 10 paths", "find * | top 10 path", null),
];

Console.WriteLine($"## {backend}, {eventCount:N0} events");
Console.WriteLine();
Console.WriteLine($"| Parse | {parsedPerSecond:N0} lines/s |");
Console.WriteLine("|---|---|");
Console.WriteLine($"| Parse and store | {ingestedPerSecond:N0} events/s |");
Console.WriteLine();
Console.WriteLine("| Query | Median | Events read | Rows |");
Console.WriteLine("|---|---:|---:|---:|");

foreach (var (name, query, start) in queries)
{
    var times = new List<TimeSpan>();
    QueryResult? result = null;
    for (var run = 0; run < QueryRuns; run++)
    {
        var watch = Stopwatch.StartNew();
        result = await engine.ExecuteAsync(new QueryExecutionContext
        {
            Query = query,
            Limit = 10_000,
            Index = benchIndex,
            StartTime = start,
            Timeout = TimeSpan.FromHours(1),
        });
        times.Add(watch.Elapsed);
    }

    times.Sort();
    Console.WriteLine(FormattableString.Invariant(
        $"| {name} | {Format(times[QueryRuns / 2])} | {result!.Statistics!.EventsScanned:N0} | {result.Events.Count:N0} |"));
}

Console.WriteLine();
Console.WriteLine($"Peak process memory: {Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024):N0} MB");

if (backend == "sqlite")
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Console.WriteLine($"Database size: {new FileInfo(databasePath).Length / (1024 * 1024):N0} MB");
    foreach (var file in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
    {
        File.Delete(file);
    }
}
else if (backend != "memory")
{
    await storage.DeleteByIndexAsync(benchIndex);
}

string Line(int i)
{
    var time = end - TimeSpan.FromTicks(span.Ticks / eventCount * (i + 1));
    var host = $"web-{i % Hosts:00}";
    var path = (i % 7) switch
    {
        0 => "/",
        1 => "/api/v1/users",
        2 => "/api/v1/orders",
        3 => "/static/app.js",
        4 => "/health",
        5 => "/login",
        _ => $"/item/{i % 500}",
    };
    var level = (i % 100) switch
    {
        0 => "error",
        < 6 => "warn",
        _ => "info",
    };
    var status = level == "error" ? 500 : level == "warn" ? 429 : 200;
    var duration = (i * 7919 % 1000) + 1;
    var word = i % NeedleEvery == 0 ? "needle " : string.Empty;
    return string.Create(
        CultureInfo.InvariantCulture,
        $"{time:yyyy-MM-ddTHH:mm:ss.fffZ} level={level} host={host} method=GET path={path} status={status} duration_ms={duration} {word}request handled by worker {i % 16}");
}

static string Format(TimeSpan time) => time.TotalSeconds >= 10
    ? $"{time.TotalSeconds:N0} s"
    : time.TotalSeconds >= 1
        ? $"{time.TotalSeconds:N2} s"
        : $"{time.TotalMilliseconds:N1} ms";

static SqliteLogStorage CreateSqlite(string path)
{
    var configuration = new CabinetConfiguration();
    configuration.Storage.ConnectionString = $"Data Source={path}";
    return new SqliteLogStorage(Options.Create(configuration), NullLogger<SqliteLogStorage>.Instance);
}
