using System.Net.Http.Json;
using System.Text.Json;
using AIToolbox.Web.Data;
using AIToolbox.Web.Models;
using AIToolbox.Web.Security;

namespace AIToolbox.Web.Services.CallEntityExtraction;

public sealed record AgentAiSettingsDto(
    string AgentKey,
    bool ShowAiPrompts,
    bool IngestionOverrideActive,
    DateTime? IngestionOverrideExpiresAtUtc,
    bool DeliveryOverrideActive,
    DateTime? DeliveryOverrideExpiresAtUtc,
    string? DeliveryMirrorAgentKey);

/// <summary>
/// Admin-only counterpart to CallDataApiNotifier, used exclusively by the Admin Call Monitor
/// page's testing-override panel. Reads the same CallDataApi ExternalResource row (base URL +
/// shared X-API-Key) — no new credential/config surface. Unlike CallDataApiNotifier this
/// surfaces failures to the caller (BadRequest et al) rather than swallowing them, since these
/// calls are a human waiting on a result, not a fire-and-forget pipeline notification.
/// </summary>
public sealed class CallDataApiAdminClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IExternalResourceRepository _resources;
    private readonly ISecretProtector _secretProtector;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CallDataApiAdminClient> _logger;

    public CallDataApiAdminClient(
        IExternalResourceRepository resources,
        ISecretProtector secretProtector,
        IHttpClientFactory httpClientFactory,
        ILogger<CallDataApiAdminClient> logger)
    {
        _resources = resources;
        _secretProtector = secretProtector;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Task<AgentAiSettingsDto?> GetAgentAiSettingsAsync(string agentKey, CancellationToken cancellationToken = default) =>
        SendAsync<AgentAiSettingsDto>(HttpMethod.Get, $"api/agents/{Uri.EscapeDataString(agentKey)}/ai-prompts", null, cancellationToken);

    // Backs the admin panel's "active overrides" side list -- every agent currently running under
    // an ingestion and/or delivery override, without having to know their agentKeys in advance.
    public Task<IReadOnlyList<AgentAiSettingsDto>?> GetActiveOverridesAsync(CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<AgentAiSettingsDto>>(HttpMethod.Get, "api/agents/ai-prompts/active-overrides", null, cancellationToken);

    // These four endpoints return a smaller shape than AgentAiSettingsDto (only the one
    // override's active/expiry fields) -- callers that need the full picture afterward should
    // re-fetch via GetAgentAiSettingsAsync rather than rely on this response.
    public Task<bool> SetIngestionOverrideAsync(string agentKey, int expiresInMinutes, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"api/agents/{Uri.EscapeDataString(agentKey)}/ai-prompts/ingestion-override", new { expiresInMinutes }, cancellationToken);

    public Task<bool> ClearIngestionOverrideAsync(string agentKey, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/agents/{Uri.EscapeDataString(agentKey)}/ai-prompts/ingestion-override", null, cancellationToken);

    public Task<bool> SetDeliveryOverrideAsync(string agentKey, int expiresInMinutes, string? mirrorToAgentKey = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"api/agents/{Uri.EscapeDataString(agentKey)}/ai-prompts/delivery-override", new { expiresInMinutes, mirrorToAgentKey }, cancellationToken);

    public Task<bool> ClearDeliveryOverrideAsync(string agentKey, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/agents/{Uri.EscapeDataString(agentKey)}/ai-prompts/delivery-override", null, cancellationToken);

    private async Task<bool> SendAsync(HttpMethod method, string relativePath, object? body, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(method, relativePath, body, cancellationToken);
        return response is not null;
    }

    private async Task<TResponse?> SendAsync<TResponse>(HttpMethod method, string relativePath, object? body, CancellationToken cancellationToken)
        where TResponse : class
    {
        using var response = await SendCoreAsync(method, relativePath, body, cancellationToken);
        return response is null ? null : await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken);
    }

    private async Task<HttpResponseMessage?> SendCoreAsync(HttpMethod method, string relativePath, object? body, CancellationToken cancellationToken)
    {
        var resource = await _resources.GetEnabledByTypeAsync(ExternalResourceType.CallDataApi, cancellationToken);
        if (resource is null)
        {
            _logger.LogWarning("No enabled CallDataApi external resource configured; cannot call {Path}.", relativePath);
            return null;
        }

        var baseUrl = ParseBaseUrl(resource.ConfigJson);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            _logger.LogWarning("CallDataApi external resource has no valid baseUrl configured; cannot call {Path}.", relativePath);
            return null;
        }

        var credential = _secretProtector.Unprotect(resource.SecretCipherText);

        var client = _httpClientFactory.CreateClient("CallDataApi");
        using var request = new HttpRequestMessage(method, $"{baseUrl.TrimEnd('/')}/{relativePath}");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        if (!string.IsNullOrWhiteSpace(credential))
        {
            request.Headers.Add("X-API-Key", credential);
        }

        var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Call Data API returned {StatusCode} for {Method} {Path}.", (int)response.StatusCode, method, relativePath);
            response.Dispose();
            return null;
        }

        return response;
    }

    private static string? ParseBaseUrl(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(configJson);
            return doc.RootElement.TryGetProperty("baseUrl", out var baseUrlEl) ? baseUrlEl.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
