using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cabinet.Agent.Configuration;
using Cabinet.Agent.Forwarding;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cabinet.Agent.Monitoring;

/// <summary>
/// A line from a Kubernetes container log file: CRI (containerd, CRI-O) writes
/// <c>2026-03-04T05:06:07.123456789Z stdout F message</c>, Docker writes a JSON object.
/// </summary>
public sealed record KubernetesLogLine(string Message, DateTime? Time, string Stream)
{
    private static readonly Regex Cri = new(@"^(?<time>\S+) (?<stream>stdout|stderr) (?<tag>[FP])(?: (?<message>.*))?$", RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds(1));

    // <pod>_<namespace>_<container>-<64 hex container ID>.log, as the kubelet names its links
    private static readonly Regex FileName = new(@"^(?<pod>[^_]+)_(?<namespace>[^_]+)_(?<container>.+)-(?<id>[0-9a-f]{64})\.log$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Unwraps a line; one in neither format is the message itself.</summary>
    public static KubernetesLogLine Parse(string line)
    {
        if (line.StartsWith('{'))
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (root.TryGetProperty("log", out var log) && log.ValueKind == JsonValueKind.String)
                {
                    return new KubernetesLogLine(
                        log.GetString()!.TrimEnd('\n', '\r'),
                        root.TryGetProperty("time", out var time) && time.TryGetDateTime(out var at) ? at.ToUniversalTime() : null,
                        root.TryGetProperty("stream", out var stream) ? stream.GetString() ?? "stdout" : "stdout");
                }
            }
            catch (JsonException)
            {
                // Not Docker's format: a JSON line the container itself wrote
                return new KubernetesLogLine(line, null, "stdout");
            }
        }

        if (Cri.Match(line) is { Success: true } cri)
        {
            var parsed = DateTime.TryParse(cri.Groups["time"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when);
            return new KubernetesLogLine(cri.Groups["message"].Value, parsed ? when : null, cri.Groups["stream"].Value);
        }

        return new KubernetesLogLine(line, null, "stdout");
    }

    /// <summary>The pod, namespace and container a log file belongs to, from its name.</summary>
    public static Dictionary<string, string> PodFields(string path)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (FileName.Match(Path.GetFileName(path)) is { Success: true } name)
        {
            fields["k8s.pod"] = name.Groups["pod"].Value;
            fields["k8s.namespace"] = name.Groups["namespace"].Value;
            fields["k8s.container"] = name.Groups["container"].Value;
            fields["k8s.container_id"] = name.Groups["id"].Value[..12];
        }

        return fields;
    }
}

/// <summary>
/// Where inputs other than files resume: a journal cursor, an event log record number. Saved as
/// soon as a batch is durable, and only then.
/// </summary>
public sealed class InputCheckpoints
{
    private readonly string _path;
    private readonly ILogger<InputCheckpoints> _logger;
    private readonly Dictionary<string, string> _values;
    private readonly object _lock = new();

    public InputCheckpoints(string path, ILogger<InputCheckpoints> logger)
    {
        _path = path;
        _logger = logger;
        try
        {
            _values = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "The checkpoint file {Path} is unreadable; inputs start over from now", path);
            _values = [];
        }
    }

    public string? Get(string key)
    {
        lock (_lock)
        {
            return _values.GetValueOrDefault(key);
        }
    }

    public void Set(IReadOnlyDictionary<string, string> changes)
    {
        if (changes.Count == 0)
        {
            return;
        }

        lock (_lock)
        {
            foreach (var (key, value) in changes)
            {
                _values[key] = value;
            }

            // Written whole and renamed into place, so a crash leaves the old file or the new one
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_values));
            File.Move(temporary, _path, overwrite: true);
        }
    }
}

/// <summary>Reads the systemd journal through <c>journalctl -o json --follow</c>, resuming at its cursor.</summary>
public sealed class JournaldInput : BackgroundService
{
    private readonly InputConfiguration _input;
    private readonly AgentConfiguration _config;
    private readonly LogForwarder _forwarder;
    private readonly InputCheckpoints _checkpoints;
    private readonly ILogger<JournaldInput> _logger;
    private readonly string _key;

    public JournaldInput(InputConfiguration input, AgentConfiguration config, LogForwarder forwarder, InputCheckpoints checkpoints, ILogger<JournaldInput> logger)
    {
        _input = input;
        _config = config;
        _forwarder = forwarder;
        _checkpoints = checkpoints;
        _logger = logger;
        _key = $"journald:{string.Join(',', input.Units.Order(StringComparer.Ordinal))}";
        _forwarder.BatchDurable += OnBatchDurable;
    }

    /// <summary>journalctl's arguments: follow, as JSON, from the cursor or from now.</summary>
    public static List<string> Arguments(string? cursor, IEnumerable<string> units, bool fromBeginning)
    {
        var arguments = new List<string> { "--output=json", "--follow", "--no-pager" };
        if (cursor != null)
        {
            arguments.Add($"--after-cursor={cursor}");
        }
        else if (!fromBeginning)
        {
            arguments.Add("--lines=0");
        }

        foreach (var unit in units)
        {
            arguments.Add($"--unit={unit}");
        }

        return arguments;
    }

    /// <summary>An event from one line of journalctl's JSON, or null for a line that isn't one.</summary>
    public static LogEventDto? ToEvent(string line, InputConfiguration input, string host, string key)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var value) ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),

                // Binary messages come as a list of bytes
                JsonValueKind.Array => Encoding.UTF8.GetString(value.EnumerateArray().Where(b => b.ValueKind == JsonValueKind.Number).Select(b => (byte)b.GetInt32()).ToArray()),
                _ => value.GetRawText(),
            } : null;

            var cursor = Text("__CURSOR");
            if (cursor == null)
            {
                return null;
            }

            var time = long.TryParse(Text("__REALTIME_TIMESTAMP"), out var micros) ? DateTimeOffset.FromUnixTimeMilliseconds(micros / 1000).UtcDateTime : DateTime.UtcNow;
            var identifier = Text("SYSLOG_IDENTIFIER") ?? Text("_COMM") ?? "journal";
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (field, name) in new[] { ("_SYSTEMD_UNIT", "unit"), ("SYSLOG_IDENTIFIER", "identifier"), ("_PID", "pid"), ("PRIORITY", "priority"), ("_UID", "uid") })
            {
                if (Text(field) is { } value)
                {
                    fields[name] = value;
                }
            }

            return new LogEventDto
            {
                Id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"journald:{host}:{cursor}")).AsSpan(0, 16)),
                InputKey = key,
                Checkpoint = cursor,
                Timestamp = time,
                Host = Text("_HOSTNAME") ?? host,
                Source = $"journald:{Text("_SYSTEMD_UNIT") ?? identifier}",
                SourceType = input.SourceType == "generic" ? "journald" : input.SourceType,
                Index = input.Index,

                // As syslog would show it, so the same parsers and searches work
                Event = $"{time:yyyy-MM-ddTHH:mm:ss.fffZ} {Text("_HOSTNAME") ?? host} {identifier}{(Text("_PID") is { } pid ? $"[{pid}]" : string.Empty)}: {Text("MESSAGE") ?? string.Empty}",
                Fields = fields,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void OnBatchDurable(IReadOnlyList<LogEventDto> batch)
    {
        if (batch.LastOrDefault(e => e.InputKey == _key) is { Checkpoint: { } cursor })
        {
            _checkpoints.Set(new Dictionary<string, string> { [_key] = cursor });
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var host = string.IsNullOrWhiteSpace(_config.Hostname) ? Environment.MachineName : _config.Hostname;
        while (!stoppingToken.IsCancellationRequested)
        {
            var start = new ProcessStartInfo("journalctl") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            Arguments(_checkpoints.Get(_key), _input.Units, _input.FromBeginning).ForEach(start.ArgumentList.Add);
            try
            {
                using var process = Process.Start(start) ?? throw new InvalidOperationException("journalctl didn't start");
                using var stop = stoppingToken.Register(() =>
                {
                    try { process.Kill(); }
                    catch (InvalidOperationException) { _logger.LogDebug("journalctl had already exited"); }
                });

                while (await process.StandardOutput.ReadLineAsync(stoppingToken) is { } line)
                {
                    if (ToEvent(line, _input, host, _key) is { } evt)
                    {
                        // Waits when the queue is full, which is what pauses reading
                        await _forwarder.EnqueueAsync(evt, stoppingToken);
                    }
                }

                if (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning("journalctl stopped ({Error}); starting it again", (await process.StandardError.ReadToEndAsync(stoppingToken)).Trim());
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                _logger.LogError(ex, "Couldn't run journalctl; trying again in 30 seconds");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public override void Dispose()
    {
        _forwarder.BatchDurable -= OnBatchDurable;
        base.Dispose();
    }
}
