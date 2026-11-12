using Cabinet.API.Middleware;
using Cabinet.API.Services;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query;
using Microsoft.AspNetCore.Mvc;

namespace Cabinet.API.Controllers;

/// <summary>Rules that learn what's normal and alert on what isn't: spikes, silence, new values, new kinds of line.</summary>
[ApiController]
[Route("api/v1/anomaly-rules")]
public sealed class AnomalyRulesController : ControllerBase
{
    private readonly AnomalyRuleStore _store;
    private readonly IQueryEngine _engine;
    private readonly IAuditLogger _audit;

    public AnomalyRulesController(AnomalyRuleStore store, IQueryEngine engine, IAuditLogger audit)
    {
        _store = store;
        _engine = engine;
        _audit = audit;
    }

    /// <summary>Every rule.</summary>
    [HttpGet]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(typeof(List<AnomalyRule>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) => Ok(await _store.ListAsync(cancellationToken));

    /// <summary>Adds or changes a rule.</summary>
    [HttpPut("{id}")]
    [RequireEveryIndex(Capability.Search)]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(typeof(AnomalyRule), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Save(string id, [FromBody] AnomalyRule rule, CancellationToken cancellationToken)
    {
        if (Validate(rule) is { } problem)
        {
            return Problem(detail: problem, statusCode: StatusCodes.Status400BadRequest);
        }

        var existing = await _store.GetAsync(id, cancellationToken);
        rule.Id = id;
        rule.LastWindowEnd = existing?.LastWindowEnd;
        rule.LastResult = existing?.LastResult;
        await _store.SaveAsync(rule, cancellationToken);
        await AuditAsync(existing == null ? AuditAction.Create : AuditAction.Update, $"{(existing == null ? "Created" : "Changed")} anomaly rule {rule.Name} ({rule.Kind}): {rule.Query}");
        return Ok(rule);
    }

    /// <summary>Deletes a rule.</summary>
    [HttpDelete("{id}")]
    [RequireEveryIndex(Capability.Search)]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        if (await _store.GetAsync(id, cancellationToken) is not { } rule)
        {
            return NotFound();
        }

        await _store.DeleteAsync(id, cancellationToken);
        await AuditAsync(AuditAction.Delete, $"Deleted anomaly rule {rule.Name}");
        return NoContent();
    }

    /// <summary>What a rule would find in the last whole window, without alerting.</summary>
    [HttpPost("test")]
    [RequireEveryIndex(Capability.Search)]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(typeof(List<AnomalyFinding>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Test([FromBody] AnomalyRule rule, CancellationToken cancellationToken)
    {
        if (Validate(rule) is { } problem)
        {
            return Problem(detail: problem, statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            return Ok(await AnomalyDetector.EvaluateAsync(rule, AnomalyDetector.WindowEnd(DateTime.UtcNow, rule.WindowMinutes), _engine, cancellationToken));
        }
        catch (QueryParseException ex)
        {
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static string? Validate(AnomalyRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Length > 200) return "Give the rule a name of up to 200 characters.";
        if (string.IsNullOrWhiteSpace(rule.Query) || rule.Query.Length > 4000) return "Give the rule a search of up to 4,000 characters.";
        if (!AnomalyDetector.IsValidField(rule.GroupBy) || !AnomalyDetector.IsValidField(rule.Field)) return "Field names are letters, digits, '_' and '.', starting with a letter or '_'.";
        if (rule.Kind == AnomalyKind.NewValue && string.IsNullOrWhiteSpace(rule.Field)) return "A new-value rule needs a field.";
        if (rule.WindowMinutes is < 1 or > 1440) return "A window is 1 to 1,440 minutes.";
        if (rule.BaselineWindows is < 2 or > 2000) return "The baseline is 2 to 2,000 windows.";
        if (rule.Threshold is <= 0 or > 100) return "The threshold is above 0 and at most 100 standard deviations.";
        return rule.MinCount < 0 ? "The minimum count can't be negative." : null;
    }

    private Task AuditAsync(AuditAction action, string description) =>
        _audit.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = action,
            ResourceType = "AnomalyRule",
            Description = description,
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
        });
}
