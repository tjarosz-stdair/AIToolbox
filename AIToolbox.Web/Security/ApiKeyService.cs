using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIToolbox.Web.Data;
using AIToolbox.Web.Models;

namespace AIToolbox.Web.Security;

public sealed class ApiKeyService : IApiKeyService
{
    private const string Prefix = "aitb_live_";
    private const int RandomByteCount = 32;
    private readonly IApiConsumerRepository _consumers;

    public ApiKeyService(IApiConsumerRepository consumers)
    {
        _consumers = consumers;
    }

    public string GenerateRawKey()
    {
        return Prefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(RandomByteCount));
    }

    public string GetPrefix(string rawKey)
    {
        var trimmed = rawKey.Trim();
        return trimmed.Length <= 20 ? trimmed : trimmed[..20];
    }

    public string HashKey(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey.Trim()));
        return Convert.ToHexString(bytes);
    }

    public async Task<ApiConsumerPrincipal?> ValidateAsync(string rawKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return null;
        }

        var key = await _consumers.GetKeyByPrefixAsync(GetPrefix(rawKey), cancellationToken);
        if (key is null || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(HashKey(rawKey)),
                Encoding.ASCII.GetBytes(key.KeyHash)))
        {
            return null;
        }

        var consumer = await _consumers.GetConsumerAsync(key.ApiConsumerId, cancellationToken);
        if (consumer is null || !consumer.IsEnabled)
        {
            return null;
        }

        await _consumers.MarkKeyUsedAsync(key.Id, cancellationToken);
        return new ApiConsumerPrincipal
        {
            Consumer = consumer,
            Key = key,
            Scopes = ParseScopes(key.ScopesJson)
        };
    }

    private static IReadOnlyList<string> ParseScopes(string scopesJson)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(scopesJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
