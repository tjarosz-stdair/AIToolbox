using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIToolbox.Web.Configuration;
using AIToolbox.Web.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AIToolbox.Web.Tests.Security;

/// <summary>
/// Part 6.1's token-validation suite. The two algorithm-confusion tokens (alg:none, HS256 signed
/// with the RSA public key as an HMAC secret) are hand-constructed rather than produced through
/// any legitimate signing path — they're exactly the rows most likely to be marked "covered"
/// without being genuinely tested, per the plan.
/// </summary>
public sealed class SessionTokenValidatorTests
{
    private const string Issuer = "https://app.p21toolbox.com";
    private const string Audience = "https://stdair.ai.p21toolbox.com";
    private const string Kid1 = "test-kid-1";
    private const string Kid2 = "test-kid-2";

    private static readonly RSA Rsa1 = RSA.Create(2048);
    private static readonly RSA Rsa2 = RSA.Create(2048);

    [Fact]
    public async Task ValidToken_IsAccepted()
    {
        var validator = CreateValidator(DefaultKeys());
        var token = CreateSignedToken(Rsa1, Kid1);

        var result = await validator.ValidateAsync(token);

        Assert.True(result.Present);
        Assert.True(result.IsValid);
        Assert.Equal("user-sub-123", result.Sub);
        Assert.Equal("user@example.com", result.Upn);
        Assert.Equal("m365", result.AuthMethod);
        Assert.True(result.IsAuthMethodRecognized);
    }

    [Fact]
    public async Task MissingRawToken_ReturnsAbsent()
    {
        var validator = CreateValidator(DefaultKeys());

        var result = await validator.ValidateAsync(null);

        Assert.False(result.Present);
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task ExpiredToken_IsRejected()
    {
        var validator = CreateValidator(DefaultKeys());
        var token = CreateSignedToken(Rsa1, Kid1, expires: DateTime.UtcNow.AddMinutes(-10));

        var result = await validator.ValidateAsync(token);

        Assert.True(result.Present);
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task WrongSignature_IsRejected()
    {
        var validator = CreateValidator(DefaultKeys());
        // Header claims kid1 (whose published public key is Rsa1's), but the token is actually
        // signed with Rsa2's private key — signature verification against Rsa1's public key must fail.
        var token = CreateSignedToken(Rsa2, Kid1);

        var result = await validator.ValidateAsync(token);

        Assert.True(result.Present);
        Assert.False(result.IsValid);
        Assert.Equal("validation_failed", result.RejectReason);
    }

    [Fact]
    public async Task AlgNone_IsRejectedBeforeAnySignatureCheck()
    {
        var validator = CreateValidator(DefaultKeys());
        var token = CreateAlgNoneToken();

        var result = await validator.ValidateAsync(token);

        Assert.True(result.Present);
        Assert.False(result.IsValid);
        Assert.Equal("unsupported_alg", result.RejectReason);
    }

    [Fact]
    public async Task Hs256SignedWithRsaPublicKeyAsHmacSecret_IsRejected()
    {
        var validator = CreateValidator(DefaultKeys());
        // The classic RS256-to-HS256 downgrade: HMAC-sign using the RSA public key's exported
        // bytes as the "shared secret" — those bytes are public by definition, so this must not
        // validate, regardless of what the resulting "signature" looks like.
        var token = CreateHs256ConfusionToken(Rsa1, Kid1);

        var result = await validator.ValidateAsync(token);

        Assert.True(result.Present);
        Assert.False(result.IsValid);
        Assert.Equal("unsupported_alg", result.RejectReason);
    }

    [Fact]
    public async Task UnknownKid_IsRejectedWithoutTryingOtherKeys()
    {
        var validator = CreateValidator(DefaultKeys());
        var token = CreateSignedToken(Rsa1, "kid-not-in-jwks");

        var result = await validator.ValidateAsync(token);

        Assert.True(result.Present);
        Assert.False(result.IsValid);
        Assert.Equal("unknown_kid", result.RejectReason);
    }

    [Fact]
    public async Task WrongIssuer_IsRejected()
    {
        var validator = CreateValidator(DefaultKeys());
        var token = CreateSignedToken(Rsa1, Kid1, issuer: "https://not-p21toolbox.example.com");

        var result = await validator.ValidateAsync(token);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task WrongAudience_IsRejected()
    {
        var validator = CreateValidator(DefaultKeys());
        var token = CreateSignedToken(Rsa1, Kid1, audience: "https://a-different-tenant.ai.p21toolbox.com");

        var result = await validator.ValidateAsync(token);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task MissingAuthMethod_DegradesWithoutRejecting()
    {
        var validator = CreateValidator(DefaultKeys());
        var token = CreateSignedToken(Rsa1, Kid1, mutateClaims: claims => claims.Remove("auth_method"));

        var result = await validator.ValidateAsync(token);

        Assert.True(result.IsValid);
        Assert.Null(result.AuthMethod);
        Assert.False(result.IsAuthMethodRecognized);
    }

    [Fact]
    public async Task UnrecognizedAuthMethod_DegradesWithoutRejecting()
    {
        var validator = CreateValidator(DefaultKeys());
        var token = CreateSignedToken(Rsa1, Kid1, mutateClaims: claims => claims["auth_method"] = "future_customer_auth");

        var result = await validator.ValidateAsync(token);

        Assert.True(result.IsValid);
        Assert.Equal("future_customer_auth", result.AuthMethod);
        Assert.False(result.IsAuthMethodRecognized);
    }

    [Fact]
    public async Task RotationWindow_BothKeysAreAcceptedSimultaneously()
    {
        var keys = new Dictionary<string, RsaSecurityKey>
        {
            [Kid1] = new RsaSecurityKey(Rsa1) { KeyId = Kid1 },
            [Kid2] = new RsaSecurityKey(Rsa2) { KeyId = Kid2 }
        };
        var validator = CreateValidator(keys);

        var tokenSignedWithOldKey = CreateSignedToken(Rsa1, Kid1);
        var tokenSignedWithNewKey = CreateSignedToken(Rsa2, Kid2);

        var oldResult = await validator.ValidateAsync(tokenSignedWithOldKey);
        var newResult = await validator.ValidateAsync(tokenSignedWithNewKey);

        Assert.True(oldResult.IsValid);
        Assert.True(newResult.IsValid);
    }

    [Fact]
    public async Task SubAndUpn_AreExtractedFromDistinctClaimsAndNeverAliased()
    {
        var validator = CreateValidator(DefaultKeys());
        var token = CreateSignedToken(Rsa1, Kid1, mutateClaims: claims =>
        {
            claims["sub"] = "identity-user-guid-111";
            claims["upn"] = "someone.else@example.com";
        });

        var result = await validator.ValidateAsync(token);

        Assert.True(result.IsValid);
        Assert.Equal("identity-user-guid-111", result.Sub);
        Assert.Equal("someone.else@example.com", result.Upn);
        Assert.NotEqual(result.Sub, result.Upn);
    }

    private static SessionTokenValidator CreateValidator(Dictionary<string, RsaSecurityKey> keys)
    {
        var options = new SessionTokenValidationOptions
        {
            ExpectedIssuer = Issuer,
            ExpectedAudience = Audience,
            AllowedAuthMethods = ["m365"],
            ClockSkewSeconds = 60
        };
        return new SessionTokenValidator(
            new FakeJwksCache(keys),
            new StaticOptionsMonitor<SessionTokenValidationOptions>(options),
            NullLogger<SessionTokenValidator>.Instance);
    }

    private static Dictionary<string, RsaSecurityKey> DefaultKeys()
    {
        return new Dictionary<string, RsaSecurityKey> { [Kid1] = new RsaSecurityKey(Rsa1) { KeyId = Kid1 } };
    }

    private static string CreateSignedToken(
        RSA signingKey,
        string kid,
        Action<Dictionary<string, object>>? mutateClaims = null,
        DateTime? expires = null,
        string? issuer = null,
        string? audience = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = "user-sub-123",
            ["upn"] = "user@example.com",
            ["tenant_id"] = "tenant-abc",
            ["surface"] = "picsearch",
            ["session_id"] = "session-xyz",
            ["auth_method"] = "m365"
        };
        mutateClaims?.Invoke(claims);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer ?? Issuer,
            Audience = audience ?? Audience,
            IssuedAt = DateTime.UtcNow.AddMinutes(-1),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(30),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(signingKey) { KeyId = kid }, SecurityAlgorithms.RsaSha256),
            Claims = claims
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static string CreateAlgNoneToken()
    {
        var header = new Dictionary<string, object> { ["alg"] = "none", ["typ"] = "JWT" };
        var payload = new Dictionary<string, object>
        {
            ["iss"] = Issuer,
            ["aud"] = Audience,
            ["sub"] = "attacker",
            ["exp"] = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
            ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["auth_method"] = "m365"
        };

        return $"{Base64UrlEncode(JsonSerializer.Serialize(header))}.{Base64UrlEncode(JsonSerializer.Serialize(payload))}.";
    }

    private static string CreateHs256ConfusionToken(RSA publicKeySource, string kid)
    {
        var header = new Dictionary<string, object> { ["alg"] = "HS256", ["typ"] = "JWT", ["kid"] = kid };
        var payload = new Dictionary<string, object>
        {
            ["iss"] = Issuer,
            ["aud"] = Audience,
            ["sub"] = "attacker",
            ["exp"] = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
            ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["auth_method"] = "m365"
        };

        var signingInput = $"{Base64UrlEncode(JsonSerializer.Serialize(header))}.{Base64UrlEncode(JsonSerializer.Serialize(payload))}";
        var confusedSecret = publicKeySource.ExportRSAPublicKey(); // public by definition
        using var hmac = new HMACSHA256(confusedSecret);
        var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(string value) => Base64UrlEncode(Encoding.UTF8.GetBytes(value));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
