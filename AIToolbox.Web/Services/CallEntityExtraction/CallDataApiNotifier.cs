using System.Net.Http.Json;
using System.Text.Json;
using AIToolbox.Web.Data;
using AIToolbox.Web.Models;
using AIToolbox.Web.Security;

namespace AIToolbox.Web.Services.CallEntityExtraction;

/// <summary>
/// POSTs to the Call Data API's entity-detected endpoint per
/// AIToolbox/TODO_Lists/call-data-api-contract.md. Reads its base URL/credential from the
/// ExternalResource row of type CallDataApi (Admin &gt; External Resources) -- same "configured
/// non-chat bolt-on service" pattern SpeechTranscriptionService uses for Azure Speech, not a new
/// appsettings section, so the credential is encrypted at rest and admin-editable without a
/// redeploy.
///
/// Never throws: no resource configured, an unreachable endpoint, or a non-2xx response are all
/// logged and swallowed -- a missed notification is a real but non-fatal gap here, not a reason to
/// fail the entity-extraction pipeline (see the contract doc's "what this is for" framing).
/// </summary>
public sealed class CallDataApiNotifier : ICallEventNotifier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IExternalResourceRepository _resources;
    private readonly ISecretProtector _secretProtector;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CallDataApiNotifier> _logger;

    public CallDataApiNotifier(
        IExternalResourceRepository resources,
        ISecretProtector secretProtector,
        IHttpClientFactory httpClientFactory,
        ILogger<CallDataApiNotifier> logger)
    {
        _resources = resources;
        _secretProtector = secretProtector;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task NotifyEntityDetectedAsync(EntityDetectedNotification notification, CancellationToken cancellationToken = default)
    {
        try
        {
            var resource = await _resources.GetEnabledByTypeAsync(ExternalResourceType.CallDataApi, cancellationToken);
            if (resource is null)
            {
                _logger.LogDebug("Skipping Call Data API notification for entity {EntityId} -- no CallDataApi external resource configured.", notification.EntityId);
                return;
            }

            var baseUrl = ParseBaseUrl(resource.ConfigJson);
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                _logger.LogWarning("CallDataApi external resource has no valid baseUrl configured; skipping notification for entity {EntityId}.", notification.EntityId);
                return;
            }

            var credential = _secretProtector.Unprotect(resource.SecretCipherText);

            var client = _httpClientFactory.CreateClient("CallDataApi");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/call-events/entity-detected")
            {
                Content = JsonContent.Create(notification, options: JsonOptions)
            };
            // The Hub's ApiKeyMiddleware is a single shared-secret check on X-API-Key -- not the
            // ApiConsumerKey/Bearer scheme AIToolbox's own inbound endpoints use. Standardizing on
            // whichever convention the *receiving* side actually expects.
            if (!string.IsNullOrWhiteSpace(credential))
            {
                request.Headers.Add("X-API-Key", credential);
            }

            var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Call Data API rejected entity-detected notification for {EntityId}: {StatusCode}.",
                    notification.EntityId,
                    (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to notify Call Data API of entity {EntityId}.", notification.EntityId);
        }
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
