namespace AIToolbox.Web.Configuration;

/// <summary>
/// Validation config for the P21 Toolbox session token (X-P21-Session-Token). Source of truth
/// for the rules themselves is docs/ai-toolbox-session-token-contract.md in the P21 Toolbox repo.
/// </summary>
public sealed class SessionTokenValidationOptions
{
    public string JwksUrl { get; set; } = "https://app.p21toolbox.com/.well-known/jwks.json";

    public string ExpectedIssuer { get; set; } = "https://app.p21toolbox.com";

    /// <summary>
    /// This instance's own origin, e.g. "https://stdair.ai.p21toolbox.com". Must exactly match
    /// (case-sensitive, no trailing slash) the tenant's dbo.tenant.ai_toolbox_origin_url value on
    /// the P21 Toolbox side, or every session token will silently degrade to anonymous tier.
    /// </summary>
    public string ExpectedAudience { get; set; } = string.Empty;

    /// <summary>
    /// Fail-safe, not fail-closed: an auth_method outside this list degrades the session to
    /// anonymous rather than rejecting the request. Expect this list to grow over time.
    /// </summary>
    public List<string> AllowedAuthMethods { get; set; } = ["m365"];

    public int ClockSkewSeconds { get; set; } = 60;
}
