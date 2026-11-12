using System.Net.Mail;
using Cabinet.API.Middleware;
using Cabinet.API.Services;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query;
using Microsoft.AspNetCore.Mvc;

namespace Cabinet.API.Controllers;

/// <summary>Scheduled reports: searches emailed on a schedule as HTML, with a PDF copy.</summary>
[ApiController]
[Route("api/v1/reports")]
public sealed class ReportsController : ControllerBase
{
    private readonly ReportStore _reports;
    private readonly IQueryEngine _engine;
    private readonly IAuditLogger _audit;

    public ReportsController(ReportStore reports, IQueryEngine engine, IAuditLogger audit)
    {
        _reports = reports;
        _engine = engine;
        _audit = audit;
    }

    /// <summary>Every report.</summary>
    [HttpGet]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(typeof(List<Report>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) => Ok(await _reports.ListAsync(cancellationToken));

    /// <summary>Adds or changes a report.</summary>
    [HttpPut("{id}")]
    [RequireEveryIndex(Capability.Search)]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(typeof(Report), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Save(string id, [FromBody] Report report, CancellationToken cancellationToken)
    {
        if (Validate(report) is { } problem)
        {
            return Problem(detail: problem, statusCode: StatusCodes.Status400BadRequest);
        }

        var existing = await _reports.GetAsync(id, cancellationToken);
        report.Id = id;
        report.LastSentAt = existing?.LastSentAt;
        report.LastResult = existing?.LastResult;
        await _reports.SaveAsync(report, cancellationToken);
        await AuditAsync(existing == null ? AuditAction.Create : AuditAction.Update, $"Set report {report.Name}, {report.Schedule}, to {string.Join(", ", report.Recipients)}");
        return Ok(report);
    }

    /// <summary>Deletes a report.</summary>
    [HttpDelete("{id}")]
    [RequireEveryIndex(Capability.Search)]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        if (!await _reports.DeleteAsync(id, cancellationToken)) return NotFound();
        await AuditAsync(AuditAction.Delete, $"Deleted report {id}");
        return NoContent();
    }

    /// <summary>The report as it would be sent now, as HTML, run with the caller's access.</summary>
    [HttpPost("preview")]
    [RequireCapability(Capability.Search)]
    [Produces("text/html")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    public async Task<IActionResult> Preview([FromBody] Report report, CancellationToken cancellationToken)
    {
        var sections = await ReportRenderer.RunAsync(report, _engine, cancellationToken);
        return Content(ReportRenderer.Html(report, sections, DateTime.UtcNow), "text/html");
    }

    /// <summary>The report as a PDF, run with the caller's access.</summary>
    [HttpPost("pdf")]
    [RequireCapability(Capability.Search)]
    [Produces("application/pdf")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Pdf([FromBody] Report report, CancellationToken cancellationToken)
    {
        var sections = await ReportRenderer.RunAsync(report, _engine, cancellationToken);
        return File(ReportRenderer.Pdf(report, sections, DateTime.UtcNow), "application/pdf", $"{report.Name}.pdf");
    }

    /// <summary>Sends a saved report now, as the schedule would.</summary>
    [HttpPost("{id}/send")]
    [RequireEveryIndex(Capability.Search)]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(typeof(Report), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Send(string id, [FromServices] ReportService service, [FromServices] IServiceScopeFactory scopes, CancellationToken cancellationToken)
    {
        if (await _reports.GetAsync(id, cancellationToken) is not { } report) return NotFound();

        // As the schedule sends it: through an unprivileged scope, masked as for a Viewer
        using var scope = scopes.CreateScope();
        await service.SendAsync(report, scope.ServiceProvider.GetRequiredService<IQueryEngine>(), cancellationToken);
        await _reports.SaveAsync(report, cancellationToken);
        await AuditAsync(AuditAction.Export, $"Sent report {report.Name} to {string.Join(", ", report.Recipients)}");
        return Ok(report);
    }

    private static string? Validate(Report report)
    {
        if (string.IsNullOrWhiteSpace(report.Name) || report.Name.Length > 200) return "Give the report a name of up to 200 characters.";
        if (ScheduledSearchService.ValidateCron(report.Schedule) is { } cron) return cron;
        if (report.Recipients.Count is 0 or > 50) return "Send it to 1 to 50 email addresses.";
        if (report.Recipients.FirstOrDefault(r => !MailAddress.TryCreate(r, out _)) is { } bad) return $"'{bad}' isn't an email address.";
        if (report.Sections.Count is 0 or > 20) return "A report has 1 to 20 sections.";
        return report.Sections.Any(s => string.IsNullOrWhiteSpace(s.Title) || string.IsNullOrWhiteSpace(s.Query)) ? "Each section needs a title and a search." : null;
    }

    private Task AuditAsync(AuditAction action, string description) =>
        _audit.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = action,
            ResourceType = "Report",
            Description = description,
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
        });
}
