using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cabinet.API.Services.Investigator;

/// <summary>
/// A model behind an OpenAI-compatible chat completions API, such as Ollama, LM Studio, vLLM or
/// llama.cpp's server, usually on this machine or network, so logs stay in-house.
/// </summary>
public sealed class OpenAiCompatibleModel : IInvestigatorModel
{
    private readonly HttpClient _http;
    private readonly InvestigatorOptions _options;
    private readonly ILogger<OpenAiCompatibleModel> _logger;

    public OpenAiCompatibleModel(HttpClient http, InvestigatorOptions options, ILogger<OpenAiCompatibleModel> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> InvestigateAsync(string systemPrompt, string question, SearchTool search, int maxSearches, CancellationToken cancellationToken)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
            new JsonObject { ["role"] = "user", ["content"] = question },
        };
        var tools = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = InvestigatorPrompt.ToolName,
                    ["description"] = InvestigatorPrompt.ToolDescription,
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string", ["description"] = "The BQL search" } },
                        ["required"] = new JsonArray("query"),
                    },
                },
            },
        };

        var searches = 0;
        while (true)
        {
            // Out of searches, it has to answer with what it has
            var request = new JsonObject
            {
                ["model"] = _options.ModelFor("openai-compatible"),
                ["messages"] = messages.DeepClone(),
                ["temperature"] = 0.2,
            };
            if (searches < maxSearches)
            {
                request["tools"] = tools.DeepClone();
            }

            var message = await CompleteAsync(request, cancellationToken);
            var calls = message["tool_calls"] as JsonArray;
            if (calls is not { Count: > 0 } || searches >= maxSearches)
            {
                return message["content"]?.GetValue<string>()?.Trim() is { Length: > 0 } answer ? answer : "The model gave no answer.";
            }

            messages.Add(message.DeepClone());
            foreach (var call in calls.OfType<JsonObject>())
            {
                var id = call["id"]?.GetValue<string>() ?? $"call_{searches}";
                string output;
                if (searches >= maxSearches)
                {
                    output = "No searches left; answer with what you have.";
                }
                else if (call["function"]?["name"]?.GetValue<string>() != InvestigatorPrompt.ToolName || Query(call) is not { } query)
                {
                    output = $"Call {InvestigatorPrompt.ToolName} with a \"query\" string.";
                }
                else
                {
                    searches++;
                    output = await search(query, cancellationToken);
                }

                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = output });
            }
        }
    }

    /// <summary>The query from a call's arguments, which arrive as a JSON string.</summary>
    private static string? Query(JsonObject call)
    {
        try
        {
            var arguments = call["function"]?["arguments"];
            var parsed = arguments is JsonValue value && value.TryGetValue<string>(out var text) ? JsonNode.Parse(text) : arguments;
            return parsed?["query"]?.GetValue<string>() is { Length: > 0 } query ? query : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Small models sometimes write arguments that aren't JSON; asking again fixes it
            return null;
        }
    }

    private async Task<JsonObject> CompleteAsync(JsonObject request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        using var response = await _http.SendAsync(message, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("The investigator's model server answered {Status}", (int)response.StatusCode);
            throw new InvestigatorUnavailableException($"The model server at {_options.BaseUrl} answered {(int)response.StatusCode}: {(body.Length > 300 ? body[..300] : body)}");
        }

        return JsonNode.Parse(body)?["choices"]?[0]?["message"] as JsonObject
            ?? throw new InvestigatorUnavailableException("The model server's answer had no message");
    }
}

/// <summary>The model couldn't be reached or didn't answer usefully.</summary>
public sealed class InvestigatorUnavailableException(string message) : Exception(message);
