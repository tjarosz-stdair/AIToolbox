using AIToolbox.Web.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AIToolbox.Web.Security;

/// <summary>
/// Independent implementation of the same rules as P21 Toolbox's own session-token validator
/// (docs/ai-toolbox-session-token-contract.md is the source of truth) — a different codebase and
/// framework, same rules, deliberately not sharing code.
/// </summary>
public sealed class SessionTokenValidator : ISessionTokenValidator
{
    private static readonly HashSet<string> AllowedAlgorithms = new(StringComparer.Ordinal) { "RS256" };

    private readonly IJwksCache _jwksCache;
    private readonly IOptionsMonitor<SessionTokenValidationOptions> _options;
    private readonly ILogger<SessionTokenValidator> _logger;
    private readonly JsonWebTokenHandler _handler = new();

    public SessionTokenValidator(
        IJwksCache jwksCache,
        IOptionsMonitor<SessionTokenValidationOptions> options,
        ILogger<SessionTokenValidator> logger)
    {
        _jwksCache = jwksCache;
        _options = options;
        _logger = logger;
    }

    public async Task<SessionTokenValidationResult> ValidateAsync(string? rawToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return SessionTokenValidationResult.Absent();
        }

        JsonWebToken jwt;
        try
        {
            jwt = new JsonWebToken(rawToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session token could not be parsed.");
            return SessionTokenValidationResult.Rejected("malformed");
        }

        // Pin the algorithm and check it BEFORE any signature verification runs. Reject "none"
        // outright, and reject anything that isn't exactly RS256 — including HS256, which a
        // vulnerable validator could be tricked into "verifying" by HMAC-ing with the RSA public
        // modulus (itself public) as the shared secret. We also never hold any symmetric key
        // material for this purpose, so that specific attack has no key to succeed against
        // regardless — this check is defense in depth on top of that, not the only defense.
        if (!AllowedAlgorithms.Contains(jwt.Alg))
        {
            return SessionTokenValidationResult.Rejected("unsupported_alg");
        }

        // Reject a missing or unmatched kid outright. Never fall back to trying every key.
        if (string.IsNullOrWhiteSpace(jwt.Kid))
        {
            return SessionTokenValidationResult.Rejected("missing_kid");
        }

        var signingKey = await _jwksCache.GetKeyAsync(jwt.Kid, cancellationToken);
        if (signingKey is null)
        {
            return SessionTokenValidationResult.Rejected("unknown_kid");
        }

        var options = _options.CurrentValue;
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.ExpectedIssuer,
            ValidateAudience = true,
            ValidAudience = options.ExpectedAudience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(options.ClockSkewSeconds),
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidAlgorithms = AllowedAlgorithms
        };

        var result = await _handler.ValidateTokenAsync(jwt, validationParameters);
        if (!result.IsValid)
        {
            _logger.LogInformation(result.Exception, "Session token failed validation.");
            return SessionTokenValidationResult.Rejected("validation_failed");
        }

        var sub = GetClaimValue(jwt, "sub");
        var upn = GetClaimValue(jwt, "upn");
        var jti = GetClaimValue(jwt, "jti");
        var tenantId = GetClaimValue(jwt, "tenant_id");
        var surface = GetClaimValue(jwt, "surface");
        var sessionId = GetClaimValue(jwt, "session_id");
        var authMethod = GetClaimValue(jwt, "auth_method");

        // Fail-safe, not fail-closed: an unrecognized (or missing) auth_method degrades the
        // session to anonymous for the caller to act on — it must never reject the request here.
        var isAuthMethodRecognized = authMethod is not null && options.AllowedAuthMethods.Contains(authMethod);

        return SessionTokenValidationResult.Accepted(
            sub,
            upn,
            jti,
            tenantId,
            surface,
            sessionId,
            authMethod,
            isAuthMethodRecognized);
    }

    private static string? GetClaimValue(JsonWebToken jwt, string claimType)
    {
        return jwt.TryGetClaim(claimType, out var claim) ? claim.Value : null;
    }
}
