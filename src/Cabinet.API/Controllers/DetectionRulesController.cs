using Cabinet.API.Middleware;
using Cabinet.API.Services;
using Cabinet.Core.Models;
using Cabinet.Query;
using Cabinet.Query.Bql;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cabinet.API.Controllers;

/// <summary>
/// Rules checked against events as they're ingested. Built-in rules can be turned on and off, and
/// you can add your own.
/// </summary>
[ApiController]
[Route("api/v1/rules")]
[Authorize]
public class DetectionRulesController : ControllerBase
{
    private readonly DetectionRuleStore _rules;
    private readonly Infrastructure.ServerLimits _limits;
    private readonly OutboundUrlGuard? _guard;
    private readonly ConnectorStore? _connectors;
    private readonly Cabinet.API.Authentication.IndexAccessScope? _scope;
    private readonly Cabinet.Core.Interfaces.IAuditLogger? _audit;

    public DetectionRulesController(
        DetectionRuleStore rules,
        Infrastructure.ServerLimits? limits = null,
        OutboundUrlGuard? guard = null,
        ConnectorStore? connectors = null,
        Cabinet.API.Authentication.IndexAccessScope? scope = null,
        Cabinet.Core.Interfaces.IAuditLogger? audit = null)
    {
        _rules = rules;
        _limits = limits ?? new Infrastructure.ServerLimits();
        _guard = guard;
        _connectors = connectors;
        _scope = scope;
        _audit = audit;
    }

    private Task AuditAsync(AuditAction action, string id, string description) => _audit?.LogAsync(new AuditEvent
    {
        PrincipalId = User.FindFirst("ClientId")?.Value ?? User.Identity?.Name ?? "unknown",
        Action = action,
        ResourceType = "DetectionRule",
        ResourceId = id,
        Description = description,
        SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
    }) ?? Task.CompletedTask;

    /// <summary>
    /// List rules
    /// </summary>
    [HttpGet]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(typeof(List<DetectionRule>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken = default) =>
        Ok(await _rules.ListAsync(cancellationToken));

    /// <summary>
    /// Get a rule
    /// </summary>
    [HttpGet("{id}")]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(typeof(DetectionRule), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken = default)
    {
        var rule = await _rules.GetAsync(id, cancellationToken);
        return rule == null ? Problem(detail: "Rule not found", statusCode: 404) : Ok(rule);
    }

    /// <summary>
    /// Add a rule
    /// </summary>
    [HttpPost]
    [RequireEveryIndex(Capability.Search)]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(typeof(DetectionRule), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] DetectionRule rule, CancellationToken cancellationToken = default)
    {
        rule.Id = Cabinet.Core.Utilities.Ulid.NewUlid();
        rule.Builtin = false;
        if (await ValidateAsync(rule, cancellationToken) is { } problem)
        {
            return problem;
        }

        await _rules.SaveAsync(rule, cancellationToken);
        await AuditAsync(AuditAction.Create, rule.Id, $"Added detection rule {rule.Name}");
        return CreatedAtAction(nameof(Get), new { id = rule.Id }, rule);
    }

    /// <summary>
    /// Change a rule. Built-in rules can only be turned on and off.
    /// </summary>
    [HttpPut("{id}")]
    [RequireEveryIndex(Capability.Search)]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(typeof(DetectionRule), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(string id, [FromBody] DetectionRule rule, CancellationToken cancellationToken = default)
    {
        var existing = await _rules.GetAsync(id, cancellationToken);
        if (existing == null)
        {
            return Problem(detail: "Rule not found", statusCode: 404);
        }

        if (existing.Builtin)
        {
            return Problem(detail: "Built-in rules can only be turned on and off. Copy it to a new rule to change it.", statusCode: 400);
        }

        rule.Id = id;
        rule.Builtin = false;
        if (await ValidateAsync(rule, cancellationToken) is { } problem)
        {
            return problem;
        }

        await _rules.SaveAsync(rule, cancellationToken);
        await AuditAsync(AuditAction.Update, id, $"Changed detection rule {rule.Name}");
        return Ok(rule);
    }

    /// <summary>
    /// Turn a rule on
    /// </summary>
    [HttpPost("{id}/enable")]
    [RequireEveryIndex(Capability.Search)]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Enable(string id, CancellationToken cancellationToken = default)
    {
        if (!await _rules.SetEnabledAsync(id, true, cancellationToken))
        {
            return Problem(detail: "Rule not found", statusCode: 404);
        }

        await AuditAsync(AuditAction.Update, id, "Turned a detection rule on");
        return NoContent();
    }

    /// <summary>
    /// Turn a rule off
    /// </summary>
    [HttpPost("{id}/disable")]
    [RequireEveryIndex(Capability.Search)]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Disable(string id, CancellationToken cancellationToken = default)
    {
        if (!await _rules.SetEnabledAsync(id, false, cancellationToken))
        {
            return Problem(detail: "Rule not found", statusCode: 404);
        }

        await AuditAsync(AuditAction.Update, id, "Turned a detection rule off");
        return NoContent();
    }

    /// <summary>
    /// Delete a rule. Built-in rules can only be turned off.
    /// </summary>
    [HttpDelete("{id}")]
    [RequireEveryIndex(Capability.Search)]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken = default)
    {
        if (!await _rules.DeleteAsync(id, cancellationToken))
        {
            return Problem(detail: "Rule not found", statusCode: 404);
        }

        await AuditAsync(AuditAction.Delete, id, "Deleted a detection rule");
        return NoContent();
    }

    /// <summary>
    /// Try a rule's expressions on a line, without saving anything
    /// </summary>
    [HttpPost("test")]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(typeof(RuleTestResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Test(
        [FromBody] RuleTestRequest request,
        [FromServices] Cabinet.Parsers.LogLineParser parser,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var matcher = new BqlEventMatcher(request.Pattern, request.Condition, request.Extract);
            var logEvent = await parser.ParseAsync(request.Line, cancellationToken: cancellationToken);
            var matched = matcher.TryMatch(logEvent, out var fields);
            return Ok(new RuleTestResult
            {
                Matched = matched,
                Fields = matched
                    ? fields.Where(field => field.Value is not null).ToDictionary(field => field.Key, field => field.Value!.ToString() ?? string.Empty)
                    : new Dictionary<string, string>(),
            });
        }
        catch (QueryParseException ex)
        {
            return Problem(detail: ex.Message, statusCode: 400);
        }
    }

    /// <summary>
    /// Run a rule over stored events to see how often it would have fired, without alerting.
    /// Send <c>rule</c> to try changes before saving them.
    /// </summary>
    [HttpPost("backtest")]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(typeof(BacktestResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Backtest(
        [FromBody] BacktestRequest request,
        [FromServices] Cabinet.Core.Interfaces.ILogStorage storage,
        [FromServices] Cabinet.API.Authentication.IndexAccessScope scope,
        CancellationToken cancellationToken = default)
    {
        var rule = request.Rule;
        if (rule == null && request.RuleId != null)
        {
            rule = await _rules.GetAsync(request.RuleId, cancellationToken);
            if (rule == null)
            {
                return Problem(detail: "Rule not found", statusCode: 404);
            }
        }

        if (rule == null)
        {
            return Problem(detail: "Send ruleId or rule", statusCode: 400);
        }

        rule.Name = string.IsNullOrWhiteSpace(rule.Name) ? "Backtest" : rule.Name;
        if (await ValidateAsync(rule, cancellationToken) is { } problem)
        {
            return problem;
        }

        var hours = Math.Clamp(request.Hours, 1, _limits.BacktestMaxHours);
        var end = DateTime.UtcNow;

        // The caller's own view: only indexes they can read, with masking applied
        var result = await RuleBacktester.RunAsync(rule, scope.Restrict(storage), end.AddHours(-hours), end, Math.Clamp(request.MaxEvents, 1, _limits.BacktestMaxEvents), cancellationToken);
        return Ok(result);
    }

    private async Task<ObjectResult?> ValidateAsync(DetectionRule rule, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            return Problem(detail: "Name is required", statusCode: 400);
        }

        if (rule.Threshold < 1 || rule.WindowSeconds < 1 || rule.CooldownSeconds < 0)
        {
            return Problem(detail: "Threshold and WindowSeconds must be positive, and CooldownSeconds can't be negative", statusCode: 400);
        }

        if (!Enum.TryParse<Cabinet.Alerts.AlertSeverity>(rule.Severity, ignoreCase: true, out _))
        {
            return Problem(detail: "Severity must be Info, Warning, Error or Critical", statusCode: 400);
        }

        foreach (var action in rule.Actions ?? [])
        {
            var type = action.Type?.ToLowerInvariant();
            if (type is not ("notify" or "event" or "webhook" or "connector"))
            {
                return Problem(detail: $"Action type must be notify, event, webhook or connector, not '{action.Type}'", statusCode: 400);
            }

            if (type == "webhook")
            {
                var reason = _guard?.Check(action.Target) ?? (Uri.TryCreate(action.Target, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" ? null : "Needs an http or https URL");
                if (reason != null)
                {
                    return Problem(detail: $"Webhook action: {reason}", statusCode: 400);
                }
            }

            if (type == "connector")
            {
                if (string.IsNullOrWhiteSpace(action.Target))
                {
                    return Problem(detail: "A connector action needs the connector's name in Target", statusCode: 400);
                }

                if (_connectors != null && await _connectors.GetAsync(action.Target, cancellationToken) == null)
                {
                    return Problem(detail: $"There's no connector named '{action.Target}'", statusCode: 400);
                }
            }

            // Writing events into an index is ingesting into it, so it needs that right.
            if (type == "event")
            {
                var index = string.IsNullOrWhiteSpace(action.Target) ? DetectionActionExecutor.DefaultEventIndex : action.Target.Trim();
                if (_scope?.Principal is not null && !_scope.Can(Capability.Ingest, index))
                {
                    return Problem(detail: $"Not allowed to write events to index '{index}'", statusCode: 403);
                }
            }

            if (action.Severity != null && !Enum.TryParse<Cabinet.Alerts.AlertSeverity>(action.Severity, ignoreCase: true, out _))
            {
                return Problem(detail: "An action's severity must be Info, Warning, Error or Critical", statusCode: 400);
            }
        }

        try
        {
            _ = new BqlEventMatcher(rule.Pattern, rule.Condition, rule.Extract);
            return null;
        }
        catch (QueryParseException ex)
        {
            return Problem(detail: ex.Message, statusCode: 400);
        }
    }
}

/// <summary>Which rule to backtest, and over how long.</summary>
public class BacktestRequest
{
    /// <summary>A saved rule.</summary>
    public string? RuleId { get; set; }

    /// <summary>Or a rule that isn't saved, to try changes.</summary>
    public DetectionRule? Rule { get; set; }

    /// <summary>How far back to look.</summary>
    public int Hours { get; set; } = 24;

    /// <summary>Stop after this many events.</summary>
    public long MaxEvents { get; set; } = 1_000_000;
}

/// <summary>A rule's expressions, and a line to try them on.</summary>
public class RuleTestRequest
{
    public string Line { get; set; } = string.Empty;
    public string? Pattern { get; set; }
    public string? Condition { get; set; }
    public string? Extract { get; set; }
}

/// <summary>Whether a line matched, and what fields it would have.</summary>
public class RuleTestResult
{
    public bool Matched { get; set; }
    public Dictionary<string, string> Fields { get; set; } = new();
}
