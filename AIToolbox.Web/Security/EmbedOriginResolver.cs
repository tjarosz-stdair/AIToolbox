using AIToolbox.Web.Configuration;
using Microsoft.Extensions.Options;

namespace AIToolbox.Web.Security;

public sealed class EmbedOriginResolver : IEmbedOriginResolver
{
    private readonly IOptionsMonitor<EmbedOptions> _options;

    public EmbedOriginResolver(IOptionsMonitor<EmbedOptions> options)
    {
        _options = options;
    }

    public string? Resolve(string? candidateOrigin)
    {
        var configured = _options.CurrentValue.ParentOrigins;
        if (configured.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(candidateOrigin))
        {
            return configured.Count == 1 ? configured[0] : null;
        }

        var normalizedCandidate = Normalize(candidateOrigin);
        if (normalizedCandidate is null)
        {
            return null;
        }

        foreach (var origin in configured)
        {
            if (string.Equals(normalizedCandidate, Normalize(origin), StringComparison.Ordinal))
            {
                return origin;
            }
        }

        return null;
    }

    /// <summary>
    /// An origin is scheme + host + port only. Reject anything carrying a path beyond "/", a
    /// query, or a fragment — accepting those would let a caller smuggle extra data through what
    /// is supposed to be a fixed selector into a server-side allowlist, not a value that flows
    /// through unchecked.
    /// </summary>
    private static string? Normalize(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || (uri.AbsolutePath.Length > 0 && uri.AbsolutePath != "/"))
        {
            return null;
        }

        return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}:{uri.Port}";
    }
}
