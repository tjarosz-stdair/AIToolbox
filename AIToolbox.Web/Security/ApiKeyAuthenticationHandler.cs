using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AIToolbox.Web.Security;

public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiConsumerKey";
    private readonly IApiKeyService _apiKeys;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IApiKeyService apiKeys)
        : base(options, logger, encoder)
    {
        _apiKeys = apiKeys;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var rawKey = ReadApiKey();
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return AuthenticateResult.NoResult();
        }

        var principal = await _apiKeys.ValidateAsync(rawKey, Context.RequestAborted);
        if (principal is null)
        {
            return AuthenticateResult.Fail("The API key is invalid, expired, or revoked.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, $"consumer:{principal.Consumer.Id}"),
            new(ClaimTypes.Name, principal.Consumer.Name),
            new(ClaimTypes.Email, $"consumer:{principal.Consumer.Id}"),
            new(ClaimTypes.Role, "ApiConsumer"),
            new(ApiConsumerClaimTypes.ConsumerId, principal.Consumer.Id.ToString()),
            new(ApiConsumerClaimTypes.ConsumerName, principal.Consumer.Name)
        };
        claims.AddRange(principal.Scopes.Select(scope => new Claim(ApiConsumerClaimTypes.Scope, scope)));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return AuthenticateResult.Success(ticket);
    }

    private string? ReadApiKey()
    {
        if (Request.Headers.TryGetValue("X-AIToolbox-Api-Key", out var headerValue)
            && !string.IsNullOrWhiteSpace(headerValue))
        {
            return headerValue.ToString();
        }

        var authorization = Request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        return authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
            && authorization[bearerPrefix.Length..].StartsWith("aitb_", StringComparison.OrdinalIgnoreCase)
                ? authorization[bearerPrefix.Length..]
                : null;
    }
}
