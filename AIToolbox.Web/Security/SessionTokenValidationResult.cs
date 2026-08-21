namespace AIToolbox.Web.Security;

/// <summary>
/// Mirrors the shape of P21 Toolbox's own SessionTokenValidationResult (WebApp/SessionTokens) for
/// consistency across the two independent implementations, per the token contract.
/// </summary>
public sealed class SessionTokenValidationResult
{
    /// <summary>True whenever a token was presented at all, valid or not.</summary>
    public bool Present { get; private init; }

    /// <summary>True only if the token cryptographically and structurally checks out (signature,
    /// alg, kid, iss, aud, lifetime). Does NOT depend on auth_method — that's fail-safe, tracked
    /// separately via <see cref="IsAuthMethodRecognized"/>.</summary>
    public bool IsValid { get; private init; }

    /// <summary>The verified subject (P21 Toolbox identity user id) — never the same value as
    /// <see cref="Upn"/>, and never conflate the two.</summary>
    public string? Sub { get; private init; }

    public string? Upn { get; private init; }

    public string? Jti { get; private init; }

    public string? TenantId { get; private init; }

    public string? Surface { get; private init; }

    public string? SessionId { get; private init; }

    /// <summary>The raw auth_method claim value, whatever it was — even when unrecognized.</summary>
    public string? AuthMethod { get; private init; }

    /// <summary>Fail-safe: false means the session must be treated as anonymous/degraded, not that
    /// the request should be rejected.</summary>
    public bool IsAuthMethodRecognized { get; private init; }

    public string? RejectReason { get; private init; }

    public static SessionTokenValidationResult Absent() => new() { Present = false, IsValid = false };

    public static SessionTokenValidationResult Rejected(string reason) => new()
    {
        Present = true,
        IsValid = false,
        RejectReason = reason
    };

    public static SessionTokenValidationResult Accepted(
        string? sub,
        string? upn,
        string? jti,
        string? tenantId,
        string? surface,
        string? sessionId,
        string? authMethod,
        bool isAuthMethodRecognized)
    {
        return new SessionTokenValidationResult
        {
            Present = true,
            IsValid = true,
            Sub = sub,
            Upn = upn,
            Jti = jti,
            TenantId = tenantId,
            Surface = surface,
            SessionId = sessionId,
            AuthMethod = authMethod,
            IsAuthMethodRecognized = isAuthMethodRecognized
        };
    }
}
