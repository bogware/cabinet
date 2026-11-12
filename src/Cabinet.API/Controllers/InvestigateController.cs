using Cabinet.API.Authentication;
using Cabinet.API.Middleware;
using Cabinet.API.Services.Investigator;
using Cabinet.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace Cabinet.API.Controllers;

/// <summary>Asks the AI investigator a question about the logs. Off unless Ai:Enabled is set.</summary>
[ApiController]
[Route("api/v1/investigate")]
[RequireCapability(Capability.Search)]
public sealed class InvestigateController : ControllerBase
{
    private readonly InvestigatorService? _investigator;
    private readonly InvestigatorOptions? _options;
    private readonly IndexAccessScope _scope;
    private readonly ILogger<InvestigateController> _logger;

    public InvestigateController(IndexAccessScope scope, ILogger<InvestigateController> logger, InvestigatorService? investigator = null, InvestigatorOptions? options = null)
    {
        _scope = scope;
        _logger = logger;
        _investigator = investigator;
        _options = options;
    }

    /// <summary>Whether the investigator is on, and with which model.</summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(InvestigatorStatus), StatusCodes.Status200OK)]
    public IActionResult Status() =>
        Ok(new InvestigatorStatus
        {
            Enabled = _investigator != null,
            Model = _investigator != null && _options != null ? $"{_options.Provider}: {_options.ModelFor(_options.Provider.ToLowerInvariant())}" : null,
        });

    /// <summary>Answers a question by running searches with the caller's access.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(InvestigationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Investigate([FromBody] InvestigateRequest request, CancellationToken cancellationToken)
    {
        if (_investigator == null)
        {
            return Problem(detail: "The AI investigator is off. An administrator can turn it on with Ai:Enabled.", statusCode: StatusCodes.Status404NotFound);
        }

        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 2000)
        {
            return Problem(detail: "Ask a question of up to 2,000 characters.", statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            return Ok(await _investigator.InvestigateAsync(request.Question.Trim(), _scope, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken));
        }
        catch (InvestigatorUnavailableException ex)
        {
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "The investigator's model couldn't be reached");
            return Problem(detail: $"The model couldn't be reached: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
        }
        catch (Anthropic.Exceptions.AnthropicApiException ex)
        {
            _logger.LogWarning(ex, "Claude didn't answer the investigator");
            return Problem(detail: $"Claude didn't answer: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Problem(detail: "The investigation took too long. Try a narrower question.", statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }
}

/// <summary>A question for the investigator.</summary>
public sealed class InvestigateRequest
{
    /// <summary>The question, in plain words.</summary>
    public string? Question { get; set; }
}

/// <summary>Whether the investigator is on.</summary>
public sealed class InvestigatorStatus
{
    /// <summary>Whether it's on.</summary>
    public bool Enabled { get; init; }

    /// <summary>The provider and model, when on.</summary>
    public string? Model { get; init; }
}
