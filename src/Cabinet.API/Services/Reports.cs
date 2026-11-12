using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text;
using Cabinet.Core.Interfaces;
using Cabinet.Query;

namespace Cabinet.API.Services;

/// <summary>A report: searches run on a schedule, sent by email as HTML, with a PDF if asked.</summary>
public sealed class Report
{
    public string Id { get; set; } = Cabinet.Core.Utilities.Ulid.NewUlid();
    public required string Name { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>When it's sent, as a five-field cron expression in UTC, such as <c>0 8 * * 1</c>.</summary>
    public string Schedule { get; set; } = "0 8 * * 1";

    public List<string> Recipients { get; set; } = [];
    public List<ReportSection> Sections { get; set; } = [];

    /// <summary>Whether the email carries a PDF copy.</summary>
    public bool AttachPdf { get; set; } = true;

    public DateTime? LastSentAt { get; set; }
    public string? LastResult { get; set; }
}

/// <summary>One search in a report, shown as a table.</summary>
public sealed class ReportSection
{
    public required string Title { get; set; }
    public required string Query { get; set; }

    /// <summary>Most rows shown.</summary>
    public int MaxRows { get; set; } = 25;
}

/// <summary>A report's sections, run.</summary>
public sealed record RenderedSection(string Title, string Query, List<string> Columns, List<List<string>> Rows, long Total, string? Error);

/// <summary>Reports, kept in the shared document store.</summary>
public sealed class ReportStore
{
    private const string Collection = "reports";
    private readonly IDocumentStore _documents;

    public ReportStore(IDocumentStore documents) => _documents = documents;

    public async Task<List<Report>> ListAsync(CancellationToken cancellationToken = default) =>
        (await _documents.ListAsync<Report>(Collection, cancellationToken)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public Task<Report?> GetAsync(string id, CancellationToken cancellationToken = default) => _documents.GetAsync<Report>(Collection, id, cancellationToken);
    public Task SaveAsync(Report report, CancellationToken cancellationToken = default) => _documents.PutAsync(Collection, report.Id, report, cancellationToken);
    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) => _documents.DeleteAsync(Collection, id, cancellationToken);
}

/// <summary>Runs a report's searches and renders them as HTML and as a PDF.</summary>
public static class ReportRenderer
{
    /// <summary>Runs each section. A failing search shows its error instead of stopping the report.</summary>
    public static async Task<List<RenderedSection>> RunAsync(Report report, IQueryEngine engine, CancellationToken cancellationToken)
    {
        var sections = new List<RenderedSection>();
        foreach (var section in report.Sections)
        {
            try
            {
                var limit = Math.Clamp(section.MaxRows, 1, 500);
                var result = await engine.ExecuteAsync(new QueryExecutionContext { Query = section.Query, Limit = limit, Timeout = TimeSpan.FromMinutes(2) }, cancellationToken);
                var rows = result.Events.Cast<IDictionary<string, object?>>().ToList();
                var columns = result.Columns?.Where(c => !c.Equals("Id", StringComparison.OrdinalIgnoreCase)).ToList()
                    ?? rows.SelectMany(r => r.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                sections.Add(new RenderedSection(
                    section.Title,
                    section.Query,
                    columns,
                    rows.Take(limit).Select(r => columns.Select(c => Cell(r.TryGetValue(c, out var v) ? v : null)).ToList()).ToList(),
                    result.Statistics?.EventsMatched ?? rows.Count,
                    null));
            }
            catch (QueryParseException ex)
            {
                sections.Add(new RenderedSection(section.Title, section.Query, [], [], 0, ex.Message));
            }
        }

        return sections;
    }

    private static string Cell(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            DateTime time => time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z",
            double d => d.ToString("0.##", CultureInfo.InvariantCulture),
            IEnumerable<object?> list when value is not string => string.Join(", ", list),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    /// <summary>The report as an HTML email body. Everything from the logs is encoded.</summary>
    public static string Html(Report report, IReadOnlyList<RenderedSection> sections, DateTime at)
    {
        static string E(string text) => WebUtility.HtmlEncode(text);
        var html = new StringBuilder();
        html.Append("<!doctype html><html><body style=\"font-family:Segoe UI,Helvetica,Arial,sans-serif;color:#1f2328;max-width:960px\">");
        html.Append($"<h1 style=\"font-size:20px\">{E(report.Name)}</h1><p style=\"color:#59636e\">{at:yyyy-MM-dd HH:mm} UTC</p>");
        foreach (var section in sections)
        {
            html.Append($"<h2 style=\"font-size:16px;margin-top:24px\">{E(section.Title)}</h2>");
            html.Append($"<p style=\"font-family:monospace;font-size:12px;color:#59636e\">{E(section.Query)}</p>");
            if (section.Error != null)
            {
                html.Append($"<p style=\"color:#cf222e\">The search failed: {E(section.Error)}</p>");
                continue;
            }

            if (section.Rows.Count == 0)
            {
                html.Append("<p>No results.</p>");
                continue;
            }

            html.Append("<table style=\"border-collapse:collapse;font-size:13px\"><thead><tr>");
            foreach (var column in section.Columns)
            {
                html.Append($"<th style=\"text-align:left;border-bottom:2px solid #d1d9e0;padding:4px 8px\">{E(column)}</th>");
            }

            html.Append("</tr></thead><tbody>");
            foreach (var row in section.Rows)
            {
                html.Append("<tr>");
                foreach (var cell in row)
                {
                    html.Append($"<td style=\"border-bottom:1px solid #eff2f5;padding:4px 8px;vertical-align:top\">{E(cell)}</td>");
                }

                html.Append("</tr>");
            }

            html.Append("</tbody></table>");
            if (section.Total > section.Rows.Count)
            {
                html.Append($"<p style=\"color:#59636e;font-size:12px\">The first {section.Rows.Count} of {section.Total} rows.</p>");
            }
        }

        html.Append("<p style=\"color:#59636e;font-size:12px;margin-top:32px\">Sent by Cabinet. Values are masked as for a Viewer.</p></body></html>");
        return html.ToString();
    }

    /// <summary>
    /// The report as a plain PDF: each section's rows as fixed-width text. Written directly, so
    /// reports need no PDF library; characters outside Latin-1 show as '?'.
    /// </summary>
    public static byte[] Pdf(Report report, IReadOnlyList<RenderedSection> sections, DateTime at)
    {
        var lines = new List<(string Text, bool Heading)>
        {
            (report.Name, true),
            ($"{at:yyyy-MM-dd HH:mm} UTC", false),
            (string.Empty, false),
        };
        foreach (var section in sections)
        {
            lines.Add((section.Title, true));
            lines.Add((section.Query, false));
            if (section.Error != null)
            {
                lines.Add(($"The search failed: {section.Error}", false));
            }
            else if (section.Rows.Count == 0)
            {
                lines.Add(("No results.", false));
            }
            else
            {
                var widths = section.Columns.Select((c, i) => Math.Min(40, Math.Max(c.Length, section.Rows.Max(r => r[i].Length)))).ToList();
                string Row(IReadOnlyList<string> cells) => string.Join("  ", cells.Select((cell, i) => (cell.Length > widths[i] ? cell[..(widths[i] - 1)] + "~" : cell).PadRight(widths[i])));
                lines.Add((Row(section.Columns), false));
                lines.Add((new string('-', Math.Min(110, widths.Sum() + 2 * (widths.Count - 1))), false));
                lines.AddRange(section.Rows.Select(r => (Row(r), false)));
            }

            lines.Add((string.Empty, false));
        }

        // Long lines wrap rather than run off the page
        var wrapped = lines.SelectMany(l => Wrap(l.Text, l.Heading ? 70 : 110).Select(t => (t, l.Heading))).ToList();
        const int LinesPerPage = 64;
        var pages = wrapped.Chunk(LinesPerPage).ToList();
        if (pages.Count == 0) pages.Add([]);

        var objects = new List<string>();
        var pageIds = new List<int>();

        // 1 catalog, 2 pages, 3 Courier, 4 Courier-Bold, then a content stream and a page each
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add(string.Empty);
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Courier-Bold /Encoding /WinAnsiEncoding >>");
        foreach (var page in pages)
        {
            var content = new StringBuilder("BT 11 TL 36 806 Td\n");
            foreach (var (text, heading) in page)
            {
                content.Append(heading ? "/F2 10 Tf " : "/F1 7 Tf ").Append('(').Append(PdfText(text)).Append(") Tj T*\n");
            }

            content.Append("ET");
            var stream = Encoding.Latin1.GetBytes(content.ToString());
            objects.Add($"<< /Length {stream.Length} >>\nstream\n{content}\nendstream");
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {objects.Count} 0 R >>");
            pageIds.Add(objects.Count);
        }

        objects[1] = $"<< /Type /Pages /Kids [{string.Join(' ', pageIds.Select(id => $"{id} 0 R"))}] /Count {pageIds.Count} >>";

        var pdf = new MemoryStream();
        void Write(string s) { var bytes = Encoding.Latin1.GetBytes(s); pdf.Write(bytes); }
        Write("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Position);
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            Write($"{offset:D10} 00000 n \n");
        }

        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        if (text.Length <= width)
        {
            yield return text;
            yield break;
        }

        for (var i = 0; i < text.Length; i += width)
        {
            yield return text.Substring(i, Math.Min(width, text.Length - i));
        }
    }

    private static string PdfText(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            escaped.Append(c switch
            {
                '(' => "\\(",
                ')' => "\\)",
                '\\' => "\\\\",
                _ when c < 32 => " ",
                _ when c > 255 => "?",
                _ => c.ToString(),
            });
        }

        return escaped.ToString();
    }
}

/// <summary>Sends reports when they're due, on the leader only.</summary>
public sealed class ReportService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _configuration;
    private readonly ILeaderLease _lease;
    private readonly ILogger<ReportService> _logger;

    public ReportService(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<ReportService> logger, ILeaderLease? lease = null)
    {
        _scopes = scopes;
        _configuration = configuration;
        _logger = logger;
        _lease = lease ?? new SingleInstanceLease();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
            try
            {
                if (_lease.IsLeader)
                {
                    using var scope = _scopes.CreateScope();
                    var store = scope.ServiceProvider.GetRequiredService<ReportStore>();
                    foreach (var report in (await store.ListAsync(stoppingToken)).Where(r => r.Enabled && ScheduledSearchService.MatchesCron(r.Schedule, minute)))
                    {
                        // Sent once per scheduled minute, even if this loop comes round twice in it
                        if (report.LastSentAt is { } last && last >= minute) continue;
                        await SendAsync(report, scope.ServiceProvider.GetRequiredService<IQueryEngine>(), stoppingToken);
                        await store.SaveAsync(report, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sending scheduled reports failed");
            }

            try
            {
                await Task.Delay(minute.AddMinutes(1) - DateTime.UtcNow + TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Runs and emails a report, recording how it went on it.</summary>
    public async Task SendAsync(Report report, IQueryEngine engine, CancellationToken cancellationToken)
    {
        var at = DateTime.UtcNow;
        try
        {
            var sections = await ReportRenderer.RunAsync(report, engine, cancellationToken);
            using var message = new MailMessage
            {
                From = new MailAddress(_configuration["Email:FromAddress"] ?? "cabinet-alerts@localhost", _configuration["Email:FromName"] ?? "Cabinet"),
                Subject = $"{report.Name} ({at:yyyy-MM-dd})",
                Body = ReportRenderer.Html(report, sections, at),
                IsBodyHtml = true,
            };
            foreach (var recipient in report.Recipients)
            {
                message.To.Add(recipient);
            }

            if (report.AttachPdf)
            {
                message.Attachments.Add(new Attachment(new MemoryStream(ReportRenderer.Pdf(report, sections, at)), $"{report.Name}-{at:yyyy-MM-dd}.pdf", "application/pdf"));
            }

            using var client = new SmtpClient(_configuration["Email:SmtpHost"] ?? "localhost", _configuration.GetValue("Email:SmtpPort", 587))
            {
                EnableSsl = _configuration.GetValue("Email:EnableTls", true),
                Timeout = _configuration.GetValue("Email:TimeoutSeconds", 30) * 1000,
            };
            if (_configuration["Email:Username"] is { Length: > 0 } user)
            {
                client.Credentials = new NetworkCredential(user, _configuration["Email:Password"]);
            }

            await client.SendMailAsync(message, cancellationToken);
            report.LastResult = $"Sent to {report.Recipients.Count} recipient(s)";
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException or FormatException or IOException)
        {
            report.LastResult = $"Not sent: {ex.Message}";
            _logger.LogWarning(ex, "Report {Name} wasn't sent", report.Name);
        }

        report.LastSentAt = at;
    }
}
