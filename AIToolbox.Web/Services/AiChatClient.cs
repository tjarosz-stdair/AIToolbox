using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIToolbox.Web.Models;
using AIToolbox.Web.Security;
using Microsoft.Extensions.AI;

namespace AIToolbox.Web.Services;

/// <summary>
/// Tool-calling IChatClient bound to one AiProvider row. Supports the two wire formats
/// AiProviderClient already speaks for interactive prompts (OpenAI-compatible/Azure OpenAI Chat
/// Completions, and Anthropic Messages) extended to carry message history and tool calls/results,
/// which the flat-prompt AiProviderClient API has no shape for. Not real token-level streaming —
/// GetStreamingResponseAsync fakes it from one completed response, matching how AI Compare's own
/// "streaming" is provider-completion-level, not token-level (see Part 0 discovery).
/// </summary>
public sealed class AiChatClient : IChatClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AiProvider _provider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISecretProtector _secretProtector;
    private readonly ILogger<AiChatClient> _logger;

    public AiChatClient(
        AiProvider provider,
        IHttpClientFactory httpClientFactory,
        ISecretProtector secretProtector,
        ILogger<AiChatClient> logger)
    {
        _provider = provider;
        _httpClientFactory = httpClientFactory;
        _secretProtector = secretProtector;
        _logger = logger;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_provider.RequestTimeoutSeconds, 5, 600)));

        using var request = BuildRequest(messageList, options);
        var client = _httpClientFactory.CreateClient("AiProviders");
        using var response = await client.SendAsync(request, timeout.Token);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"AI provider '{_provider.DisplayName}' request failed: {(int)response.StatusCode} {response.ReasonPhrase}: {CreateExcerpt(body)}");
        }

        return _provider.ProviderType == AiProviderType.Anthropic
            ? ParseAnthropicResponse(body)
            : ParseOpenAiResponse(body);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var message in response.Messages)
        {
            yield return new ChatResponseUpdate(message.Role, message.Contents)
            {
                ResponseId = response.ResponseId,
                ModelId = response.ModelId,
                FinishReason = response.FinishReason
            };
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }

    // ----- Request building -----

    private HttpRequestMessage BuildRequest(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _provider.EndpointUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var apiKey = _secretProtector.Unprotect(_provider.ApiKeyCipherText);
        JsonObject body;

        switch (_provider.ProviderType)
        {
            case AiProviderType.Anthropic:
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                }

                request.Headers.TryAddWithoutValidation(
                    "anthropic-version",
                    string.IsNullOrWhiteSpace(_provider.ApiVersion) ? "2023-06-01" : _provider.ApiVersion);
                body = BuildAnthropicRequestBody(messages, options);
                break;

            case AiProviderType.AzureOpenAi:
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.TryAddWithoutValidation("api-key", apiKey);
                }

                body = BuildOpenAiRequestBody(messages, options);
                break;

            case AiProviderType.OpenAiCompatible:
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }

                body = BuildOpenAiRequestBody(messages, options);
                break;

            default:
                throw new NotSupportedException(
                    $"Tool-calling chat is not implemented for provider type '{_provider.ProviderType}'. " +
                    "Only OpenAiCompatible, AzureOpenAi, and Anthropic are supported.");
        }

        request.Content = new StringContent(body.ToJsonString(JsonOptions), Encoding.UTF8, "application/json");
        return request;
    }

    private JsonObject BuildOpenAiRequestBody(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var body = new JsonObject
        {
            ["model"] = _provider.ModelName,
            ["messages"] = MapMessagesForOpenAi(messages, options)
        };

        if (options?.Tools is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var tool in options.Tools.OfType<AIFunction>())
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = JsonNode.Parse(tool.JsonSchema.GetRawText())
                    }
                });
            }

            body["tools"] = tools;
        }

        if (options?.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        var maxOutputTokens = options?.MaxOutputTokens ?? _provider.MaxOutputTokens;
        if (maxOutputTokens is > 0)
        {
            body["max_tokens"] = maxOutputTokens.Value;
        }

        return body;
    }

    private static JsonArray MapMessagesForOpenAi(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var array = new JsonArray();

        var systemText = CollectSystemText(messages, options);
        if (!string.IsNullOrWhiteSpace(systemText))
        {
            array.Add(new JsonObject { ["role"] = "system", ["content"] = systemText });
        }

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                continue; // already folded into the combined system message above
            }

            if (message.Role == ChatRole.Tool)
            {
                foreach (var result in message.Contents.OfType<FunctionResultContent>())
                {
                    array.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = result.CallId,
                        ["content"] = ResultToText(result.Result)
                    });
                }

                continue;
            }

            var role = message.Role == ChatRole.Assistant ? "assistant" : "user";
            var textParts = message.Contents.OfType<TextContent>().Select(c => c.Text).Where(t => !string.IsNullOrEmpty(t));
            var text = string.Join("\n", textParts);
            var toolCalls = message.Contents.OfType<FunctionCallContent>().ToList();

            var entry = new JsonObject { ["role"] = role };
            if (!string.IsNullOrEmpty(text) || toolCalls.Count == 0)
            {
                entry["content"] = string.IsNullOrEmpty(text) ? null : text;
            }

            if (toolCalls.Count > 0)
            {
                var toolCallsArray = new JsonArray();
                foreach (var call in toolCalls)
                {
                    toolCallsArray.Add(new JsonObject
                    {
                        ["id"] = call.CallId,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = call.Name,
                            ["arguments"] = JsonSerializer.Serialize(call.Arguments, JsonOptions)
                        }
                    });
                }

                entry["tool_calls"] = toolCallsArray;
            }

            array.Add(entry);
        }

        return array;
    }

    private JsonObject BuildAnthropicRequestBody(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var body = new JsonObject
        {
            ["model"] = _provider.ModelName,
            ["max_tokens"] = options?.MaxOutputTokens ?? _provider.MaxOutputTokens.GetValueOrDefault(4000),
            ["messages"] = MapMessagesForAnthropic(messages)
        };

        var systemText = CollectSystemText(messages, options);
        if (!string.IsNullOrWhiteSpace(systemText))
        {
            body["system"] = systemText;
        }

        if (options?.Tools is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var tool in options.Tools.OfType<AIFunction>())
            {
                tools.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["input_schema"] = JsonNode.Parse(tool.JsonSchema.GetRawText())
                });
            }

            body["tools"] = tools;
        }

        if (options?.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        return body;
    }

    private static JsonArray MapMessagesForAnthropic(IReadOnlyList<ChatMessage> messages)
    {
        var array = new JsonArray();

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                continue; // Anthropic carries system text in a top-level "system" field, not messages
            }

            if (message.Role == ChatRole.Tool)
            {
                // Anthropic has no "tool" role: tool results travel back as a user message
                // containing tool_result content blocks.
                var toolResultBlocks = new JsonArray();
                foreach (var result in message.Contents.OfType<FunctionResultContent>())
                {
                    toolResultBlocks.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = result.CallId,
                        ["content"] = ResultToText(result.Result)
                    });
                }

                if (toolResultBlocks.Count > 0)
                {
                    array.Add(new JsonObject { ["role"] = "user", ["content"] = toolResultBlocks });
                }

                continue;
            }

            var role = message.Role == ChatRole.Assistant ? "assistant" : "user";
            var contentBlocks = new JsonArray();
            foreach (var item in message.Contents)
            {
                switch (item)
                {
                    case TextContent text when !string.IsNullOrEmpty(text.Text):
                        contentBlocks.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                        break;
                    case FunctionCallContent call:
                        contentBlocks.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = call.CallId,
                            ["name"] = call.Name,
                            ["input"] = JsonNode.Parse(JsonSerializer.Serialize(call.Arguments, JsonOptions))
                        });
                        break;
                }
            }

            if (contentBlocks.Count > 0)
            {
                array.Add(new JsonObject { ["role"] = role, ["content"] = contentBlocks });
            }
        }

        return array;
    }

    private static string? CollectSystemText(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(options?.Instructions))
        {
            parts.Add(options.Instructions);
        }

        parts.AddRange(messages
            .Where(m => m.Role == ChatRole.System)
            .Select(m => m.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t)));

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    private static string ResultToText(object? result)
    {
        return result switch
        {
            null => string.Empty,
            string text => text,
            _ => JsonSerializer.Serialize(result, JsonOptions)
        };
    }

    // ----- Response parsing -----

    private ChatResponse ParseOpenAiResponse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var choice = root.GetProperty("choices")[0];
        var messageElement = choice.GetProperty("message");

        var contents = new List<AIContent>();
        if (messageElement.TryGetProperty("content", out var contentElement)
            && contentElement.ValueKind == JsonValueKind.String
            && contentElement.GetString() is { Length: > 0 } text)
        {
            contents.Add(new TextContent(text));
        }

        if (messageElement.TryGetProperty("tool_calls", out var toolCallsElement)
            && toolCallsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var toolCall in toolCallsElement.EnumerateArray())
            {
                var callId = toolCall.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("n");
                var function = toolCall.GetProperty("function");
                var name = function.GetProperty("name").GetString() ?? string.Empty;
                var argumentsJson = function.TryGetProperty("arguments", out var argsElement)
                    ? argsElement.GetString()
                    : null;
                var arguments = ParseArguments(argumentsJson);
                contents.Add(new FunctionCallContent(callId, name, arguments));
            }
        }

        var finishReasonText = choice.TryGetProperty("finish_reason", out var finishReasonElement)
            ? finishReasonElement.GetString()
            : null;

        var responseMessage = new ChatMessage(ChatRole.Assistant, contents);
        return new ChatResponse(responseMessage)
        {
            ModelId = _provider.ModelName,
            FinishReason = MapOpenAiFinishReason(finishReasonText),
            Usage = ExtractOpenAiUsage(root)
        };
    }

    private ChatResponse ParseAnthropicResponse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var contents = new List<AIContent>();
        if (root.TryGetProperty("content", out var contentArray) && contentArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in contentArray.EnumerateArray())
            {
                var blockType = block.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                if (blockType == "text" && block.TryGetProperty("text", out var textElement))
                {
                    contents.Add(new TextContent(textElement.GetString() ?? string.Empty));
                }
                else if (blockType == "tool_use")
                {
                    var callId = block.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("n");
                    var name = block.GetProperty("name").GetString() ?? string.Empty;
                    var arguments = block.TryGetProperty("input", out var inputElement)
                        ? ParseArguments(inputElement.GetRawText())
                        : new Dictionary<string, object?>();
                    contents.Add(new FunctionCallContent(callId, name, arguments));
                }
            }
        }

        var stopReason = root.TryGetProperty("stop_reason", out var stopReasonElement)
            ? stopReasonElement.GetString()
            : null;

        var responseMessage = new ChatMessage(ChatRole.Assistant, contents);
        return new ChatResponse(responseMessage)
        {
            ModelId = _provider.ModelName,
            FinishReason = MapAnthropicStopReason(stopReason),
            Usage = ExtractAnthropicUsage(root)
        };
    }

    private static Dictionary<string, object?> ParseArguments(string? json)
    {
        var arguments = new Dictionary<string, object?>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return arguments;
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return arguments;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            arguments[property.Name] = property.Value.Clone();
        }

        return arguments;
    }

    private static ChatFinishReason? MapOpenAiFinishReason(string? finishReason)
    {
        return finishReason switch
        {
            null => null,
            "stop" => ChatFinishReason.Stop,
            "tool_calls" => ChatFinishReason.ToolCalls,
            "length" => ChatFinishReason.Length,
            "content_filter" => ChatFinishReason.ContentFilter,
            _ => new ChatFinishReason(finishReason)
        };
    }

    private static ChatFinishReason? MapAnthropicStopReason(string? stopReason)
    {
        return stopReason switch
        {
            null => null,
            "end_turn" => ChatFinishReason.Stop,
            "tool_use" => ChatFinishReason.ToolCalls,
            "max_tokens" => ChatFinishReason.Length,
            _ => new ChatFinishReason(stopReason)
        };
    }

    private static UsageDetails? ExtractOpenAiUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage))
        {
            return null;
        }

        return new UsageDetails
        {
            InputTokenCount = TryGetInt(usage, "prompt_tokens"),
            OutputTokenCount = TryGetInt(usage, "completion_tokens"),
            TotalTokenCount = TryGetInt(usage, "total_tokens")
        };
    }

    private static UsageDetails? ExtractAnthropicUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage))
        {
            return null;
        }

        var input = TryGetInt(usage, "input_tokens");
        var output = TryGetInt(usage, "output_tokens");
        return new UsageDetails
        {
            InputTokenCount = input,
            OutputTokenCount = output,
            TotalTokenCount = input is null && output is null ? null : (input ?? 0) + (output ?? 0)
        };
    }

    private static int? TryGetInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static string CreateExcerpt(string value)
    {
        const int maxLength = 800;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
