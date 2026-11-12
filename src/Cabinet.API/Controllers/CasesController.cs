using Cabinet.API.Authentication;
using Cabinet.API.Middleware;
using Cabinet.API.Services;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace Cabinet.API.Controllers;

/// <summary>Cases: investigations that gather alerts, events, searches and notes, with a timeline.</summary>
[ApiController]
[Route("api/v1/cases")]
public sealed class CasesController : ControllerBase
{
    private readonly CaseStore _cases;
    private readonly ILogStorage _storage;
    private readonly IndexAccessScope _scope;
    private readonly IAlertHistoryStorage? _alerts;
    private readonly IAuditLogger _audit;

    public CasesController(CaseStore cases, ILogStorage storage, IndexAccessScope scope, IAuditLogger audit, IAlertHistoryStorage? alerts = null)
    {
        _cases = cases;
        _storage = storage;
        _scope = scope;
        _audit = audit;
        _alerts = alerts;
    }

    private string Me => User.FindFirst("ClientId")?.Value ?? "unknown";

    /// <summary>Every case, most recently changed first.</summary>
    [HttpGet]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(List<Case>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] CaseStatus? status = null, CancellationToken cancellationToken = default) =>
        Ok((await _cases.ListAsync(cancellationToken)).Where(c => status == null || c.Status == status));

    /// <summary>One case.</summary>
    [HttpGet("{id}")]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(Case), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken) =>
        await _cases.GetAsync(id, cancellationToken) is { } item ? Ok(item) : NotFound();

    /// <summary>Opens a case, optionally from alerts and events.</summary>
    [HttpPost]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(typeof(Case), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CaseRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 200)
        {
            return Problem(detail: "Give the case a title of up to 200 characters.", statusCode: StatusCodes.Status400BadRequest);
        }

        var item = new Case
        {
            Title = request.Title.Trim(),
            Description = request.Description,
            Severity = request.Severity ?? "Warning",
            Assignee = request.Assignee,
            CreatedBy = Me,
            AlertIds = (request.AlertIds ?? []).Distinct().Take(CaseStore.MaxItemsPerList).ToList(),
            Events = (request.EventIds ?? []).Distinct().Take(CaseStore.MaxItemsPerList).Select(e => new CaseEventRef { EventId = e, AddedBy = Me }).ToList(),
            History = [new CaseEntry { Text = "Opened the case", By = Me }],
        };
        await _cases.SaveAsync(item, cancellationToken);
        await AuditAsync(AuditAction.Create, $"Opened case {item.Title}");
        return CreatedAtAction(nameof(Get), new { id = item.Id }, item);
    }

    /// <summary>Changes a case's title, status, severity, assignee or description.</summary>
    [HttpPatch("{id}")]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(typeof(Case), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(string id, [FromBody] CaseRequest request, CancellationToken cancellationToken)
    {
        var item = await _cases.UpdateAsync(id, c =>
        {
            void Change(string what, string? from, string? to)
            {
                if (to != null && to != from)
                {
                    c.History.Add(new CaseEntry { Text = $"Changed {what} from {from ?? "nothing"} to {to}", By = Me });
                }
            }

            Change("status", c.Status.ToString(), request.Status?.ToString());
            Change("severity", c.Severity, request.Severity);
            Change("assignee", c.Assignee, request.Assignee);
            Change("title", c.Title, request.Title);
            c.Status = request.Status ?? c.Status;
            c.Severity = request.Severity ?? c.Severity;
            c.Assignee = request.Assignee ?? c.Assignee;
            c.Title = string.IsNullOrWhiteSpace(request.Title) ? c.Title : request.Title.Trim();
            c.Description = request.Description ?? c.Description;
        }, cancellationToken);
        if (item == null) return NotFound();
        await AuditAsync(AuditAction.Update, $"Changed case {item.Title}");
        return Ok(item);
    }

    /// <summary>Adds a note, events, alerts or a search to a case.</summary>
    [HttpPost("{id}/items")]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(Case), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Add(string id, [FromBody] CaseItemsRequest request, CancellationToken cancellationToken)
    {
        var item = await _cases.UpdateAsync(id, c =>
        {
            if (!string.IsNullOrWhiteSpace(request.Note) && c.Notes.Count < CaseStore.MaxItemsPerList)
            {
                c.Notes.Add(new CaseEntry { Text = request.Note.Length > 10_000 ? request.Note[..10_000] : request.Note, By = Me });
            }

            if (!string.IsNullOrWhiteSpace(request.Search) && c.Searches.Count < CaseStore.MaxItemsPerList)
            {
                c.Searches.Add(new CaseEntry { Text = request.Search, By = Me });
            }

            foreach (var eventId in (request.EventIds ?? []).Where(e => c.Events.All(existing => existing.EventId != e)))
            {
                if (c.Events.Count >= CaseStore.MaxItemsPerList) break;
                c.Events.Add(new CaseEventRef { EventId = eventId, AddedBy = Me, Note = request.Note });
            }

            foreach (var alertId in (request.AlertIds ?? []).Where(a => !c.AlertIds.Contains(a)))
            {
                if (c.AlertIds.Count >= CaseStore.MaxItemsPerList) break;
                c.AlertIds.Add(alertId);
            }
        }, cancellationToken);
        return item == null ? NotFound() : Ok(item);
    }

    /// <summary>Deletes a case.</summary>
    [HttpDelete("{id}")]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        if (await _cases.GetAsync(id, cancellationToken) is not { } item) return NotFound();
        await _cases.DeleteAsync(id, cancellationToken);
        await AuditAsync(AuditAction.Delete, $"Deleted case {item.Title}");
        return NoContent();
    }

    /// <summary>
    /// The case in time order: its events at their own times, its alerts when they fired, and its
    /// notes, searches and changes when they were made. Events the reader can't read are left
    /// out, and the rest are masked for them.
    /// </summary>
    [HttpGet("{id}/timeline")]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(List<TimelineItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Timeline(string id, CancellationToken cancellationToken)
    {
        if (await _cases.GetAsync(id, cancellationToken) is not { } item) return NotFound();

        var readable = _scope.Restrict(_storage);
        var timeline = new List<TimelineItem>();
        var hidden = 0;
        foreach (var reference in item.Events)
        {
            if (await readable.GetByIdAsync(reference.EventId, cancellationToken) is { } logEvent)
            {
                timeline.Add(new TimelineItem
                {
                    Time = logEvent.Timestamp,
                    Kind = "event",
                    Text = logEvent.RawLine.Length > 2000 ? logEvent.RawLine[..2000] : logEvent.RawLine,
                    By = reference.AddedBy,
                    Host = logEvent.Host,
                    Index = logEvent.Index,
                    Reference = logEvent.Id,
                });
            }
            else
            {
                hidden++;
            }
        }

        if (_alerts != null)
        {
            foreach (var alertId in item.AlertIds)
            {
                if (await _alerts.GetAlertByIdAsync(alertId) is { } alert && (alert.Index == null || _scope.CanRead(alert.Index)))
                {
                    timeline.Add(new TimelineItem { Time = alert.FiredAt, Kind = "alert", Text = $"{alert.SearchName}: {alert.Message}", Index = alert.Index, Reference = alert.Id });
                }
            }
        }

        timeline.AddRange(item.Notes.Select(n => new TimelineItem { Time = n.At, Kind = "note", Text = n.Text, By = n.By }));
        timeline.AddRange(item.Searches.Select(s => new TimelineItem { Time = s.At, Kind = "search", Text = s.Text, By = s.By }));
        timeline.AddRange(item.History.Select(h => new TimelineItem { Time = h.At, Kind = "change", Text = h.Text, By = h.By }));
        if (hidden > 0)
        {
            Response.Headers["X-Hidden-Events"] = hidden.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return Ok(timeline.OrderBy(t => t.Time).ToList());
    }

    private Task AuditAsync(AuditAction action, string description) =>
        _audit.LogAsync(new AuditEvent { PrincipalId = Me, Action = action, ResourceType = "Case", Description = description, SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString() });
}

/// <summary>A new case, or changes to one.</summary>
public sealed class CaseRequest
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public CaseStatus? Status { get; set; }
    public string? Severity { get; set; }
    public string? Assignee { get; set; }
    public List<string>? AlertIds { get; set; }
    public List<string>? EventIds { get; set; }
}

/// <summary>Things to add to a case.</summary>
public sealed class CaseItemsRequest
{
    public string? Note { get; set; }
    public string? Search { get; set; }
    public List<string>? EventIds { get; set; }
    public List<string>? AlertIds { get; set; }
}
