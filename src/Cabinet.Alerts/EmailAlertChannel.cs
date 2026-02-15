using System.Net;
using System.Net.Mail;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Cabinet.Alerts;

/// <summary>
/// Configuration for the email alert channel
/// </summary>
public sealed class EmailAlertConfiguration
{
    /// <summary>SMTP server hostname</summary>
    public string SmtpHost { get; set; } = "localhost";

    /// <summary>SMTP server port</summary>
    public int SmtpPort { get; set; } = 587;

    /// <summary>Enable TLS/SSL for SMTP connection</summary>
    public bool EnableTls { get; set; } = true;

    /// <summary>SMTP username (null for no authentication)</summary>
    public string? Username { get; set; }

    /// <summary>SMTP password (null for no authentication)</summary>
    public string? Password { get; set; }

    /// <summary>Sender email address</summary>
    public string FromAddress { get; set; } = "cabinet-alerts@localhost";

    /// <summary>Sender display name</summary>
    public string FromName { get; set; } = "Cabinet Alert System";

    /// <summary>Default recipient email addresses</summary>
    public List<string> ToAddresses { get; set; } = new();

    /// <summary>Connection timeout in seconds</summary>
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// SMTP email alert channel implementing IAlertChannel.
/// Sends HTML-formatted email notifications with severity color coding.
/// Handles errors gracefully (logs warnings, never throws).
/// </summary>
public sealed class EmailAlertChannel : IAlertChannel
{
    private readonly EmailAlertConfiguration _config;
    private readonly ILogger<EmailAlertChannel> _logger;

    public string Name => "Email";

    public EmailAlertChannel(
        EmailAlertConfiguration config,
        ILogger<EmailAlertChannel> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task SendAsync(Alert alert, CancellationToken cancellationToken = default)
    {
        if (_config.ToAddresses.Count == 0)
        {
            _logger.LogWarning("Email alert channel has no recipient addresses configured, skipping alert");
            return;
        }

        try
        {
            using var smtpClient = CreateSmtpClient();
            using var message = BuildMessage(alert);

            await smtpClient.SendMailAsync(message, cancellationToken);
            _logger.LogInformation(
                "Email alert sent for '{Title}' to {RecipientCount} recipients",
                alert.Title, _config.ToAddresses.Count);
        }
        catch (SmtpException ex)
        {
            _logger.LogWarning(ex, "SMTP error sending email alert for '{Title}'", alert.Title);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send email alert for '{Title}'", alert.Title);
        }
    }

    private SmtpClient CreateSmtpClient()
    {
        var client = new SmtpClient(_config.SmtpHost, _config.SmtpPort)
        {
            EnableSsl = _config.EnableTls,
            Timeout = _config.TimeoutSeconds * 1000
        };

        if (!string.IsNullOrEmpty(_config.Username))
        {
            client.Credentials = new NetworkCredential(_config.Username, _config.Password);
        }

        return client;
    }

    private MailMessage BuildMessage(Alert alert)
    {
        var from = new MailAddress(_config.FromAddress, _config.FromName);
        var message = new MailMessage
        {
            From = from,
            Subject = $"[{alert.Severity}] Cabinet Alert: {alert.Title}",
            Body = BuildHtmlBody(alert),
            IsBodyHtml = true,
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8
        };

        foreach (var address in _config.ToAddresses)
        {
            message.To.Add(new MailAddress(address));
        }

        return message;
    }

    private static string BuildHtmlBody(Alert alert)
    {
        var severityColor = alert.Severity switch
        {
            AlertSeverity.Critical => "#dc3545",
            AlertSeverity.Error => "#fd7e14",
            AlertSeverity.Warning => "#ffc107",
            _ => "#0dcaf0"
        };

        var severityBgColor = alert.Severity switch
        {
            AlertSeverity.Critical => "#f8d7da",
            AlertSeverity.Error => "#fff3cd",
            AlertSeverity.Warning => "#fff9e6",
            _ => "#d1ecf1"
        };

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html><head><meta charset='utf-8'></head>");
        sb.AppendLine("<body style='font-family: -apple-system, BlinkMacSystemFont, sans-serif; margin: 0; padding: 20px; background-color: #f5f5f5;'>");

        // Container
        sb.AppendLine("<div style='max-width: 600px; margin: 0 auto; background-color: #ffffff; border-radius: 8px; overflow: hidden; box-shadow: 0 2px 4px rgba(0,0,0,0.1);'>");

        // Header
        sb.AppendLine($"<div style='background-color: {severityColor}; color: #ffffff; padding: 16px 24px;'>");
        sb.AppendLine($"<h2 style='margin: 0; font-size: 18px;'>Cabinet Alert: {WebUtility.HtmlEncode(alert.Title)}</h2>");
        sb.AppendLine("</div>");

        // Severity badge
        sb.AppendLine($"<div style='padding: 12px 24px; background-color: {severityBgColor}; border-bottom: 1px solid #dee2e6;'>");
        sb.AppendLine($"<span style='display: inline-block; padding: 4px 12px; border-radius: 4px; background-color: {severityColor}; color: #ffffff; font-weight: bold; font-size: 12px;'>{alert.Severity}</span>");
        sb.AppendLine($"<span style='margin-left: 12px; color: #6c757d; font-size: 14px;'>{alert.Timestamp:yyyy-MM-dd HH:mm:ss} UTC</span>");
        sb.AppendLine("</div>");

        // Body
        sb.AppendLine("<div style='padding: 24px;'>");
        sb.AppendLine($"<p style='margin: 0 0 16px; font-size: 14px; line-height: 1.5; color: #212529;'>{WebUtility.HtmlEncode(alert.Message)}</p>");

        // Source
        if (!string.IsNullOrEmpty(alert.Source))
        {
            sb.AppendLine($"<p style='margin: 0 0 16px; font-size: 13px; color: #6c757d;'><strong>Source:</strong> {WebUtility.HtmlEncode(alert.Source)}</p>");
        }

        // Metadata table
        if (alert.Metadata.Count > 0)
        {
            sb.AppendLine("<table style='width: 100%; border-collapse: collapse; font-size: 13px;'>");
            sb.AppendLine("<tr><th colspan='2' style='text-align: left; padding: 8px 0; border-bottom: 2px solid #dee2e6; color: #495057;'>Details</th></tr>");

            foreach (var kvp in alert.Metadata)
            {
                sb.AppendLine("<tr>");
                sb.AppendLine($"<td style='padding: 6px 12px 6px 0; border-bottom: 1px solid #f0f0f0; color: #6c757d; width: 30%; vertical-align: top;'>{WebUtility.HtmlEncode(kvp.Key)}</td>");
                sb.AppendLine($"<td style='padding: 6px 0; border-bottom: 1px solid #f0f0f0; color: #212529;'>{WebUtility.HtmlEncode(kvp.Value?.ToString() ?? "")}</td>");
                sb.AppendLine("</tr>");
            }

            sb.AppendLine("</table>");
        }

        sb.AppendLine("</div>");

        // Footer
        sb.AppendLine("<div style='padding: 12px 24px; background-color: #f8f9fa; border-top: 1px solid #dee2e6; font-size: 12px; color: #6c757d; text-align: center;'>");
        sb.AppendLine("Sent by Cabinet Alert System");
        sb.AppendLine("</div>");

        sb.AppendLine("</div>");
        sb.AppendLine("</body></html>");

        return sb.ToString();
    }
}
