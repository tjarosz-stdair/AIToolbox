using AIToolbox.Web.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AIToolbox.Web.Security;

/// <summary>
/// Caches P21 Toolbox's JWKS keyed by kid, not "the current key" — during a key rotation the
/// server serves both the old and new key simultaneously for at least one token TTL, and a
/// single-key cache would fail intermittently during that window. Registered as a singleton so
/// the cache and its rate-limiting survive across requests.
/// </summary>
public sealed class JwksCache : IJwksCache
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinRefetchInterval = TimeSpan.FromSeconds(10);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<SessionTokenValidationOptions> _options;
    private readonly ILogger<JwksCache> _logger;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);

    private volatile IReadOnlyDictionary<string, RsaSecurityKey> _keysByKid =
        new Dictionary<string, RsaSecurityKey>();
    private DateTime _cacheExpiresAtUtc = DateTime.MinValue;
    private DateTime _lastFetchAttemptUtc = DateTime.MinValue;

    public JwksCache(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<SessionTokenValidationOptions> options,
        ILogger<JwksCache> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public async Task<RsaSecurityKey?> GetKeyAsync(string kid, CancellationToken cancellationToken = default)
    {
        if (DateTime.UtcNow >= _cacheExpiresAtUtc)
        {
            await RefetchAsync(force: false, cancellationToken);
        }

        if (_keysByKid.TryGetValue(kid, out var key))
        {
            return key;
        }

        // Unknown kid: may simply be newer than our last fetch, mid-rotation. Force one refetch,
        // rate-limited so a flood of bogus kids can't hammer the JWKS endpoint.
        await RefetchAsync(force: true, cancellationToken);
        return _keysByKid.TryGetValue(kid, out key) ? key : null;
    }

    private async Task RefetchAsync(bool force, CancellationToken cancellationToken)
    {
        await _fetchLock.WaitAsync(cancellationToken);
        try
        {
            if (!force && DateTime.UtcNow < _cacheExpiresAtUtc)
            {
                return;
            }

            if (force && DateTime.UtcNow - _lastFetchAttemptUtc < MinRefetchInterval)
            {
                return;
            }

            _lastFetchAttemptUtc = DateTime.UtcNow;

            var jwksUrl = _options.CurrentValue.JwksUrl;
            var client = _httpClientFactory.CreateClient("McpClient");
            using var response = await client.GetAsync(jwksUrl, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Fetching JWKS from {JwksUrl} failed with {StatusCode}.",
                    jwksUrl,
                    (int)response.StatusCode);
                return;
            }

            var keySet = new JsonWebKeySet(body);
            var parsed = new Dictionary<string, RsaSecurityKey>();
            foreach (var jwk in keySet.Keys)
            {
                if (!string.Equals(jwk.Kty, "RSA", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(jwk.Kid))
                {
                    continue;
                }

                if (JsonWebKeyConverter.TryConvertToSecurityKey(jwk, out var securityKey) && securityKey is RsaSecurityKey rsaKey)
                {
                    parsed[jwk.Kid] = rsaKey;
                }
            }

            _keysByKid = parsed;
            _cacheExpiresAtUtc = DateTime.UtcNow + (response.Headers.CacheControl?.MaxAge ?? DefaultTtl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error refreshing JWKS from {JwksUrl}.", _options.CurrentValue.JwksUrl);
        }
        finally
        {
            _fetchLock.Release();
        }
    }
}
