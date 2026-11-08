using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// Load and soak test against running API instances. Writers send HEC batches with their own event
// IDs and retry until each batch is accepted, readers run a mix of queries, and memory is sampled
// from each instance's /metrics. At the end, every acknowledged event has to be stored exactly once.
//
//   Cabinet.LoadTest --urls http://a:5000,http://b:5000 --key KEY --duration 30m --rate 2000
//
// Exits non-zero if events were lost or duplicated, too many requests failed, or memory kept growing.

var options = Options.Parse(args);
var runId = $"load{DateTime.UtcNow:yyyyMMddHHmmss}";
var index = $"loadtest-{runId}";
using var stopping = new CancellationTokenSource(options.Duration);
var clients = options.Urls.Select(url => new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(120) }).ToArray();
foreach (var client in clients)
{
    client.DefaultRequestHeaders.Add("X-API-Key", options.Key);
}

var stats = new Stats();
var watch = Stopwatch.StartNew();
Console.WriteLine($"Run {runId}: {options.Urls.Length} instance(s), {options.Rate:N0} events/s target, {options.Queriers} queriers, {options.Duration}");

var writers = Enumerable.Range(0, options.Writers).Select(w => Task.Run(() => WriteAsync(w))).ToArray();
var queriers = Enumerable.Range(0, options.Queriers).Select(q => Task.Run(() => QueryAsync(q))).ToArray();
var sampler = Task.Run(SampleAsync);

await Task.WhenAll(writers.Concat(queriers).Append(sampler));
watch.Stop();

// Everything acknowledged should now be stored, once
Console.WriteLine("Checking what was stored...");
await Task.Delay(TimeSpan.FromSeconds(options.SettleSeconds));
var stored = await CountStoredAsync();

var report = stats.Report(watch.Elapsed, stored, options);
Console.WriteLine(report.Text);
if (options.Output != null)
{
    await File.WriteAllTextAsync(options.Output, report.Text);
}

return report.Passed ? 0 : 1;

async Task WriteAsync(int writer)
{
    var random = new Random(writer);
    var perWriter = options.Rate / (double)options.Writers;
    var interval = TimeSpan.FromSeconds(options.Batch / perWriter);
    var next = Stopwatch.GetTimestamp();
    long sequence = 0;
    var instance = writer % clients.Length;

    while (!stopping.IsCancellationRequested)
    {
        var body = new StringBuilder();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < options.Batch; i++)
        {
            var id = $"{runId}-w{writer:D3}-{sequence++:D10}";
            var status = random.Next(100) switch { < 2 => 500, < 7 => 404, _ => 200 };
            var line = $"{now:O} level={(status == 500 ? "error" : "info")} host=web-{random.Next(20):D2} status={status} duration_ms={random.Next(1, 2000)} path=/api/item/{random.Next(500)} msg=request handled";
            body.Append(JsonSerializer.Serialize(new { id, index, time = now.ToUnixTimeMilliseconds() / 1000.0, @event = line })).Append('\n');
        }

        // Retry the same batch until it's accepted, which is how a real sender behaves. The IDs
        // make a retry harmless if the first attempt got through after all.
        var content = body.ToString();
        var attempt = 0;
        while (true)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                using var request = new StringContent(content, Encoding.UTF8, "application/json");
                using var response = await clients[instance].PostAsync("/services/collector/event", request, CancellationToken.None);
                stats.Record("ingest", Stopwatch.GetElapsedTime(started), (int)response.StatusCode);
                if (response.IsSuccessStatusCode)
                {
                    stats.Acknowledged(options.Batch);
                    break;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                stats.Record("ingest", Stopwatch.GetElapsedTime(started), 0);
            }

            // Another instance may be up while this one restarts
            instance = (instance + 1) % clients.Length;
            stats.Retried();
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(5000, 100 * Math.Pow(2, attempt++))));
        }

        next += (long)(interval.TotalSeconds * Stopwatch.Frequency);
        var wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), next);
        if (wait > TimeSpan.Zero && !stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(wait, stopping.Token);
            }
            catch (TaskCanceledException)
            {
                // The run is over
            }
        }
    }
}

async Task QueryAsync(int querier)
{
    string[] queries =
    [
        "find * | head 100",
        "find * from last 15m | stats count by host",
        "find * from last 1h | timechart span=1m count by status",
        "find * from last 1h | stats avg(duration_ms) as avg_ms, p95(duration_ms) as p95_ms by path | sort - p95_ms | head 10",
        "find \"error\" from last 15m | head 50",
        "find * from last 1h | where status = 500 | stats count by host",
    ];
    var instance = querier % clients.Length;
    var random = new Random(1000 + querier);

    while (!stopping.IsCancellationRequested)
    {
        var query = queries[random.Next(queries.Length)];

        // An instance killed mid-query never answers. Ask another once, as a client behind a
        // load balancer would; the query fails only if that doesn't answer either.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                using var response = await clients[instance].PostAsJsonAsync("/api/v1/query/execute", new { query }, CancellationToken.None);
                stats.Record("query", Stopwatch.GetElapsedTime(started), (int)response.StatusCode);
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (attempt == 1 || clients.Length == 1)
                {
                    stats.Record("query", Stopwatch.GetElapsedTime(started), 0);
                    break;
                }

                stats.Retried();
                instance = (instance + 1) % clients.Length;
            }
        }

        instance = (instance + 1) % clients.Length;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(options.QueryPauseMs), stopping.Token);
        }
        catch (TaskCanceledException)
        {
            return;
        }
    }
}

async Task SampleAsync()
{
    var pattern = new Regex(@"^process_working_set_bytes (\d+)", RegexOptions.Multiline);
    while (!stopping.IsCancellationRequested)
    {
        for (var i = 0; i < clients.Length; i++)
        {
            try
            {
                var metrics = await clients[i].GetStringAsync("/metrics");
                var match = pattern.Match(metrics);
                if (match.Success)
                {
                    stats.Memory(i, watch.Elapsed, long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Down, perhaps on purpose
                continue;
            }
        }

        Console.WriteLine(stats.Progress(watch.Elapsed));
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(options.ReportSeconds), stopping.Token);
        }
        catch (TaskCanceledException)
        {
            return;
        }
    }
}

async Task<long> CountStoredAsync() => await ScalarAsync("find * | stats count", index);

async Task<long> ScalarAsync(string query, string queryIndex)
{
    for (var attempt = 0; ; attempt++)
    {
        try
        {
            using var response = await clients[attempt % clients.Length].PostAsJsonAsync("/api/v1/query/execute", new { query, index = queryIndex });
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var row = document.RootElement.GetProperty("results")[0];
            return row.EnumerateObject().First().Value.GetInt64();
        }
        catch (Exception ex) when (attempt < 10 && ex is HttpRequestException or TaskCanceledException)
        {
            await Task.Delay(2000);
        }
    }
}

internal sealed class Options
{
    public string[] Urls { get; private set; } = ["http://localhost:5000"];
    public string Key { get; private set; } = string.Empty;
    public TimeSpan Duration { get; private set; } = TimeSpan.FromMinutes(5);
    public int Rate { get; private set; } = 1000;
    public int Batch { get; private set; } = 200;
    public int Writers { get; private set; } = 4;
    public int Queriers { get; private set; } = 4;
    public int QueryPauseMs { get; private set; } = 250;
    public int ReportSeconds { get; private set; } = 30;
    public int SettleSeconds { get; private set; } = 10;
    public double MaxErrorRate { get; private set; } = 0.01;
    public double MaxMemoryGrowthMbPerHour { get; private set; } = 200;
    public string? Output { get; private set; }

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            var value = args[i + 1];
            switch (args[i])
            {
                case "--urls": options.Urls = value.Split(',', StringSplitOptions.RemoveEmptyEntries); break;
                case "--key": options.Key = value; break;
                case "--duration": options.Duration = ParseDuration(value); break;
                case "--rate": options.Rate = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--batch": options.Batch = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--writers": options.Writers = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--queriers": options.Queriers = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--query-pause-ms": options.QueryPauseMs = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--report-seconds": options.ReportSeconds = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--max-error-rate": options.MaxErrorRate = double.Parse(value, CultureInfo.InvariantCulture); break;
                case "--max-memory-growth-mb-per-hour": options.MaxMemoryGrowthMbPerHour = double.Parse(value, CultureInfo.InvariantCulture); break;
                case "--output": options.Output = value; break;
                default: throw new ArgumentException($"Unknown option {args[i]}");
            }
        }

        if (string.IsNullOrEmpty(options.Key))
        {
            throw new ArgumentException("--key is required");
        }

        return options;
    }

    private static TimeSpan ParseDuration(string value)
    {
        var amount = double.Parse(value[..^1], CultureInfo.InvariantCulture);
        return value[^1] switch
        {
            's' => TimeSpan.FromSeconds(amount),
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            'd' => TimeSpan.FromDays(amount),
            _ => throw new ArgumentException($"Durations end in s, m, h or d: {value}"),
        };
    }
}

internal sealed class Stats
{
    private readonly ConcurrentDictionary<string, ConcurrentBag<double>> _latencies = new();
    private readonly ConcurrentDictionary<(string Kind, int Status), int> _statuses = new();
    private readonly ConcurrentDictionary<int, List<(double Hours, long Bytes)>> _memory = new();
    private long _acknowledged;
    private long _retries;

    public void Record(string kind, TimeSpan elapsed, int status)
    {
        _latencies.GetOrAdd(kind, _ => new ConcurrentBag<double>()).Add(elapsed.TotalMilliseconds);
        _statuses.AddOrUpdate((kind, status), 1, (_, count) => count + 1);
    }

    public void Acknowledged(int events) => Interlocked.Add(ref _acknowledged, events);

    public void Retried() => Interlocked.Increment(ref _retries);

    public void Memory(int instance, TimeSpan at, long bytes)
    {
        var samples = _memory.GetOrAdd(instance, _ => new List<(double, long)>());
        lock (samples)
        {
            samples.Add((at.TotalHours, bytes));
        }
    }

    public string Progress(TimeSpan elapsed)
    {
        var ingested = Interlocked.Read(ref _acknowledged);
        var queries = _latencies.TryGetValue("query", out var q) ? q.Count : 0;
        var memory = string.Join(", ", _memory.OrderBy(pair => pair.Key).Select(pair =>
        {
            lock (pair.Value)
            {
                return pair.Value.Count == 0 ? "-" : $"{pair.Value[^1].Bytes / 1048576} MB";
            }
        }));
        return $"[{elapsed:hh\\:mm\\:ss}] {ingested:N0} events acknowledged ({ingested / Math.Max(1, elapsed.TotalSeconds):N0}/s), {queries:N0} queries, {Interlocked.Read(ref _retries):N0} retries, memory {memory}";
    }

    /// <summary>
    /// Storage keeps one event per ID, and writers retry with the same IDs, so exactly the
    /// acknowledged events should be stored: fewer means some were lost, more that a retry
    /// stored a batch twice.
    /// </summary>
    public (string Text, bool Passed) Report(TimeSpan elapsed, long stored, Options options)
    {
        var text = new StringBuilder();
        var acknowledged = Interlocked.Read(ref _acknowledged);
        var failures = new List<string>();

        text.AppendLine($"## Load test, {elapsed.TotalMinutes:N1} minutes, {options.Urls.Length} instance(s)");
        text.AppendLine();
        text.AppendLine("| | |");
        text.AppendLine("|---|---:|");
        text.AppendLine($"| Events acknowledged | {acknowledged:N0} |");
        text.AppendLine($"| Events stored | {stored:N0} |");
        text.AppendLine($"| Ingest rate | {acknowledged / elapsed.TotalSeconds:N0} events/s |");
        text.AppendLine($"| Requests retried on another instance | {Interlocked.Read(ref _retries):N0} |");
        text.AppendLine();

        if (stored < acknowledged)
        {
            failures.Add($"{acknowledged - stored:N0} acknowledged events are missing");
        }

        if (stored > acknowledged)
        {
            failures.Add($"{stored - acknowledged:N0} more events are stored than were sent");
        }

        text.AppendLine("| Requests | Count | Failed | p50 | p95 | p99 | Max |");
        text.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var (kind, bag) in _latencies.OrderBy(pair => pair.Key))
        {
            var sorted = bag.OrderBy(value => value).ToArray();
            var total = sorted.Length;
            var failed = _statuses.Where(pair => pair.Key.Kind == kind && (pair.Key.Status == 0 || pair.Key.Status >= 500)).Sum(pair => pair.Value);
            double Percentile(double p) => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];
            text.AppendLine($"| {kind} | {total:N0} | {failed:N0} | {Percentile(0.5):N0} ms | {Percentile(0.95):N0} ms | {Percentile(0.99):N0} ms | {(total == 0 ? 0 : sorted[^1]):N0} ms |");
            if (total > 0 && failed / (double)total > options.MaxErrorRate)
            {
                failures.Add($"{failed:N0} of {total:N0} {kind} requests failed");
            }
        }

        var statuses = string.Join(", ", _statuses.OrderBy(pair => pair.Key.Kind).ThenBy(pair => pair.Key.Status).Select(pair => $"{pair.Key.Kind} {(pair.Key.Status == 0 ? "no response" : pair.Key.Status.ToString(CultureInfo.InvariantCulture))}: {pair.Value:N0}"));
        text.AppendLine();
        text.AppendLine($"Responses: {statuses}");
        text.AppendLine();

        foreach (var (instance, samples) in _memory.OrderBy(pair => pair.Key))
        {
            List<(double Hours, long Bytes)> copy;
            lock (samples)
            {
                copy = samples.ToList();
            }

            // Ignore the first fifth, while caches and pools fill
            var steady = copy.Skip(copy.Count / 5).ToList();
            var slope = Slope(steady) / 1048576;
            text.AppendLine($"Instance {instance + 1} memory: {copy.Min(s => s.Bytes) / 1048576:N0} to {copy.Max(s => s.Bytes) / 1048576:N0} MB, trend {slope:N0} MB/hour after warm-up");
            if (steady.Count >= 10 && slope > options.MaxMemoryGrowthMbPerHour)
            {
                failures.Add($"instance {instance + 1} memory grew {slope:N0} MB/hour");
            }
        }

        text.AppendLine();
        text.AppendLine(failures.Count == 0 ? "PASS" : $"FAIL: {string.Join("; ", failures)}");
        return (text.ToString(), failures.Count == 0);
    }

    /// <summary>Least-squares slope, in bytes per hour.</summary>
    private static double Slope(List<(double Hours, long Bytes)> samples)
    {
        if (samples.Count < 2)
        {
            return 0;
        }

        var meanX = samples.Average(s => s.Hours);
        var meanY = samples.Average(s => (double)s.Bytes);
        var numerator = samples.Sum(s => (s.Hours - meanX) * (s.Bytes - meanY));
        var denominator = samples.Sum(s => (s.Hours - meanX) * (s.Hours - meanX));
        return denominator == 0 ? 0 : numerator / denominator;
    }
}
