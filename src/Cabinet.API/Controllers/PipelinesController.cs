using Cabinet.API.Middleware;
using Cabinet.API.Services;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Parsers;
using Microsoft.AspNetCore.Mvc;

namespace Cabinet.API.Controllers;

/// <summary>
/// The ingest pipeline: rules that drop, sample, route or redact events before they're stored,
/// and what they've saved.
/// </summary>
[ApiController]
[Route("api/v1/pipelines")]
[RequireCapability(Capability.IndexManagement)]
public sealed class PipelinesController : ControllerBase
{
    private readonly IDocumentStore _documents;
    private readonly IngestPipeline _pipeline;
    private readonly IAuditLogger _audit;

    public PipelinesController(IDocumentStore documents, IngestPipeline pipeline, IAuditLogger audit)
    {
        _documents = documents;
        _pipeline = pipeline;
        _audit = audit;
    }

    private async Task<List<PipelineRule>> RulesAsync(CancellationToken cancellationToken) =>
        (await _documents.ListAsync<PipelineRule>(IngestPipeline.Collection, cancellationToken)).OrderBy(r => r.Order).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();

    /// <summary>The rules, in the order they run.</summary>
    [HttpGet("rules")]
    [ProducesResponseType(typeof(List<PipelineRule>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) => Ok(await RulesAsync(cancellationToken));

    /// <summary>Adds or changes a rule. It applies to new events on every instance within 30 seconds.</summary>
    [HttpPut("rules/{id}")]
    [RequireEveryIndex(Capability.IndexManagement)]
    [ProducesResponseType(typeof(PipelineRule), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Save(string id, [FromBody] PipelineRule rule, CancellationToken cancellationToken)
    {
        rule.Id = id;
        if (IngestPipeline.Validate(rule) is { } problem)
        {
            return Problem(detail: problem, statusCode: StatusCodes.Status400BadRequest);
        }

        await _documents.PutAsync(IngestPipeline.Collection, id, rule, cancellationToken);
        await _pipeline.ReloadAsync(cancellationToken);
        await AuditAsync(AuditAction.Update, $"Set ingest rule {rule.Name}: {rule.Action}{(rule.Enabled ? string.Empty : " (off)")}");
        return Ok(rule);
    }

    /// <summary>Deletes a rule.</summary>
    [HttpDelete("rules/{id}")]
    [RequireEveryIndex(Capability.IndexManagement)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        if (!await _documents.DeleteAsync(IngestPipeline.Collection, id, cancellationToken)) return NotFound();
        await _pipeline.ReloadAsync(cancellationToken);
        await AuditAsync(AuditAction.Delete, $"Deleted ingest rule {id}");
        return NoContent();
    }

    /// <summary>What the saved rules, or the rules given, would do to some lines. Stores nothing.</summary>
    [HttpPost("test")]
    [ProducesResponseType(typeof(List<PipelinePreview>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Test([FromBody] PipelineTestRequest request, [FromServices] LogLineParser parser, CancellationToken cancellationToken)
    {
        var rules = request.Rules ?? await RulesAsync(cancellationToken);
        if (rules.Select(IngestPipeline.Validate).FirstOrDefault(p => p != null) is { } problem)
        {
            return Problem(detail: problem, statusCode: StatusCodes.Status400BadRequest);
        }

        var previews = new List<PipelinePreview>();
        foreach (var line in (request.Lines ?? []).Take(200))
        {
            var logEvent = await parser.ParseAsync(line, cancellationToken: cancellationToken);
            logEvent.Index = request.Index ?? "main";
            var result = IngestPipeline.Preview(rules, logEvent);
            previews.Add(new PipelinePreview { Line = line, Kept = result != null, Index = result?.Index, Result = result?.RawLine });
        }

        return Ok(previews);
    }

    /// <summary>What each rule has done since this instance started, and the volume it kept out of storage.</summary>
    [HttpGet("stats")]
    [ProducesResponseType(typeof(PipelineSavings), StatusCodes.Status200OK)]
    public async Task<IActionResult> Stats(CancellationToken cancellationToken)
    {
        var rules = await RulesAsync(cancellationToken);
        var perRule = rules.Select(rule =>
        {
            var s = _pipeline.Stats.GetValueOrDefault(rule.Id) ?? new PipelineRuleStats();
            return new PipelineRuleSavings
            {
                Id = rule.Id,
                Name = rule.Name,
                Action = rule.Action,
                Matched = Interlocked.Read(ref s.Matched),
                Dropped = Interlocked.Read(ref s.Dropped),
                BytesDropped = Interlocked.Read(ref s.BytesDropped),
                Routed = Interlocked.Read(ref s.Routed),
                Redacted = Interlocked.Read(ref s.Redacted),
            };
        }).ToList();
        return Ok(new PipelineSavings
        {
            Rules = perRule,
            EventsDropped = perRule.Sum(r => r.Dropped),
            BytesDropped = perRule.Sum(r => r.BytesDropped),
            Since = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(),
        });
    }

    private Task AuditAsync(AuditAction action, string description) =>
        _audit.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = action,
            ResourceType = "IngestPipeline",
            Description = description,
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
        });
}

/// <summary>Lines to try the pipeline on.</summary>
public sealed class PipelineTestRequest
{
    public List<string>? Lines { get; set; }

    /// <summary>The index they'd arrive in.</summary>
    public string? Index { get; set; }

    /// <summary>Rules to try instead of the saved ones.</summary>
    public List<PipelineRule>? Rules { get; set; }
}

/// <summary>What happened to one line.</summary>
public sealed class PipelinePreview
{
    public required string Line { get; init; }
    public bool Kept { get; init; }
    public string? Index { get; init; }
    public string? Result { get; init; }
}

/// <summary>What the pipeline kept out of storage on this instance.</summary>
public sealed class PipelineSavings
{
    public required List<PipelineRuleSavings> Rules { get; init; }
    public long EventsDropped { get; init; }
    public long BytesDropped { get; init; }
    public DateTime Since { get; init; }
}

/// <summary>One rule's counts.</summary>
public sealed class PipelineRuleSavings
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public PipelineAction Action { get; init; }
    public long Matched { get; init; }
    public long Dropped { get; init; }
    public long BytesDropped { get; init; }
    public long Routed { get; init; }
    public long Redacted { get; init; }
}
