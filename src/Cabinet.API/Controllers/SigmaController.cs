using Cabinet.API.Middleware;
using Cabinet.API.Services;
using Cabinet.API.Services.Sigma;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query.Bql;
using Microsoft.AspNetCore.Mvc;

namespace Cabinet.API.Controllers;

/// <summary>Sigma rule import, and which ATT&amp;CK techniques the detection rules cover.</summary>
[ApiController]
[Route("api/v1/rules")]
public sealed class SigmaController : ControllerBase
{
    private readonly DetectionRuleStore _rules;
    private readonly IAuditLogger _audit;

    public SigmaController(DetectionRuleStore rules, IAuditLogger audit)
    {
        _rules = rules;
        _audit = audit;
    }

    /// <summary>
    /// Converts Sigma rules (YAML, several separated by ---) to detection rules, and with save=true
    /// adds them, turned off until someone checks them. Rules that can't be converted say why.
    /// </summary>
    [HttpPost("sigma")]
    [RequireEveryIndex(Capability.Search)]
    [RequireCapability(Capability.Alerts)]
    [Consumes("text/plain", "application/x-yaml", "text/yaml")]
    [ProducesResponseType(typeof(List<SigmaImportResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Import([FromQuery] bool save = false, CancellationToken cancellationToken = default)
    {
        string yaml;
        using (var reader = new StreamReader(Request.Body))
        {
            yaml = await reader.ReadToEndAsync(cancellationToken);
        }

        if (yaml.Length > 2_000_000)
        {
            return Problem(detail: "Send at most 2 MB of Sigma rules at a time.", statusCode: StatusCodes.Status400BadRequest);
        }

        var results = new List<SigmaImportResult>();
        foreach (var conversion in SigmaConverter.Convert(yaml))
        {
            var problem = conversion.Problem;
            if (conversion.Rule is { } rule && rule.Condition != null)
            {
                // The condition has to be one the engine can run
                try
                {
                    new BqlParser().ParseBql($"* | where {rule.Condition}");
                }
                catch (Cabinet.Query.QueryParseException ex)
                {
                    problem = $"The converted condition doesn't parse: {ex.Message}";
                }
            }

            var saved = false;
            if (save && problem == null && conversion.Rule != null)
            {
                await _rules.SaveAsync(conversion.Rule, cancellationToken);
                saved = true;
            }

            results.Add(new SigmaImportResult
            {
                Title = conversion.Title,
                Saved = saved,
                Problem = problem,
                RuleId = saved ? conversion.Rule!.Id : null,
                Condition = conversion.Rule?.Condition,
                Technique = conversion.Rule?.Technique,
                Severity = conversion.Rule?.Severity,
            });
        }

        if (save && results.Any(r => r.Saved))
        {
            await _audit.LogAsync(new AuditEvent
            {
                PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
                Action = AuditAction.Create,
                ResourceType = "DetectionRule",
                Description = $"Imported {results.Count(r => r.Saved)} Sigma rule(s), turned off: {string.Join(", ", results.Where(r => r.Saved).Select(r => r.Title).Take(20))}",
                SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            });
        }

        return Ok(results);
    }

    /// <summary>The ATT&amp;CK matrix, with the rules that cover each technique, so gaps show.</summary>
    [HttpGet("coverage")]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(typeof(List<TacticCoverage>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Coverage(CancellationToken cancellationToken)
    {
        var rules = await _rules.ListAsync(cancellationToken);
        var byTechnique = rules
            .Where(r => !string.IsNullOrWhiteSpace(r.Technique))
            .GroupBy(r => MitreAttack.Parent(r.Technique!))
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var known = MitreAttack.Techniques.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        TechniqueCoverage Cell(string id, string name) => new()
        {
            Id = id,
            Name = name,
            Rules = byTechnique.TryGetValue(id, out var covering) ? covering.Select(r => new CoveringRule { Id = r.Id, Name = r.Name, Enabled = r.Enabled }).ToList() : [],
        };

        var matrix = MitreAttack.Tactics.Select(tactic => new TacticCoverage
        {
            Id = tactic.Id,
            Name = tactic.Name,
            Techniques = MitreAttack.Techniques.Where(t => t.Tactics.Contains(tactic.Id)).Select(t => Cell(t.Id, t.Name)).ToList(),
        }).ToList();

        var other = byTechnique.Keys.Where(id => !known.Contains(id)).Order().Select(id => Cell(id, id)).ToList();
        if (other.Count > 0)
        {
            matrix.Add(new TacticCoverage { Id = "other", Name = "Other", Techniques = other });
        }

        return Ok(matrix);
    }
}

/// <summary>What became of one Sigma rule.</summary>
public sealed class SigmaImportResult
{
    public required string Title { get; init; }
    public bool Saved { get; init; }
    public string? Problem { get; init; }
    public string? RuleId { get; init; }
    public string? Condition { get; init; }
    public string? Technique { get; init; }
    public string? Severity { get; init; }
}

/// <summary>One tactic's column of the matrix.</summary>
public sealed class TacticCoverage
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required List<TechniqueCoverage> Techniques { get; init; }
}

/// <summary>One technique, and the rules that cover it.</summary>
public sealed class TechniqueCoverage
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required List<CoveringRule> Rules { get; init; }
}

/// <summary>A rule that covers a technique.</summary>
public sealed class CoveringRule
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; }
}
