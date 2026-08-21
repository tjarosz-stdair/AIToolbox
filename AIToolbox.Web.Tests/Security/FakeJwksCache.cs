using AIToolbox.Web.Security;
using Microsoft.IdentityModel.Tokens;

namespace AIToolbox.Web.Tests.Security;

/// <summary>Hands back exactly the key registered for a given kid, or null — mirrors the real
/// JwksCache's "never fall back to trying every key" contract without any HTTP fetching.</summary>
public sealed class FakeJwksCache : IJwksCache
{
    private readonly Dictionary<string, RsaSecurityKey> _keysByKid;

    public FakeJwksCache(Dictionary<string, RsaSecurityKey> keysByKid)
    {
        _keysByKid = keysByKid;
    }

    public Task<RsaSecurityKey?> GetKeyAsync(string kid, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_keysByKid.TryGetValue(kid, out var key) ? key : null);
    }
}
