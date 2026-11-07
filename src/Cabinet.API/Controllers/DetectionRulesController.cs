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

    public DetectionRulesController(DetectionRuleStore rules)
    {
        _rules = rules;
    }

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
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(typeof(DetectionRule), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] DetectionRule rule, CancellationToken cancellationToken = default)
    {
        rule.Id = Cabinet.Core.Utilities.Ulid.NewUlid();
        rule.Builtin = false;
        if (Validate(rule) is { } problem)
        {
            return problem;
        }

        await _rules.SaveAsync(rule, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = rule.Id }, rule);
    }

    /// <summary>
    /// Change a rule. Built-in rules can only be turned on and off.
    /// </summary>
    [HttpPut("{id}")]
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
        if (Validate(rule) is { } problem)
        {
            return problem;
        }

        await _rules.SaveAsync(rule, cancellationToken);
        return Ok(rule);
    }

    /// <summary>
    /// Turn a rule on
    /// </summary>
    [HttpPost("{id}/enable")]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Enable(string id, CancellationToken cancellationToken = default) =>
        await _rules.SetEnabledAsync(id, true, cancellationToken) ? NoContent() : Problem(detail: "Rule not found", statusCode: 404);

    /// <summary>
    /// Turn a rule off
    /// </summary>
    [HttpPost("{id}/disable")]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Disable(string id, CancellationToken cancellationToken = default) =>
        await _rules.SetEnabledAsync(id, false, cancellationToken) ? NoContent() : Problem(detail: "Rule not found", statusCode: 404);

    /// <summary>
    /// Delete a rule. Built-in rules can only be turned off.
    /// </summary>
    [HttpDelete("{id}")]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken = default) =>
        await _rules.DeleteAsync(id, cancellationToken) ? NoContent() : Problem(detail: "Rule not found", statusCode: 404);

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

    private ObjectResult? Validate(DetectionRule rule)
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
