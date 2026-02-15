using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cabinet.Alerts;

/// <summary>
/// Webhook alert channel for sending alerts to external services
/// </summary>
public class WebhookAlertChannel : IAlertChannel
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WebhookAlertChannel> _logger;
    private readonly WebhookConfiguration _config;

    public string Name => "Webhook";

    public WebhookAlertChannel(
        HttpClient httpClient,
        WebhookConfiguration config,
        ILogger<WebhookAlertChannel> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    public async Task SendAsync(Alert alert, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_config.Url))
        {
            _logger.LogWarning("Webhook URL not configured, skipping alert");
            return;
        }

        var maxRetries = Math.Clamp(_config.RetryCount, 0, 5);

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                var payload = BuildPayload(alert);
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Add custom headers if configured (validate to prevent header injection)
                if (_config.Headers != null)
                {
                    foreach (var header in _config.Headers)
                    {
                        // Reject headers containing newline characters to prevent HTTP header injection
                        if (header.Key.AsSpan().IndexOfAny('\r', '\n') >= 0 ||
                            header.Value.AsSpan().IndexOfAny('\r', '\n') >= 0)
                        {
                            _logger.LogWarning("Skipping webhook header with newline characters: {Key}", header.Key);
                            continue;
                        }
                        content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }

                // Use HttpRequestMessage to set per-request auth (avoids race condition on shared HttpClient)
                using var request = new HttpRequestMessage(HttpMethod.Post, _config.Url) { Content = content };
                if (!string.IsNullOrEmpty(_config.AuthorizationHeader))
                {
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue(
                            _config.AuthorizationType ?? "Bearer",
                            _config.AuthorizationHeader);
                }

                // Apply configurable timeout per request
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_config.TimeoutSeconds, 5, 120)));

                var response = await _httpClient.SendAsync(request, timeoutCts.Token);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Alert sent successfully to webhook: {Url} (attempt {Attempt})", _config.Url, attempt + 1);
                    return; // Success — no retry needed
                }

                // Don't retry client errors (4xx) — only server errors (5xx) are retriable
                if ((int)response.StatusCode < 500)
                {
                    _logger.LogWarning("Webhook returned non-retriable error. Status: {Status}, Reason: {Reason}",
                        response.StatusCode, response.ReasonPhrase);
                    return;
                }

                _logger.LogWarning("Webhook returned server error (attempt {Attempt}/{MaxRetries}). Status: {Status}",
                    attempt + 1, maxRetries + 1, response.StatusCode);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Caller cancelled — don't retry
                _logger.LogWarning("Webhook send cancelled for alert '{Title}'", alert.Title);
                return;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                _logger.LogWarning(ex, "Webhook attempt {Attempt}/{MaxRetries} failed, retrying...",
                    attempt + 1, maxRetries + 1);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "All webhook retry attempts exhausted for alert '{Title}'", alert.Title);
                return;
            }

            // Exponential backoff: 1s, 2s, 4s, 8s, 16s
            if (attempt < maxRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private object BuildPayload(Alert alert)
    {
        // Support different webhook formats
        return _config.Format?.ToLower() switch
        {
            "slack" => BuildSlackPayload(alert),
            "discord" => BuildDiscordPayload(alert),
            "teams" => BuildTeamsPayload(alert),
            "pagerduty" => BuildPagerDutyPayload(alert),
            _ => BuildGenericPayload(alert)
        };
    }

    private object BuildGenericPayload(Alert alert)
    {
        return new
        {
            id = alert.Id,
            timestamp = alert.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
            severity = alert.Severity.ToString(),
            title = alert.Title,
            message = alert.Message,
            source = alert.Source,
            metadata = alert.Metadata
        };
    }

    private object BuildSlackPayload(Alert alert)
    {
        var color = alert.Severity switch
        {
            AlertSeverity.Critical => "danger",
            AlertSeverity.Error => "warning",
            AlertSeverity.Warning => "#ff9900",
            _ => "good"
        };

        return new
        {
            text = $"*{alert.Title}*",
            attachments = new[]
            {
                new
                {
                    color,
                    title = alert.Title,
                    text = alert.Message,
                    fields = alert.Metadata.Select(kvp => new
                    {
                        title = kvp.Key,
                        value = kvp.Value?.ToString() ?? "",
                        @short = true
                    }).ToArray(),
                    footer = "Cabinet Alert System",
                    ts = new DateTimeOffset(alert.Timestamp).ToUnixTimeSeconds()
                }
            }
        };
    }

    private object BuildDiscordPayload(Alert alert)
    {
        var color = alert.Severity switch
        {
            AlertSeverity.Critical => 15158332, // Red
            AlertSeverity.Error => 15105570,    // Orange
            AlertSeverity.Warning => 15844367,  // Yellow
            _ => 3066993                        // Green
        };

        return new
        {
            content = $"**Alert: {alert.Title}**",
            embeds = new[]
            {
                new
                {
                    title = alert.Title,
                    description = alert.Message,
                    color,
                    fields = alert.Metadata.Select(kvp => new
                    {
                        name = kvp.Key,
                        value = kvp.Value?.ToString() ?? "",
                        inline = true
                    }).ToArray(),
                    footer = new
                    {
                        text = "Cabinet Alert System"
                    },
                    timestamp = alert.Timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK")
                }
            }
        };
    }

    private object BuildTeamsPayload(Alert alert)
    {
        var themeColor = alert.Severity switch
        {
            AlertSeverity.Critical => "FF0000",
            AlertSeverity.Error => "FFA500",
            AlertSeverity.Warning => "FFFF00",
            _ => "00FF00"
        };

        return new
        {
            @type = "MessageCard",
            @context = "https://schema.org/extensions",
            themeColor,
            summary = alert.Title,
            sections = new[]
            {
                new
                {
                    activityTitle = alert.Title,
                    activitySubtitle = $"Severity: {alert.Severity}",
                    activityImage = GetSeverityIcon(alert.Severity),
                    text = alert.Message,
                    facts = alert.Metadata.Select(kvp => new
                    {
                        name = kvp.Key,
                        value = kvp.Value?.ToString() ?? ""
                    }).ToArray()
                }
            }
        };
    }

    private object BuildPagerDutyPayload(Alert alert)
    {
        var severity = alert.Severity switch
        {
            AlertSeverity.Critical => "critical",
            AlertSeverity.Error => "error",
            AlertSeverity.Warning => "warning",
            _ => "info"
        };

        return new
        {
            routing_key = _config.PagerDutyRoutingKey,
            event_action = "trigger",
            payload = new
            {
                summary = alert.Title,
                source = alert.Source,
                severity,
                timestamp = alert.Timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK"),
                custom_details = alert.Metadata
            }
        };
    }

    private string GetSeverityIcon(AlertSeverity severity)
    {
        return severity switch
        {
            AlertSeverity.Critical => "🔴",
            AlertSeverity.Error => "🟠",
            AlertSeverity.Warning => "🟡",
            _ => "🟢"
        };
    }
}

public class WebhookConfiguration
{
    public string? Url { get; set; }
    public string? Format { get; set; } = "generic"; // generic, slack, discord, teams, pagerduty
    public string? AuthorizationType { get; set; }
    public string? AuthorizationHeader { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public string? PagerDutyRoutingKey { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public int RetryCount { get; set; } = 3;
}