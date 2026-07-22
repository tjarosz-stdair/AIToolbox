using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AIToolbox.Web.Models;
using AIToolbox.Web.Security;

namespace AIToolbox.Web.Services;

public sealed class AiBatchProviderClient : IAiBatchProviderClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISecretProtector _secretProtector;
    private readonly ILogger<AiBatchProviderClient> _logger;

    public AiBatchProviderClient(
        IHttpClientFactory httpClientFactory,
        ISecretProtector secretProtector,
        ILogger<AiBatchProviderClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _secretProtector = secretProtector;
        _logger = logger;
    }

    public async Task<AiBatchJob> SubmitAsync(
        AiProvider provider,
        AiBatchJob job,
        IReadOnlyList<AiBatchRequest> requests,
        CancellationToken cancellationToken = default)
    {
        return provider.ProviderType switch
        {
            AiProviderType.OpenAiBatchResponses => await SubmitOpenAiAsync(provider, job, requests, cancellationToken),
            AiProviderType.AnthropicMessageBatch => await SubmitAnthropicAsync(provider, job, requests, cancellationToken),
            _ => throw new InvalidOperationException("The selected provider is not a batch provider.")
        };
    }

    public async Task<AiBatchPollResult> PollAsync(
        AiProvider provider,
        AiBatchJob job,
        IReadOnlyList<AiBatchRequest> requests,
        CancellationToken cancellationToken = default)
    {
        return provider.ProviderType switch
        {
            AiProviderType.OpenAiBatchResponses => await PollOpenAiAsync(provider, job, requests, cancellationToken),
            AiProviderType.AnthropicMessageBatch => await PollAnthropicAsync(provider, job, requests, cancellationToken),
            _ => throw new InvalidOperationException("The selected provider is not a batch provider.")
        };
    }

    public async Task<AiBatchJob> CancelAsync(AiProvider provider, AiBatchJob job, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(job.ProviderBatchId))
        {
            job.Status = AiBatchStatus.Cancelled;
            job.CompletedUtc = DateTime.UtcNow;
            return job;
        }

        var client = _httpClientFactory.CreateClient("AiProviders");
        var apiKey = _secretProtector.Unprotect(provider.ApiKeyCipherText);
        var uri = provider.ProviderType == AiProviderType.OpenAiBatchResponses
            ? new Uri($"{GetOpenAiRoot(provider.EndpointUrl)}/batches/{Uri.EscapeDataString(job.ProviderBatchId)}/cancel")
            : new Uri($"{provider.EndpointUrl.TrimEnd('/')}/{Uri.EscapeDataString(job.ProviderBatchId)}/cancel");

        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        ApplyAuth(request, provider, apiKey);
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}: {CreateExcerpt(body)}");
        }

        job.Status = AiBatchStatus.Cancelling;
        job.ResultJson = body;
        job.LastPolledUtc = DateTime.UtcNow;
        return job;
    }

    private async Task<AiBatchJob> SubmitOpenAiAsync(
        AiProvider provider,
        AiBatchJob job,
        IReadOnlyList<AiBatchRequest> requests,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("AiProviders");
        var apiKey = _secretProtector.Unprotect(provider.ApiKeyCipherText);
        var jsonl = string.Join("\n", requests.Select(request => JsonSerializer.Serialize(new
        {
            custom_id = request.CustomId,
            method = "POST",
            url = "/v1/responses",
            body = JsonSerializer.Deserialize<JsonElement>(request.RequestJson)
        }, JsonOptions)));

        using var upload = new HttpRequestMessage(HttpMethod.Post, $"{GetOpenAiRoot(provider.EndpointUrl)}/files");
        ApplyAuth(upload, provider, apiKey);
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("batch"), "purpose");
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(jsonl))
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/jsonl") }
        }, "file", $"aitoolbox-batch-{job.Id}.jsonl");
        upload.Content = content;
        using var uploadResponse = await client.SendAsync(upload, cancellationToken);
        var uploadBody = await uploadResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!uploadResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{(int)uploadResponse.StatusCode} {uploadResponse.ReasonPhrase}: {CreateExcerpt(uploadBody)}");
        }

        var inputFileId = ReadString(uploadBody, "id")
            ?? throw new InvalidOperationException("OpenAI did not return an input file id.");

        using var batch = new HttpRequestMessage(HttpMethod.Post, provider.EndpointUrl);
        ApplyAuth(batch, provider, apiKey);
        batch.Content = JsonContent.Create(new
        {
            input_file_id = inputFileId,
            endpoint = "/v1/responses",
            completion_window = "24h",
            metadata = new Dictionary<string, string>
            {
                ["aitoolbox_job_id"] = job.Id.ToString(),
                ["tool_key"] = job.ToolKey
            }
        });
        using var batchResponse = await client.SendAsync(batch, cancellationToken);
        var batchBody = await batchResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!batchResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{(int)batchResponse.StatusCode} {batchResponse.ReasonPhrase}: {CreateExcerpt(batchBody)}");
        }

        ApplyOpenAiBatchState(job, batchBody);
        job.ProviderInputFileId = inputFileId;
        job.SubmittedUtc = DateTime.UtcNow;
        job.Status = AiBatchStatus.WaitingOnProvider;
        job.ResultJson = batchBody;
        return job;
    }

    private async Task<AiBatchJob> SubmitAnthropicAsync(
        AiProvider provider,
        AiBatchJob job,
        IReadOnlyList<AiBatchRequest> requests,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("AiProviders");
        var apiKey = _secretProtector.Unprotect(provider.ApiKeyCipherText);
        using var request = new HttpRequestMessage(HttpMethod.Post, provider.EndpointUrl);
        ApplyAuth(request, provider, apiKey);
        request.Content = JsonContent.Create(new
        {
            requests = requests.Select(batchRequest => new
            {
                custom_id = batchRequest.CustomId,
                @params = JsonSerializer.Deserialize<JsonElement>(batchRequest.RequestJson)
            })
        });

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}: {CreateExcerpt(body)}");
        }

        ApplyAnthropicBatchState(job, body);
        job.SubmittedUtc = DateTime.UtcNow;
        job.Status = AiBatchStatus.WaitingOnProvider;
        job.ResultJson = body;
        return job;
    }

    private async Task<AiBatchPollResult> PollOpenAiAsync(
        AiProvider provider,
        AiBatchJob job,
        IReadOnlyList<AiBatchRequest> requests,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("AiProviders");
        var apiKey = _secretProtector.Unprotect(provider.ApiKeyCipherText);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{GetOpenAiRoot(provider.EndpointUrl)}/batches/{Uri.EscapeDataString(job.ProviderBatchId ?? string.Empty)}");
        ApplyAuth(request, provider, apiKey);
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}: {CreateExcerpt(body)}");
        }

        ApplyOpenAiBatchState(job, body);
        job.ResultJson = body;
        job.LastPolledUtc = DateTime.UtcNow;
        var status = ReadString(body, "status");
        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase))
        {
            job.Status = string.Equals(status, "cancelling", StringComparison.OrdinalIgnoreCase)
                ? AiBatchStatus.Cancelling
                : AiBatchStatus.WaitingOnProvider;
            return new AiBatchPollResult { Job = job };
        }

        var updated = new List<AiBatchRequest>();
        if (!string.IsNullOrWhiteSpace(job.ProviderOutputFileId))
        {
            updated.AddRange(await ReadOpenAiResultFileAsync(client, apiKey, provider, job.ProviderOutputFileId, requests, false, cancellationToken));
        }

        if (!string.IsNullOrWhiteSpace(job.ProviderErrorFileId))
        {
            updated.AddRange(await ReadOpenAiResultFileAsync(client, apiKey, provider, job.ProviderErrorFileId, requests, true, cancellationToken));
        }

        CompleteJobFromRequests(job, updated.Count > 0 ? updated : requests);
        return new AiBatchPollResult { Job = job, UpdatedRequests = updated };
    }

    private async Task<AiBatchPollResult> PollAnthropicAsync(
        AiProvider provider,
        AiBatchJob job,
        IReadOnlyList<AiBatchRequest> requests,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("AiProviders");
        var apiKey = _secretProtector.Unprotect(provider.ApiKeyCipherText);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{provider.EndpointUrl.TrimEnd('/')}/{Uri.EscapeDataString(job.ProviderBatchId ?? string.Empty)}");
        ApplyAuth(request, provider, apiKey);
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}: {CreateExcerpt(body)}");
        }

        ApplyAnthropicBatchState(job, body);
        job.ResultJson = body;
        job.LastPolledUtc = DateTime.UtcNow;
        if (!string.Equals(ReadString(body, "processing_status"), "ended", StringComparison.OrdinalIgnoreCase))
        {
            job.Status = string.Equals(ReadString(body, "processing_status"), "canceling", StringComparison.OrdinalIgnoreCase)
                ? AiBatchStatus.Cancelling
                : AiBatchStatus.WaitingOnProvider;
            return new AiBatchPollResult { Job = job };
        }

        var updated = string.IsNullOrWhiteSpace(job.ProviderResultsUrl)
            ? []
            : await ReadAnthropicResultsAsync(client, apiKey, provider, job.ProviderResultsUrl, requests, cancellationToken);
        CompleteJobFromRequests(job, updated.Count > 0 ? updated : requests);
        return new AiBatchPollResult { Job = job, UpdatedRequests = updated };
    }

    private async Task<IReadOnlyList<AiBatchRequest>> ReadOpenAiResultFileAsync(
        HttpClient client,
        string? apiKey,
        AiProvider provider,
        string fileId,
        IReadOnlyList<AiBatchRequest> existing,
        bool isErrorFile,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{GetOpenAiRoot(provider.EndpointUrl)}/files/{Uri.EscapeDataString(fileId)}/content");
        ApplyAuth(request, provider, apiKey);
        using var response = await client.SendAsync(request, cancellationToken);
        var jsonl = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to fetch OpenAI batch result file {FileId}: {Status} {Body}", fileId, response.StatusCode, CreateExcerpt(jsonl));
            return [];
        }

        return ParseJsonlResults(jsonl, existing, isErrorFile);
    }

    private async Task<IReadOnlyList<AiBatchRequest>> ReadAnthropicResultsAsync(
        HttpClient client,
        string? apiKey,
        AiProvider provider,
        string resultsUrl,
        IReadOnlyList<AiBatchRequest> existing,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, resultsUrl);
        ApplyAuth(request, provider, apiKey);
        using var response = await client.SendAsync(request, cancellationToken);
        var jsonl = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to fetch Anthropic batch results {ResultsUrl}: {Status} {Body}", resultsUrl, response.StatusCode, CreateExcerpt(jsonl));
            return [];
        }

        return ParseJsonlResults(jsonl, existing, false);
    }

    private static IReadOnlyList<AiBatchRequest> ParseJsonlResults(
        string jsonl,
        IReadOnlyList<AiBatchRequest> existing,
        bool isErrorFile)
    {
        var byCustomId = existing.ToDictionary(request => request.CustomId, StringComparer.OrdinalIgnoreCase);
        var updated = new List<AiBatchRequest>();
        foreach (var line in jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var customId = ReadString(root, "custom_id");
            if (string.IsNullOrWhiteSpace(customId) || !byCustomId.TryGetValue(customId, out var request))
            {
                continue;
            }

            var clone = new AiBatchRequest
            {
                Id = request.Id,
                AiBatchJobId = request.AiBatchJobId,
                CustomId = request.CustomId,
                SequenceNumber = request.SequenceNumber,
                SourceObjectType = request.SourceObjectType,
                SourceObjectId = request.SourceObjectId,
                RequestJson = request.RequestJson,
                CompletedUtc = DateTime.UtcNow
            };

            if (isErrorFile || root.TryGetProperty("error", out var error) && error.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                clone.Status = AiBatchStatus.Failed;
                clone.ErrorJson = line;
            }
            else if (root.TryGetProperty("result", out var result)
                && result.TryGetProperty("type", out var resultType)
                && !string.Equals(resultType.GetString(), "succeeded", StringComparison.OrdinalIgnoreCase))
            {
                clone.Status = MapRequestFailureStatus(resultType.GetString());
                clone.ErrorJson = line;
            }
            else
            {
                clone.Status = AiBatchStatus.Succeeded;
                clone.ResponseJson = line;
                clone.ProviderRequestId = TryReadProviderRequestId(root);
                clone.InputTokens = TryReadToken(root, "input_tokens") ?? TryReadToken(root, "prompt_tokens");
                clone.OutputTokens = TryReadToken(root, "output_tokens") ?? TryReadToken(root, "completion_tokens");
            }

            updated.Add(clone);
        }

        return updated;
    }

    private static void CompleteJobFromRequests(AiBatchJob job, IReadOnlyList<AiBatchRequest> requests)
    {
        job.SucceededRequestCount = requests.Count(request => string.Equals(request.Status, AiBatchStatus.Succeeded, StringComparison.OrdinalIgnoreCase));
        job.FailedRequestCount = requests.Count(request => string.Equals(request.Status, AiBatchStatus.Failed, StringComparison.OrdinalIgnoreCase));
        job.CanceledRequestCount = requests.Count(request => string.Equals(request.Status, AiBatchStatus.Cancelled, StringComparison.OrdinalIgnoreCase));
        job.ExpiredRequestCount = requests.Count(request => string.Equals(request.Status, AiBatchStatus.Expired, StringComparison.OrdinalIgnoreCase));
        job.CompletedUtc = DateTime.UtcNow;

        if (job.SucceededRequestCount == job.TotalRequestCount)
        {
            job.Status = AiBatchStatus.Succeeded;
        }
        else if (job.SucceededRequestCount > 0)
        {
            job.Status = AiBatchStatus.PartiallySucceeded;
        }
        else if (job.CanceledRequestCount > 0)
        {
            job.Status = AiBatchStatus.Cancelled;
        }
        else if (job.ExpiredRequestCount > 0)
        {
            job.Status = AiBatchStatus.Expired;
        }
        else
        {
            job.Status = AiBatchStatus.Failed;
        }
    }

    private static void ApplyOpenAiBatchState(AiBatchJob job, string responseBody)
    {
        job.ProviderBatchId = ReadString(responseBody, "id") ?? job.ProviderBatchId;
        job.ProviderInputFileId = ReadString(responseBody, "input_file_id") ?? job.ProviderInputFileId;
        job.ProviderOutputFileId = ReadString(responseBody, "output_file_id") ?? job.ProviderOutputFileId;
        job.ProviderErrorFileId = ReadString(responseBody, "error_file_id") ?? job.ProviderErrorFileId;
        job.ExpiresUtc = FromUnix(ReadInt(responseBody, "expires_at")) ?? job.ExpiresUtc;
        ApplyCounts(job, responseBody, "request_counts", "completed", "failed", null, null);
    }

    private static void ApplyAnthropicBatchState(AiBatchJob job, string responseBody)
    {
        job.ProviderBatchId = ReadString(responseBody, "id") ?? job.ProviderBatchId;
        job.ProviderResultsUrl = ReadString(responseBody, "results_url") ?? job.ProviderResultsUrl;
        job.ExpiresUtc = ReadDate(responseBody, "expires_at") ?? job.ExpiresUtc;
        ApplyCounts(job, responseBody, "request_counts", "succeeded", "errored", "canceled", "expired");
    }

    private static void ApplyCounts(AiBatchJob job, string responseBody, string countsName, string succeededName, string failedName, string? canceledName, string? expiredName)
    {
        using var document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty(countsName, out var counts))
        {
            return;
        }

        job.TotalRequestCount = TryGetInt(counts, "total") ?? job.TotalRequestCount;
        job.SucceededRequestCount = TryGetInt(counts, succeededName) ?? job.SucceededRequestCount;
        job.FailedRequestCount = TryGetInt(counts, failedName) ?? job.FailedRequestCount;
        if (canceledName is not null)
        {
            job.CanceledRequestCount = TryGetInt(counts, canceledName) ?? job.CanceledRequestCount;
        }

        if (expiredName is not null)
        {
            job.ExpiredRequestCount = TryGetInt(counts, expiredName) ?? job.ExpiredRequestCount;
        }
    }

    private static void ApplyAuth(HttpRequestMessage request, AiProvider provider, string? apiKey)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (provider.ProviderType == AiProviderType.AnthropicMessageBatch)
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            }

            request.Headers.TryAddWithoutValidation(
                "anthropic-version",
                string.IsNullOrWhiteSpace(provider.ApiVersion) ? "2023-06-01" : provider.ApiVersion);
            return;
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    private static string GetOpenAiRoot(string endpointUrl)
    {
        var uri = new Uri(endpointUrl);
        return $"{uri.Scheme}://{uri.Authority}/v1";
    }

    private static string? ReadString(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        return ReadString(document.RootElement, name);
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static int? ReadInt(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        return TryGetInt(document.RootElement, name);
    }

    private static DateTime? ReadDate(string json, string name)
    {
        var value = ReadString(json, name);
        return DateTime.TryParse(value, out var date) ? date.ToUniversalTime() : null;
    }

    private static DateTime? FromUnix(int? seconds)
    {
        return seconds is null ? null : DateTimeOffset.FromUnixTimeSeconds(seconds.Value).UtcDateTime;
    }

    private static int? TryGetInt(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var value)
                ? value
                : null;
    }

    private static string MapRequestFailureStatus(string? resultType)
    {
        return resultType?.ToLowerInvariant() switch
        {
            "canceled" or "cancelled" => AiBatchStatus.Cancelled,
            "expired" => AiBatchStatus.Expired,
            _ => AiBatchStatus.Failed
        };
    }

    private static string? TryReadProviderRequestId(JsonElement root)
    {
        if (root.TryGetProperty("response", out var response)
            && response.TryGetProperty("request_id", out var requestId))
        {
            return requestId.GetString();
        }

        if (root.TryGetProperty("id", out var id))
        {
            return id.GetString();
        }

        return null;
    }

    private static int? TryReadToken(JsonElement root, string tokenName)
    {
        if (root.TryGetProperty("response", out var response)
            && response.TryGetProperty("body", out var body)
            && body.TryGetProperty("usage", out var usage))
        {
            return TryGetInt(usage, tokenName);
        }

        if (root.TryGetProperty("result", out var result)
            && result.TryGetProperty("message", out var message)
            && message.TryGetProperty("usage", out var anthropicUsage))
        {
            return TryGetInt(anthropicUsage, tokenName);
        }

        return null;
    }

    private static string CreateExcerpt(string value)
    {
        return value.Length <= 800 ? value : value[..800];
    }
}
