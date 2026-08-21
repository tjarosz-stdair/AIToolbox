using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using AIToolbox.Web.Data;
using AIToolbox.Web.Models;
using Microsoft.Extensions.AI;

namespace AIToolbox.Web.Services;

/// <summary>
/// Registered as a singleton so the manifest cache (§5.4: honor Cache-Control, 5-minute default)
/// survives across requests, mirroring JwksCache's in-memory-dictionary-with-expiry pattern.
/// </summary>
public sealed class UiCapabilityProvider : IUiCapabilityProvider
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly IMcpClientCredentialRepository _credentials;
    private readonly IMcpAccessTokenProvider _tokenProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<UiCapabilityProvider> _logger;

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public UiCapabilityProvider(
        IMcpClientCredentialRepository credentials,
        IMcpAccessTokenProvider tokenProvider,
        IHttpClientFactory httpClientFactory,
        ILogger<UiCapabilityProvider> logger)
    {
        _credentials = credentials;
        _tokenProvider = tokenProvider;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AITool>> GetToolsAsync(
        string mcpCredentialSurface,
        IReadOnlyList<string> uiSurfaces,
        CancellationToken cancellationToken = default)
    {
        if (uiSurfaces.Count == 0)
        {
            return [];
        }

        var tools = new List<AITool>();
        foreach (var uiSurface in uiSurfaces)
        {
            var capabilities = await GetCapabilitiesAsync(mcpCredentialSurface, uiSurface, cancellationToken);
            tools.AddRange(capabilities.Select(capability => new UiIntentFunction(capability)));
        }

        return tools;
    }

    private async Task<IReadOnlyList<UiCapability>> GetCapabilitiesAsync(
        string mcpCredentialSurface,
        string uiSurface,
        CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(uiSurface, out var cached) && DateTime.UtcNow < cached.ExpiresAtUtc)
        {
            return cached.Capabilities;
        }

        try
        {
            var credential = await _credentials.GetBySurfaceAsync(mcpCredentialSurface, cancellationToken);
            if (credential is null)
            {
                _logger.LogWarning(
                    "No MCP client credential configured for surface '{Surface}'; cannot fetch UI capabilities for '{UiSurface}'.",
                    mcpCredentialSurface,
                    uiSurface);
                return [];
            }

            var accessToken = await _tokenProvider.GetAccessTokenAsync(mcpCredentialSurface, cancellationToken: cancellationToken);
            var baseUri = new Uri(credential.McpEndpointUrl);
            var capabilitiesUri = new Uri(baseUri, $"/api/ui-capabilities/{Uri.EscapeDataString(uiSurface)}");

            using var request = new HttpRequestMessage(HttpMethod.Get, capabilitiesUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var client = _httpClientFactory.CreateClient("McpClient");
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Fetching UI capabilities for surface '{UiSurface}' failed with {StatusCode}. Degrading to MCP-only tools for this surface.",
                    uiSurface,
                    (int)response.StatusCode);
                return [];
            }

            var parsed = ParseAndValidate(uiSurface, body);
            var ttl = response.Headers.CacheControl?.MaxAge ?? DefaultTtl;
            _cache[uiSurface] = new CacheEntry(parsed, DateTime.UtcNow + ttl);
            return parsed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never throws out of here: "degrading to a data-only assistant is correct; failing
            // the conversation is not" (§5.4).
            _logger.LogWarning(ex, "Error fetching UI capabilities for surface '{UiSurface}'. Degrading to MCP-only tools for this surface.", uiSurface);
            return [];
        }
    }

    private List<UiCapability> ParseAndValidate(string uiSurface, string body)
    {
        var results = new List<UiCapability>();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "UI capabilities manifest for surface '{UiSurface}' was not valid JSON; ignoring.", uiSurface);
            return results;
        }

        using (document)
        {
            var root = document.RootElement;
            var array = root.ValueKind == JsonValueKind.Array
                ? root
                : root.TryGetProperty("capabilities", out var nested) ? nested : default;

            if (array.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("UI capabilities manifest for surface '{UiSurface}' was not an array; ignoring.", uiSurface);
                return results;
            }

            foreach (var entry in array.EnumerateArray())
            {
                var name = entry.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                var description = entry.TryGetProperty("description", out var descEl) ? descEl.GetString() : null;
                var kind = entry.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() : null;
                var hasInputSchema = entry.TryGetProperty("inputSchema", out var schemaEl);

                if (string.IsNullOrWhiteSpace(name))
                {
                    _logger.LogWarning("A UI capability for surface '{UiSurface}' has no name; skipping.", uiSurface);
                    continue;
                }

                // Refuse to register one that doesn't carry the ui_ prefix — an unprefixed UI
                // verb would compete with a data tool for the model's attention (§5.4).
                if (!name.StartsWith("ui_", StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "Refusing to register UI capability '{Name}' for surface '{UiSurface}': names must be prefixed 'ui_'.",
                        name,
                        uiSurface);
                    continue;
                }

                // v1 has only "navigation" — a "mutation" capability (or anything else) is never
                // registered without a directive covering its confirmation flow.
                if (!string.Equals(kind, "navigation", StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "Refusing to register UI capability '{Name}' for surface '{UiSurface}': kind is '{Kind}', only 'navigation' is supported in v1.",
                        name,
                        uiSurface,
                        kind ?? "(none)");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(description))
                {
                    _logger.LogWarning("UI capability '{Name}' for surface '{UiSurface}' has no description; registering anyway.", name, uiSurface);
                }
                else if (!description.Contains("no product data", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "UI capability '{Name}' for surface '{UiSurface}' doesn't state that it returns no product data / name the MCP tool that does.",
                        name,
                        uiSurface);
                }

                results.Add(new UiCapability
                {
                    Name = name,
                    Description = description ?? string.Empty,
                    Kind = "navigation", // the only value that survives the check above
                    InputSchema = hasInputSchema ? schemaEl.Clone() : EmptyObjectSchema(),
                    UiSurface = uiSurface
                });
            }
        }

        return results;
    }

    private static JsonElement EmptyObjectSchema()
    {
        using var document = JsonDocument.Parse("""{"type":"object","properties":{}}""");
        return document.RootElement.Clone();
    }

    private sealed record CacheEntry(IReadOnlyList<UiCapability> Capabilities, DateTime ExpiresAtUtc);
}
