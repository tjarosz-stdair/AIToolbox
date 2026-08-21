namespace AIToolbox.Web.Security;

public interface IEmbedOriginResolver
{
    /// <summary>
    /// Resolves a candidate origin string against the configured allowlist. Returns the exact
    /// canonical configured value on a match — never the caller's raw input — or null on no
    /// match. A null/empty candidate resolves to the single configured origin only when exactly
    /// one is configured; with zero or several configured, an absent candidate cannot be resolved.
    /// </summary>
    string? Resolve(string? candidateOrigin);
}
