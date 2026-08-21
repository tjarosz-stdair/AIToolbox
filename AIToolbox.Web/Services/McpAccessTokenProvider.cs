using System.Collections.Concurrent;
using System.Text.Json;
using AIToolbox.Web.Data;
using AIToolbox.Web.Security;
using Microsoft.Extensions.DependencyInjection;

namespace AIToolbox.Web.Services;

/// <summary>
/// Registered as a singleton so the token cache survives across requests; resolves the (scoped)
/// credential repository and secret protector through a fresh DI scope each time a token
/// actually needs minting, rather than holding a captive scoped dependency.
/// </summary>
public sealed class McpAccessTokenProvider : IMcpAccessTokenProvider
{
    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultTokenLifetime = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<McpAccessTokenProvider> _logger;
    private readonly ConcurrentDictionary<string, CachedToken> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public McpAccessTokenProvider(
        IHttpClientFactory httpClientFactory,
        IServiceScopeFactory scopeFactory,
        ILogger<McpAccessTokenProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<string> GetAccessTokenAsync(string surface, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && _cache.TryGetValue(surface, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow + ExpiryBuffer)
        {
            return cached.AccessToken;
        }

        var gate = _locks.GetOrAdd(surface, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && _cache.TryGetValue(surface, out cached) && cached.ExpiresAtUtc > DateTime.UtcNow + ExpiryBuffer)
            {
                return cached.AccessToken;
            }

            var token = await MintTokenAsync(surface, cancellationToken);
            _cache[surface] = token;
            return token.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CachedToken> MintTokenAsync(string surface, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var credentials = scope.ServiceProvider.GetRequiredService<IMcpClientCredentialRepository>();
        var secretProtector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();

        var credential = await credentials.GetBySurfaceAsync(surface, cancellationToken)
            ?? throw new InvalidOperationException($"No enabled MCP client credential is configured for surface '{surface}'.");

        var clientSecret = secretProtector.Unprotect(credential.ClientSecretCipherText);
        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException($"The MCP client credential for surface '{surface}' has no stored client secret.");
        }

        var formValues = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = credential.ClientId,
            ["client_secret"] = clientSecret
        };
        if (!string.IsNullOrWhiteSpace(credential.GrantedScopes))
        {
            formValues["scope"] = credential.GrantedScopes;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, credential.TokenEndpointUrl)
        {
            Content = new FormUrlEncodedContent(formValues)
        };

        var client = _httpClientFactory.CreateClient("McpClient");
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "MCP token request for surface {Surface} failed with {StatusCode}: {Body}",
                surface,
                (int)response.StatusCode,
                body.Length > 500 ? body[..500] : body);
            throw new InvalidOperationException(
                $"Requesting an MCP access token for surface '{surface}' failed with {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var accessToken = root.TryGetProperty("access_token", out var tokenProperty) ? tokenProperty.GetString() : null;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException($"The token endpoint for surface '{surface}' did not return an access_token.");
        }

        var expiresInSeconds = root.TryGetProperty("expires_in", out var expiresProperty) && expiresProperty.TryGetInt32(out var seconds)
            ? seconds
            : (int?)null;
        var lifetime = expiresInSeconds is > 0 ? TimeSpan.FromSeconds(expiresInSeconds.Value) : DefaultTokenLifetime;

        return new CachedToken(accessToken, DateTime.UtcNow + lifetime);
    }

    private sealed record CachedToken(string AccessToken, DateTime ExpiresAtUtc);
}
