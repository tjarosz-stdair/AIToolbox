using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AIToolbox.Web.Models;
using AIToolbox.Web.Security;

namespace AIToolbox.Web.Services;

public sealed class AiProviderClient : IAiProviderClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> OpenAiResponsesIncompleteStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "queued",
        "in_progress"
    };
    private static readonly HashSet<string> OpenAiResponsesTerminalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "completed",
        "failed",
        "cancelled",
        "canceled",
        "incomplete"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISecretProtector _secretProtector;
    private readonly ILogger<AiProviderClient> _logger;

    public AiProviderClient(
        IHttpClientFactory httpClientFactory,
        ISecretProtector secretProtector,
        ILogger<AiProviderClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _secretProtector = secretProtector;
        _logger = logger;
    }

    public async Task<AiToolCallResult> SendPromptAsync(
        AiProvider provider,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(provider.RequestTimeoutSeconds, 5, 600)));

            using var request = BuildRequest(provider, prompt);
            var client = _httpClientFactory.CreateClient("AiProviders");
            using var response = await client.SendAsync(request, timeout.Token);
            var responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return Failure(provider, stopwatch.ElapsedMilliseconds,
                    $"{(int)response.StatusCode} {response.ReasonPhrase}: {CreateExcerpt(responseBody)}");
            }

            var extracted = ExtractResponse(provider.ProviderType, responseBody);
            return new AiToolCallResult
            {
                ProviderId = provider.Id,
                ProviderName = provider.DisplayName,
                ResponseText = extracted.Text,
                FinishReason = extracted.FinishReason,
                InputTokens = extracted.InputTokens,
                OutputTokens = extracted.OutputTokens,
                DurationMs = ToInt32(stopwatch.ElapsedMilliseconds)
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return Failure(provider, stopwatch.ElapsedMilliseconds, "The provider request timed out.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "AI provider {ProviderId} failed.", provider.Id);
            return Failure(provider, stopwatch.ElapsedMilliseconds, ex.Message);
        }
    }

    public async Task<DocumentPromptResult> SendDocumentPromptAsync(
        DocumentPromptRequest documentRequest,
        CancellationToken cancellationToken = default)
    {
        var provider = documentRequest.Provider;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(provider.RequestTimeoutSeconds, 5, 600)));

            var apiKey = _secretProtector.Unprotect(provider.ApiKeyCipherText);
            using var request = BuildDocumentRequest(documentRequest);
            var client = _httpClientFactory.CreateClient("AiProviders");
            using var response = await client.SendAsync(request, timeout.Token);
            var responseBody = await response.Content.ReadAsStringAsync(timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                stopwatch.Stop();
                return DocumentFailure(provider, stopwatch.ElapsedMilliseconds,
                    $"{(int)response.StatusCode} {response.ReasonPhrase}: {CreateExcerpt(responseBody)}");
            }

            if (provider.ProviderType == AiProviderType.OpenAiResponsesBackground)
            {
                responseBody = await PollOpenAiBackgroundResponseAsync(
                    client,
                    request.RequestUri,
                    apiKey,
                    responseBody,
                    timeout.Token);
            }

            stopwatch.Stop();
            var extracted = ExtractResponse(provider.ProviderType, responseBody);
            return new DocumentPromptResult
            {
                ProviderId = provider.Id,
                ProviderName = provider.DisplayName,
                ResponseText = extracted.Text,
                FinishReason = extracted.FinishReason,
                InputTokens = extracted.InputTokens,
                OutputTokens = extracted.OutputTokens,
                DurationMs = ToInt32(stopwatch.ElapsedMilliseconds)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return DocumentFailure(provider, stopwatch.ElapsedMilliseconds, "The provider request timed out.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "AI document provider {ProviderId} failed.", provider.Id);
            return DocumentFailure(provider, stopwatch.ElapsedMilliseconds, ex.Message);
        }
    }

    public async Task<DocumentPromptResult> SendImagePromptAsync(
        ImagePromptRequest imageRequest,
        CancellationToken cancellationToken = default)
    {
        // Rejections happen before any network call, so nothing leaves the server for a bad request.
        ValidateImageRequest(imageRequest);

        var provider = imageRequest.Provider;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(provider.RequestTimeoutSeconds, 5, 600)));

            var apiKey = _secretProtector.Unprotect(provider.ApiKeyCipherText);
            using var request = BuildImageRequest(imageRequest);
            var client = _httpClientFactory.CreateClient("AiProviders");
            using var response = await client.SendAsync(request, timeout.Token);
            var responseBody = await response.Content.ReadAsStringAsync(timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                stopwatch.Stop();
                return DocumentFailure(provider, stopwatch.ElapsedMilliseconds,
                    $"{(int)response.StatusCode} {response.ReasonPhrase}: {CreateExcerpt(responseBody)}");
            }

            if (provider.ProviderType == AiProviderType.OpenAiResponsesBackground)
            {
                responseBody = await PollOpenAiBackgroundResponseAsync(
                    client,
                    request.RequestUri,
                    apiKey,
                    responseBody,
                    timeout.Token);
            }

            stopwatch.Stop();
            var extracted = ExtractResponse(provider.ProviderType, responseBody);
            return new DocumentPromptResult
            {
                ProviderId = provider.Id,
                ProviderName = provider.DisplayName,
                ResponseText = extracted.Text,
                FinishReason = extracted.FinishReason,
                InputTokens = extracted.InputTokens,
                OutputTokens = extracted.OutputTokens,
                DurationMs = ToInt32(stopwatch.ElapsedMilliseconds)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            throw;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return DocumentFailure(provider, stopwatch.ElapsedMilliseconds, "The provider request timed out.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            // The exception text can echo request content, so only the provider id is logged here.
            _logger.LogWarning(ex, "AI image provider {ProviderId} failed.", provider.Id);
            return DocumentFailure(provider, stopwatch.ElapsedMilliseconds, ex.Message);
        }
    }

    private static void ValidateImageRequest(ImagePromptRequest request)
    {
        var provider = request.Provider;

        if (!provider.SupportsVision)
            throw new ImagePromptRejectedException($"Provider '{provider.DisplayName}' is not enabled for vision (image input).");

        if (provider.ProviderType is AiProviderType.OpenAiBatchResponses or AiProviderType.AnthropicMessageBatch)
            throw new ImagePromptRejectedException($"Provider type {provider.ProviderType} has no synchronous image route.");

        if (string.IsNullOrWhiteSpace(request.Directive))
            throw new ImagePromptRejectedException("An image prompt needs a directive.");

        if (request.Images.Count == 0)
            throw new ImagePromptRejectedException("At least one image is required.");

        if (request.Images.Count > ImagePromptLimits.MaxImagesPerRequest)
            throw new ImagePromptRejectedException(
                $"An image prompt may carry at most {ImagePromptLimits.MaxImagesPerRequest} images, but {request.Images.Count} were supplied.");

        for (var i = 0; i < request.Images.Count; i++)
        {
            var image = request.Images[i];
            if (image.Bytes is null || image.Bytes.Length == 0)
                throw new ImagePromptRejectedException($"Image {i + 1} is empty.");
            if (image.Bytes.Length > ImagePromptLimits.MaxImageBytes)
                throw new ImagePromptRejectedException(
                    $"Image {i + 1} is {image.Bytes.Length:N0} bytes, over the {ImagePromptLimits.MaxImageBytes:N0} byte limit.");
            if (string.IsNullOrWhiteSpace(image.MediaType) || !ImagePromptLimits.AllowedMediaTypes.Contains(image.MediaType))
                throw new ImagePromptRejectedException($"Image {i + 1} has unsupported media type '{image.MediaType}'.");
        }
    }

    private HttpRequestMessage BuildImageRequest(ImagePromptRequest imageRequest)
    {
        var provider = imageRequest.Provider;
        var request = new HttpRequestMessage(HttpMethod.Post, provider.EndpointUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        ApplyExtraHeaders(request, provider.ExtraHeadersJson);

        var apiKey = _secretProtector.Unprotect(provider.ApiKeyCipherText);
        static string DataUrl(ImagePromptImage image) => $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Bytes)}";

        switch (provider.ProviderType)
        {
            case AiProviderType.OpenAiResponses:
            case AiProviderType.OpenAiResponsesBackground:
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }

                var responseContent = new List<object>();
                responseContent.AddRange(imageRequest.Images.Select(image => new { type = "input_image", image_url = DataUrl(image) }));
                responseContent.Add(new { type = "input_text", text = imageRequest.Directive });

                var responsesPayload = new Dictionary<string, object?>
                {
                    ["model"] = provider.ModelName,
                    ["input"] = new object[] { new { role = "user", content = responseContent } }
                };
                if (provider.ProviderType == AiProviderType.OpenAiResponsesBackground)
                {
                    responsesPayload["background"] = true;
                    responsesPayload["store"] = true;
                }

                if (provider.MaxOutputTokens is > 0)
                {
                    responsesPayload["max_output_tokens"] = provider.MaxOutputTokens.Value;
                }

                request.Content = JsonContent.Create(responsesPayload);
                break;

            case AiProviderType.Anthropic:
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                }

                request.Headers.TryAddWithoutValidation(
                    "anthropic-version",
                    string.IsNullOrWhiteSpace(provider.ApiVersion) ? "2023-06-01" : provider.ApiVersion);

                var anthropicContent = new List<object>();
                anthropicContent.AddRange(imageRequest.Images.Select(image => new
                {
                    type = "image",
                    source = new
                    {
                        type = "base64",
                        media_type = image.MediaType,
                        data = Convert.ToBase64String(image.Bytes)
                    }
                }));
                anthropicContent.Add(new { type = "text", text = imageRequest.Directive });

                request.Content = JsonContent.Create(new
                {
                    model = provider.ModelName,
                    max_tokens = provider.MaxOutputTokens.GetValueOrDefault(4000),
                    messages = new[] { new { role = "user", content = anthropicContent } }
                });
                break;

            default:
                // OpenAI-compatible chat completions and Azure OpenAI share the image_url content part.
                if (provider.ProviderType == AiProviderType.AzureOpenAi)
                {
                    if (!string.IsNullOrWhiteSpace(apiKey))
                    {
                        request.Headers.TryAddWithoutValidation("api-key", apiKey);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }

                var chatContent = new List<object>();
                chatContent.Add(new { type = "text", text = imageRequest.Directive });
                chatContent.AddRange(imageRequest.Images.Select(image => new
                {
                    type = "image_url",
                    image_url = new { url = DataUrl(image) }
                }));

                var chatPayload = new Dictionary<string, object?>
                {
                    ["model"] = provider.ModelName,
                    ["messages"] = new[] { new { role = "user", content = chatContent } },
                    ["temperature"] = 0.2
                };
                if (provider.MaxOutputTokens is > 0)
                {
                    chatPayload["max_tokens"] = provider.MaxOutputTokens.Value;
                }

                request.Content = JsonContent.Create(chatPayload);
                break;
        }

        return request;
    }

    private HttpRequestMessage BuildRequest(AiProvider provider, string prompt)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, provider.EndpointUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        ApplyExtraHeaders(request, provider.ExtraHeadersJson);

        var apiKey = _secretProtector.Unprotect(provider.ApiKeyCipherText);
        switch (provider.ProviderType)
        {
            case AiProviderType.OpenAiResponses:
            case AiProviderType.OpenAiResponsesBackground:
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }

                var responsesPayload = new Dictionary<string, object?>
                {
                    ["model"] = provider.ModelName,
                    ["input"] = prompt
                };
                if (provider.MaxOutputTokens is > 0)
                {
                    responsesPayload["max_output_tokens"] = provider.MaxOutputTokens.Value;
                }

                request.Content = JsonContent.Create(responsesPayload);
                break;

            case AiProviderType.AzureOpenAi:
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.TryAddWithoutValidation("api-key", apiKey);
                }

                var azurePayload = new Dictionary<string, object?>
                {
                    ["messages"] = new[]
                    {
                        new { role = "user", content = prompt }
                    },
                    ["temperature"] = 0.2
                };
                if (provider.MaxOutputTokens is > 0)
                {
                    azurePayload["max_tokens"] = provider.MaxOutputTokens.Value;
                }

                request.Content = JsonContent.Create(azurePayload);
                break;

            case AiProviderType.Anthropic:
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                }

                request.Headers.TryAddWithoutValidation(
                    "anthropic-version",
                    string.IsNullOrWhiteSpace(provider.ApiVersion) ? "2023-06-01" : provider.ApiVersion);
                request.Content = JsonContent.Create(new
                {
                    model = provider.ModelName,
                    max_tokens = provider.MaxOutputTokens.GetValueOrDefault(4000),
                    messages = new[]
                    {
                        new { role = "user", content = prompt }
                    }
                });
                break;

            default:
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }

                var chatPayload = new Dictionary<string, object?>
                {
                    ["model"] = provider.ModelName,
                    ["messages"] = new[]
                    {
                        new { role = "user", content = prompt }
                    },
                    ["temperature"] = 0.2
                };
                if (provider.MaxOutputTokens is > 0)
                {
                    chatPayload["max_tokens"] = provider.MaxOutputTokens.Value;
                }

                request.Content = JsonContent.Create(chatPayload);
                break;
        }

        return request;
    }

    private HttpRequestMessage BuildDocumentRequest(DocumentPromptRequest documentRequest)
    {
        var provider = documentRequest.Provider;
        var request = new HttpRequestMessage(HttpMethod.Post, provider.EndpointUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        ApplyExtraHeaders(request, provider.ExtraHeadersJson);

        var apiKey = _secretProtector.Unprotect(provider.ApiKeyCipherText);
        var documents = NormalizeDocuments(documentRequest);

        switch (provider.ProviderType)
        {
            case AiProviderType.OpenAiResponses:
            case AiProviderType.OpenAiResponsesBackground:
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }

                var responseContent = new List<object>();
                responseContent.AddRange(documents.Select(document => new
                {
                    type = "input_file",
                    filename = document.FileName,
                    file_data = $"data:{document.MediaType};base64,{Convert.ToBase64String(document.Bytes)}"
                }));
                responseContent.Add(new { type = "input_text", text = documentRequest.Directive });

                var responsesPayload = new Dictionary<string, object?>
                {
                    ["model"] = provider.ModelName,
                    ["input"] = new object[]
                    {
                        new
                        {
                            role = "user",
                            content = responseContent
                        }
                    }
                };
                if (provider.ProviderType == AiProviderType.OpenAiResponsesBackground)
                {
                    responsesPayload["background"] = true;
                    responsesPayload["store"] = true;
                }

                if (provider.MaxOutputTokens is > 0)
                {
                    responsesPayload["max_output_tokens"] = provider.MaxOutputTokens.Value;
                }

                request.Content = JsonContent.Create(responsesPayload);
                break;

            case AiProviderType.Anthropic:
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                }

                request.Headers.TryAddWithoutValidation(
                    "anthropic-version",
                    string.IsNullOrWhiteSpace(provider.ApiVersion) ? "2023-06-01" : provider.ApiVersion);
                var anthropicContent = new List<object>();
                anthropicContent.AddRange(documents.Select(document => new
                {
                    type = "document",
                    source = new
                    {
                        type = "base64",
                        media_type = document.MediaType,
                        data = Convert.ToBase64String(document.Bytes)
                    }
                }));
                anthropicContent.Add(new { type = "text", text = documentRequest.Directive });

                request.Content = JsonContent.Create(new
                {
                    model = provider.ModelName,
                    max_tokens = provider.MaxOutputTokens.GetValueOrDefault(4000),
                    messages = new[]
                    {
                        new
                        {
                            role = "user",
                            content = anthropicContent
                        }
                    }
                });
                break;

            default:
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }

                var fallbackDocuments = string.Join(
                    "\n\n",
                    documents.Select(document => $"File: {document.FileName}\ndata:{document.MediaType};base64,{Convert.ToBase64String(document.Bytes)}"));
                var fallbackPrompt = $"""
                    {documentRequest.Directive}

                    The uploaded PDFs are attached as base64 data URLs. If this provider does not support inline PDFs, return an error explaining that document input is unsupported.

                    {fallbackDocuments}
                    """;
                var fallbackPayload = new Dictionary<string, object?>
                {
                    ["model"] = provider.ModelName,
                    ["messages"] = new[] { new { role = "user", content = fallbackPrompt } },
                    ["temperature"] = 0.2
                };
                if (provider.MaxOutputTokens is > 0)
                {
                    fallbackPayload["max_tokens"] = provider.MaxOutputTokens.Value;
                }

                request.Content = JsonContent.Create(fallbackPayload);
                break;
        }

        return request;
    }

    private static void ApplyExtraHeaders(HttpRequestMessage request, string? extraHeadersJson)
    {
        if (string.IsNullOrWhiteSpace(extraHeadersJson))
        {
            return;
        }

        var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(extraHeadersJson, JsonOptions);
        if (headers is null)
        {
            return;
        }

        foreach (var (key, value) in headers)
        {
            if (!string.IsNullOrWhiteSpace(key) && value is not null)
            {
                request.Headers.TryAddWithoutValidation(key.Trim(), value);
            }
        }
    }

    private static IReadOnlyList<DocumentPromptFile> NormalizeDocuments(DocumentPromptRequest request)
    {
        if (request.Documents.Count > 0)
        {
            return request.Documents;
        }

        if (request.DocumentBytes is not null
            && !string.IsNullOrWhiteSpace(request.FileName)
            && !string.IsNullOrWhiteSpace(request.MediaType))
        {
            return
            [
                new DocumentPromptFile
                {
                    Bytes = request.DocumentBytes,
                    FileName = request.FileName,
                    MediaType = request.MediaType
                }
            ];
        }

        throw new InvalidOperationException("At least one PDF document is required for document prompts.");
    }

    private async Task<string> PollOpenAiBackgroundResponseAsync(
        HttpClient client,
        Uri? createUri,
        string? apiKey,
        string initialResponseBody,
        CancellationToken cancellationToken)
    {
        var responseId = ExtractOpenAiResponseId(initialResponseBody)
            ?? throw new InvalidOperationException("OpenAI did not return a response id for the background response.");
        var retrieveUri = BuildOpenAiResponseUri(createUri, responseId, null);
        var cancelUri = BuildOpenAiResponseUri(createUri, responseId, "cancel");
        var latestBody = initialResponseBody;

        try
        {
            while (true)
            {
                var status = ExtractOpenAiResponseStatus(latestBody);
                if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    return latestBody;
                }

                if (!string.IsNullOrWhiteSpace(status)
                    && OpenAiResponsesTerminalStatuses.Contains(status)
                    && !OpenAiResponsesIncompleteStatuses.Contains(status))
                {
                    throw new InvalidOperationException($"OpenAI background response {responseId} ended with status {status}: {CreateExcerpt(latestBody)}");
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                using var pollRequest = new HttpRequestMessage(HttpMethod.Get, retrieveUri);
                pollRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    pollRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }

                using var pollResponse = await client.SendAsync(pollRequest, cancellationToken);
                latestBody = await pollResponse.Content.ReadAsStringAsync(cancellationToken);
                if (!pollResponse.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"{(int)pollResponse.StatusCode} {pollResponse.ReasonPhrase}: {CreateExcerpt(latestBody)}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            await TryCancelOpenAiBackgroundResponseAsync(client, cancelUri, apiKey);
            throw;
        }
    }

    private static async Task TryCancelOpenAiBackgroundResponseAsync(HttpClient client, Uri cancelUri, string? apiKey)
    {
        try
        {
            using var cancelRequest = new HttpRequestMessage(HttpMethod.Post, cancelUri);
            cancelRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                cancelRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var _ = await client.SendAsync(cancelRequest, timeout.Token);
        }
        catch
        {
            // Best-effort provider cancellation; the local job still transitions to Cancelled.
        }
    }

    private static Uri BuildOpenAiResponseUri(Uri? createUri, string responseId, string? suffix)
    {
        if (createUri is null)
        {
            throw new InvalidOperationException("The OpenAI Responses endpoint URL is not configured.");
        }

        var baseUri = createUri.IsAbsoluteUri
            ? createUri
            : new Uri(new Uri("https://api.openai.com"), createUri);
        var root = baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var suffixPart = string.IsNullOrWhiteSpace(suffix) ? string.Empty : $"/{suffix.Trim('/')}";
        return new Uri($"{root}/{Uri.EscapeDataString(responseId)}{suffixPart}");
    }

    private static string? ExtractOpenAiResponseId(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        return document.RootElement.TryGetProperty("id", out var id)
            ? JsonElementToText(id)
            : null;
    }

    private static string? ExtractOpenAiResponseStatus(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        return document.RootElement.TryGetProperty("status", out var status)
            ? JsonElementToText(status)
            : null;
    }

    private static ExtractedProviderResponse ExtractResponse(AiProviderType providerType, string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        var text = ExtractResponseText(providerType, root, responseBody);

        return new ExtractedProviderResponse(
            text,
            ExtractFinishReason(providerType, root),
            ExtractInputTokenCount(root),
            ExtractOutputTokenCount(root));
    }

    private static string ExtractResponseText(AiProviderType providerType, JsonElement root, string responseBody)
    {
        if (providerType == AiProviderType.Anthropic
            && root.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var text))
                {
                    builder.AppendLine(text.GetString());
                }
            }

            var anthropicText = builder.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(anthropicText))
            {
                return anthropicText;
            }
        }

        if (root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];
            if (firstChoice.TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var content2))
            {
                return JsonElementToText(content2);
            }

            if (firstChoice.TryGetProperty("text", out var text))
            {
                return JsonElementToText(text);
            }
        }

        if (root.TryGetProperty("output_text", out var outputText))
        {
            return JsonElementToText(outputText);
        }

        if (IsOpenAiResponsesProvider(providerType)
            && root.TryGetProperty("output", out var output)
            && output.ValueKind == JsonValueKind.Array)
        {
            var responseText = ExtractOpenAiResponseOutputText(output);
            if (!string.IsNullOrWhiteSpace(responseText))
            {
                return responseText;
            }
        }

        if (root.TryGetProperty("text", out var rootText))
        {
            return JsonElementToText(rootText);
        }

        return responseBody;
    }

    private static string? ExtractFinishReason(AiProviderType providerType, JsonElement root)
    {
        var reasons = new List<string>();

        if (providerType == AiProviderType.Anthropic
            && root.TryGetProperty("stop_reason", out var anthropicStopReason))
        {
            AddReason(reasons, JsonElementToText(anthropicStopReason));
        }

        if (root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("finish_reason", out var finishReason))
        {
            AddReason(reasons, JsonElementToText(finishReason));
        }

        if (IsOpenAiResponsesProvider(providerType))
        {
            if (root.TryGetProperty("incomplete_details", out var incompleteDetails)
                && incompleteDetails.ValueKind == JsonValueKind.Object
                && incompleteDetails.TryGetProperty("reason", out var incompleteReason))
            {
                AddReason(reasons, $"incomplete: {JsonElementToText(incompleteReason)}");
            }
            else if (root.TryGetProperty("status", out var status))
            {
                AddReason(reasons, JsonElementToText(status));
            }
        }

        if (root.TryGetProperty("candidates", out var candidates)
            && candidates.ValueKind == JsonValueKind.Array
            && candidates.GetArrayLength() > 0
            && candidates[0].TryGetProperty("finishReason", out var geminiFinishReason))
        {
            AddReason(reasons, JsonElementToText(geminiFinishReason));
        }

        return reasons.Count == 0
            ? null
            : string.Join("; ", reasons.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static int? ExtractInputTokenCount(JsonElement root)
    {
        if (root.TryGetProperty("usage", out var usage))
        {
            return TryGetInt32(usage, "input_tokens")
                ?? TryGetInt32(usage, "prompt_tokens");
        }

        if (root.TryGetProperty("usageMetadata", out var usageMetadata))
        {
            return TryGetInt32(usageMetadata, "promptTokenCount");
        }

        return null;
    }

    private static int? ExtractOutputTokenCount(JsonElement root)
    {
        if (root.TryGetProperty("usage", out var usage))
        {
            return TryGetInt32(usage, "output_tokens")
                ?? TryGetInt32(usage, "completion_tokens");
        }

        if (root.TryGetProperty("usageMetadata", out var usageMetadata))
        {
            return TryGetInt32(usageMetadata, "candidatesTokenCount");
        }

        return null;
    }

    private static string ExtractOpenAiResponseOutputText(JsonElement output)
    {
        var builder = new StringBuilder();

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var contentItem in content.EnumerateArray())
            {
                if (contentItem.TryGetProperty("text", out var text))
                {
                    builder.AppendLine(JsonElementToText(text));
                }
            }
        }

        return builder.ToString().Trim();
    }

    private static string JsonElementToText(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : element.GetRawText();
    }

    private static int? TryGetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String
            && int.TryParse(property.GetString(), out var stringNumber)
                ? stringNumber
                : null;
    }

    private static void AddReason(List<string> reasons, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            reasons.Add(value.Trim());
        }
    }

    private static bool IsOpenAiResponsesProvider(AiProviderType providerType)
    {
        return providerType == AiProviderType.OpenAiResponses
            || providerType == AiProviderType.OpenAiResponsesBackground;
    }

    private static AiToolCallResult Failure(AiProvider provider, long elapsedMilliseconds, string error)
    {
        return new AiToolCallResult
        {
            ProviderId = provider.Id,
            ProviderName = provider.DisplayName,
            ErrorMessage = error,
            DurationMs = ToInt32(elapsedMilliseconds)
        };
    }

    private static DocumentPromptResult DocumentFailure(AiProvider provider, long elapsedMilliseconds, string error)
    {
        return new DocumentPromptResult
        {
            ProviderId = provider.Id,
            ProviderName = provider.DisplayName,
            ErrorMessage = error,
            DurationMs = ToInt32(elapsedMilliseconds)
        };
    }

    private static string CreateExcerpt(string value)
    {
        const int maxLength = 800;
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static int ToInt32(long value)
    {
        return value > int.MaxValue ? int.MaxValue : (int)value;
    }

    private sealed record ExtractedProviderResponse(
        string Text,
        string? FinishReason,
        int? InputTokens,
        int? OutputTokens);
}
