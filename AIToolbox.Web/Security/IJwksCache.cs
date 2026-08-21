using Microsoft.IdentityModel.Tokens;

namespace AIToolbox.Web.Security;

public interface IJwksCache
{
    /// <summary>
    /// Looks up the RSA key for the exact given kid. Never falls back to any other key. Triggers
    /// at most one JWKS refetch (rate-limited) if the kid isn't in the current cache, since it may
    /// simply be newer than our last fetch during a rotation window.
    /// </summary>
    Task<RsaSecurityKey?> GetKeyAsync(string kid, CancellationToken cancellationToken = default);
}
