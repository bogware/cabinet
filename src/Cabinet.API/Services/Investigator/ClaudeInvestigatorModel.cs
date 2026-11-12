using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;

namespace Cabinet.API.Services.Investigator;

/// <summary>
/// Claude through Anthropic's API, for those who choose it over a local model. Logs leave this
/// network, masked as for a Viewer. Uses the official SDK with a manual tool loop.
/// </summary>
public sealed class ClaudeInvestigatorModel : IInvestigatorModel
{
    private readonly AnthropicClient _client;
    private readonly InvestigatorOptions _options;

    public ClaudeInvestigatorModel(InvestigatorOptions options)
    {
        _options = options;

        // An empty key leaves the SDK to find credentials itself (ANTHROPIC_API_KEY and others)
        _client = string.IsNullOrWhiteSpace(options.ApiKey) ? new AnthropicClient() : new AnthropicClient { ApiKey = options.ApiKey };
    }

    /// <inheritdoc />
    public async Task<string> InvestigateAsync(string systemPrompt, string question, SearchTool search, int maxSearches, CancellationToken cancellationToken)
    {
        List<BetaMessageParam> messages = [new BetaUserMessageParam { Content = question }];
        var searches = 0;

        // Each round is one request; a model that keeps calling past its searches still stops
        for (var round = 0; round <= maxSearches + 2; round++)
        {
            var response = await _client.Beta.Messages.Create(Request(systemPrompt, messages), cancellationToken);

            // A declined request has no answer to read
            if (response.StopReason == "refusal")
            {
                return "Claude declined to answer this question.";
            }

            List<BetaContentBlockParam> echo = [];
            List<BetaContentBlockParam> results = [];
            var text = new List<string>();
            foreach (var block in response.Content)
            {
                if (block.TryPickText(out var said))
                {
                    text.Add(said.Text);
                    echo.Add(new BetaTextBlockParam { Text = said.Text });
                }
                else if (block.TryPickThinking(out var thinking))
                {
                    // Passed back unchanged, signature included
                    echo.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                }
                else if (block.TryPickRedactedThinking(out var redacted))
                {
                    echo.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
                }
                else if (block.TryPickToolUse(out var call))
                {
                    echo.Add(new BetaToolUseBlockParam { ID = call.ID, Name = call.Name, Input = call.Input });
                    string output;
                    if (searches >= maxSearches)
                    {
                        output = "No searches left; answer with what you have.";
                    }
                    else if (call.Name != InvestigatorPrompt.ToolName || !call.Input.TryGetValue("query", out var query) || query.ValueKind != JsonValueKind.String)
                    {
                        output = $"Call {InvestigatorPrompt.ToolName} with a \"query\" string.";
                    }
                    else
                    {
                        searches++;
                        output = await search(query.GetString()!, cancellationToken);
                    }

                    // One result for every call, or the next request is rejected
                    results.Add(new BetaToolResultBlockParam { ToolUseID = call.ID, Content = output });
                }
            }

            if (results.Count == 0)
            {
                var answer = string.Join("\n", text).Trim();
                return answer.Length > 0 ? answer : response.StopReason == "max_tokens" ? "The answer was too long to finish." : "Claude gave no answer.";
            }

            messages.Add(new BetaAssistantMessageParam { Content = echo });
            messages.Add(new BetaUserMessageParam { Content = results });
        }

        return "The investigation used all its searches without reaching an answer. Try a narrower question.";
    }

    private MessageCreateParams Request(string systemPrompt, List<BetaMessageParam> messages) => new()
    {
        Model = _options.ModelFor("anthropic"),
        MaxTokens = 16000,

        // A request Claude declines is re-served by a fallback model in the same call
        Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
        Fallbacks = new Default(),

        // The prompt and tool never change, so they're cached across questions
        System = new List<BetaTextBlockParam>
        {
            new() { Text = systemPrompt, CacheControl = new BetaCacheControlEphemeral() },
        },
        // Always sent, as earlier turns hold calls to it; past the limit each call is told no
        Tools = [SearchToolDefinition],
        Messages = [.. messages],
    };

    private static readonly BetaTool SearchToolDefinition = new()
    {
        Name = InvestigatorPrompt.ToolName,
        Description = InvestigatorPrompt.ToolDescription,
        InputSchema = new()
        {
            Properties = new Dictionary<string, JsonElement>
            {
                ["query"] = JsonSerializer.SerializeToElement(new { type = "string", description = "The BQL search" }),
            },
            Required = ["query"],
        },
    };
}
