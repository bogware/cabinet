using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text;
using Cabinet.Core.Interfaces;

namespace Cabinet.API.Services;

/// <summary>What a connector sends: a firing or scheduled-search alert, in a neutral shape.</summary>
public sealed class ConnectorNotification
{
    public required string Title { get; init; }
    public required string Message { get; init; }
    public string Severity { get; init; } = "Warning";
    public string Source { get; init; } = "Cabinet";
    public DateTime Time { get; init; } = DateTime.UtcNow;
    public Dictionary<string, string> Facts { get; init; } = new();

    /// <summary>Groups repeats of the same alert in services that deduplicate, such as PagerDuty.</summary>
    public string? DedupKey { get; init; }
}

/// <summary>
/// A named destination for alerts, such as a Slack channel, a PagerDuty service or a Jira project.
/// Rules and scheduled searches refer to it by name, so its credentials live in one place and are
/// never shown back: anyone who can read rules sees the connector's name, not its token.
/// </summary>
public sealed class ActionConnector
{
    /// <summary>Unique name rules refer to, such as <c>oncall-pagerduty</c>.</summary>
    public required string Name { get; set; }

    /// <summary>webhook, slack, teams, discord, pagerduty, opsgenie, jira, servicenow or email.</summary>
    public required string Type { get; set; }

    /// <summary>Endpoint: the webhook URL, Jira or ServiceNow base URL. Optional for PagerDuty, Opsgenie and email.</summary>
    public string? Url { get; set; }

    /// <summary>Token, routing key, API key or password. Write-only: never returned by the API.</summary>
    public string? Secret { get; set; }

    /// <summary>
    /// Type-specific settings: <c>user</c> (Jira, ServiceNow), <c>project</c> and <c>issueType</c> (Jira),
    /// <c>table</c> (ServiceNow, default incident), <c>to</c> (email, comma-separated).
    /// </summary>
    public Dictionary<string, string> Settings { get; set; } = new();

    public bool Enabled { get; set; } = true;

    /// <summary>Set by the API when reading, instead of the secret itself.</summary>
    public bool HasSecret { get; set; }

    public static readonly string[] Types = ["webhook", "slack", "teams", "discord", "pagerduty", "opsgenie", "jira", "servicenow", "email"];
}

/// <summary>Sends a notification through a named connector.</summary>
public interface IActionConnectorRunner
{
    Task SendAsync(string connectorName, ConnectorNotification notification, CancellationToken cancellationToken = default);
}

/// <summary>Keeps connectors in the document store, one per name.</summary>
public sealed class ConnectorStore
{
    private const string Collection = "action-connectors";
    private readonly IDocumentStore _documents;

    public ConnectorStore(IDocumentStore documents)
    {
        _documents = documents;
    }

    public async Task<List<ActionConnector>> ListAsync(CancellationToken cancellationToken = default) =>
        (await _documents.ListAsync<ActionConnector>(Collection, cancellationToken)).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public Task<ActionConnector?> GetAsync(string name, CancellationToken cancellationToken = default) =>
        _documents.GetAsync<ActionConnector>(Collection, Key(name), cancellationToken);

    public Task SaveAsync(ActionConnector connector, CancellationToken cancellationToken = default) =>
        _documents.PutAsync(Collection, Key(connector.Name), connector, cancellationToken);

    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) =>
        _documents.DeleteAsync(Collection, Key(name), cancellationToken);

    private static string Key(string name) => name.Trim().ToLowerInvariant();
}

/// <summary>Builds each service's payload and delivers it through the SSRF-guarded client.</summary>
public sealed class ConnectorRunner : IActionConnectorRunner
{
    private readonly ConnectorStore _store;
    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConnectorRunner> _logger;

    public ConnectorRunner(ConnectorStore store, HttpClient http, IConfiguration configuration, ILogger<ConnectorRunner> logger)
    {
        _store = store;
        _http = http;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendAsync(string connectorName, ConnectorNotification notification, CancellationToken cancellationToken = default)
    {
        var connector = await _store.GetAsync(connectorName, cancellationToken);
        if (connector == null)
        {
            _logger.LogWarning("No connector named {Connector}; the alert wasn't sent there", connectorName);
            return;
        }

        if (!connector.Enabled)
        {
            return;
        }

        await SendAsync(connector, notification, cancellationToken);
    }

    /// <summary>Sends through a connector that may not be saved yet, for the test button.</summary>
    public async Task SendAsync(ActionConnector connector, ConnectorNotification notification, CancellationToken cancellationToken = default)
    {
        if (connector.Type.Equals("email", StringComparison.OrdinalIgnoreCase))
        {
            await SendEmailAsync(connector, notification, cancellationToken);
            return;
        }

        using var request = BuildRequest(connector, notification);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"{connector.Type} connector '{connector.Name}' returned {(int)response.StatusCode}: {body[..Math.Min(300, body.Length)]}");
        }
    }

    /// <summary>The HTTP request a connector sends. Public for tests.</summary>
    public static HttpRequestMessage BuildRequest(ActionConnector connector, ConnectorNotification n)
    {
        var type = connector.Type.ToLowerInvariant();
        var setting = (string key, string fallback) => connector.Settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
        var text = $"{n.Title}\n{n.Message}";
        var facts = string.Join("\n", n.Facts.Where(f => !string.IsNullOrEmpty(f.Value)).Select(f => $"{f.Key}: {f.Value}"));

        HttpRequestMessage Post(string url, object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            return request;
        }

        HttpRequestMessage WithBasic(HttpRequestMessage request)
        {
            var user = setting("user", string.Empty);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{connector.Secret}")));
            return request;
        }

        string Required(string? url) => string.IsNullOrWhiteSpace(url) ? throw new InvalidOperationException($"Connector '{connector.Name}' needs a URL") : url.TrimEnd('/');

        switch (type)
        {
            case "slack":
                return Post(Required(connector.Url), new
                {
                    text = $"*{Emoji(n.Severity)} {n.Title}*",
                    blocks = new object[]
                    {
                        new { type = "header", text = new { type = "plain_text", text = Truncate(n.Title, 150) } },
                        new { type = "section", text = new { type = "mrkdwn", text = Truncate(n.Message, 2900) } },
                        new { type = "context", elements = new object[] { new { type = "mrkdwn", text = Truncate($"*{n.Severity}* · {facts.Replace("\n", " · ")}", 2900) } } },
                    },
                });

            case "discord":
                return Post(Required(connector.Url), new
                {
                    embeds = new object[]
                    {
                        new { title = Truncate(n.Title, 256), description = Truncate(n.Message, 4000), color = Color(n.Severity), timestamp = n.Time.ToString("o", CultureInfo.InvariantCulture),
                              fields = n.Facts.Where(f => !string.IsNullOrEmpty(f.Value)).Take(20).Select(f => new { name = f.Key, value = Truncate(f.Value, 1000), inline = true }).ToArray() },
                    },
                });

            case "teams":
                return Post(Required(connector.Url), new Dictionary<string, object>
                {
                    ["@type"] = "MessageCard",
                    ["@context"] = "https://schema.org/extensions",
                    ["themeColor"] = Color(n.Severity).ToString("X6", CultureInfo.InvariantCulture),
                    ["summary"] = n.Title,
                    ["title"] = n.Title,
                    ["text"] = n.Message,
                    ["sections"] = new object[] { new { facts = n.Facts.Where(f => !string.IsNullOrEmpty(f.Value)).Select(f => new { name = f.Key, value = f.Value }).ToArray() } },
                });

            case "pagerduty":
                return Post(string.IsNullOrWhiteSpace(connector.Url) ? "https://events.pagerduty.com/v2/enqueue" : connector.Url, new
                {
                    routing_key = connector.Secret,
                    event_action = "trigger",
                    dedup_key = n.DedupKey,
                    payload = new
                    {
                        summary = Truncate($"{n.Title}: {n.Message}", 1024),
                        severity = n.Severity.ToLowerInvariant() switch { "critical" => "critical", "error" => "error", "info" => "info", _ => "warning" },
                        source = n.Source,
                        timestamp = n.Time.ToString("o", CultureInfo.InvariantCulture),
                        custom_details = n.Facts,
                    },
                });

            case "opsgenie":
                {
                    var request = Post(string.IsNullOrWhiteSpace(connector.Url) ? "https://api.opsgenie.com/v2/alerts" : connector.Url, new
                    {
                        message = Truncate(n.Title, 130),
                        description = Truncate($"{n.Message}\n\n{facts}", 15000),
                        alias = n.DedupKey,
                        priority = n.Severity.ToLowerInvariant() switch { "critical" => "P1", "error" => "P2", "warning" => "P3", _ => "P5" },
                        source = n.Source,
                        details = n.Facts,
                    });
                    request.Headers.TryAddWithoutValidation("Authorization", $"GenieKey {connector.Secret}");
                    return request;
                }

            case "jira":
                return WithBasic(Post($"{Required(connector.Url)}/rest/api/2/issue", new
                {
                    fields = new Dictionary<string, object>
                    {
                        ["project"] = new { key = setting("project", "OPS") },
                        ["summary"] = Truncate(n.Title, 250),
                        ["description"] = $"{n.Message}\n\n{facts}",
                        ["issuetype"] = new { name = setting("issueType", "Task") },
                    },
                }));

            case "servicenow":
                return WithBasic(Post($"{Required(connector.Url)}/api/now/table/{setting("table", "incident")}", new
                {
                    short_description = Truncate(n.Title, 160),
                    description = $"{n.Message}\n\n{facts}",
                    urgency = n.Severity.ToLowerInvariant() switch { "critical" => "1", "error" => "2", _ => "3" },
                    correlation_id = n.DedupKey,
                }));

            default:
                {
                    var request = Post(Required(connector.Url), new
                    {
                        title = n.Title,
                        message = n.Message,
                        severity = n.Severity,
                        source = n.Source,
                        time = n.Time,
                        facts = n.Facts,
                        dedup_key = n.DedupKey,
                    });
                    if (!string.IsNullOrEmpty(connector.Secret))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connector.Secret);
                    }

                    return request;
                }
        }
    }

    private async Task SendEmailAsync(ActionConnector connector, ConnectorNotification n, CancellationToken cancellationToken)
    {
        var to = (connector.Settings.TryGetValue("to", out var list) ? list : string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (to.Length == 0)
        {
            throw new InvalidOperationException($"Email connector '{connector.Name}' has no 'to' addresses");
        }

        var c = _configuration;
        using var client = new SmtpClient(c["Email:SmtpHost"] ?? "localhost", c.GetValue("Email:SmtpPort", 587))
        {
            EnableSsl = c.GetValue("Email:EnableTls", true),
            Timeout = c.GetValue("Email:TimeoutSeconds", 30) * 1000,
        };
        var user = c["Email:Username"];
        if (!string.IsNullOrEmpty(user))
        {
            client.Credentials = new System.Net.NetworkCredential(user, c["Email:Password"]);
        }

        using var message = new MailMessage
        {
            From = new MailAddress(c["Email:FromAddress"] ?? "cabinet-alerts@localhost", c["Email:FromName"] ?? "Cabinet"),
            Subject = $"[{n.Severity}] {n.Title}",
            Body = $"{n.Message}\n\n{string.Join("\n", n.Facts.Select(f => $"{f.Key}: {f.Value}"))}\n\nSent by Cabinet at {n.Time:u}",
        };
        foreach (var address in to)
        {
            message.To.Add(address);
        }

        await client.SendMailAsync(message, cancellationToken);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    private static string Emoji(string severity) => severity.ToLowerInvariant() switch
    {
        "critical" => ":rotating_light:",
        "error" => ":red_circle:",
        "warning" => ":warning:",
        _ => ":information_source:",
    };

    private static int Color(string severity) => severity.ToLowerInvariant() switch
    {
        "critical" => 0xB00020,
        "error" => 0xE5484D,
        "warning" => 0xF5A623,
        _ => 0x3B82F6,
    };
}
