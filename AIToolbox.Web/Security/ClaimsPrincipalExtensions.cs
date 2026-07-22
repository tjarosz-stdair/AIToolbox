using System.Security.Claims;

namespace AIToolbox.Web.Security;

public static class ClaimsPrincipalExtensions
{
    public static string? GetEmailAddress(this ClaimsPrincipal principal)
    {
        return principal.FindFirstNonEmptyValue(
            ClaimTypes.Email,
            ClaimTypes.Upn,
            "preferred_username",
            "email",
            "upn",
            "unique_name");
    }

    public static string? GetTenantId(this ClaimsPrincipal principal)
    {
        return principal.FindFirstNonEmptyValue(
                "tid",
                "tenant_id",
                "http://schemas.microsoft.com/identity/claims/tenantid")
            ?? GetTenantIdFromIssuer(principal);
    }

    private static string? FindFirstNonEmptyValue(this ClaimsPrincipal principal, params string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = principal.FindFirstValue(claimType);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? GetTenantIdFromIssuer(ClaimsPrincipal principal)
    {
        var issuer = principal.Claims
            .Select(claim => claim.Issuer)
            .FirstOrDefault(value => value.StartsWith("https://login.microsoftonline.com/", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(issuer))
        {
            issuer = principal.FindFirstValue("iss");
        }

        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri))
        {
            return null;
        }

        var segments = issuerUri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return segments.Length > 0 && Guid.TryParse(segments[0], out _)
            ? segments[0]
            : null;
    }

    public static int? GetApiConsumerId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ApiConsumerClaimTypes.ConsumerId);
        return int.TryParse(value, out var consumerId) ? consumerId : null;
    }

    public static bool HasApiScope(this ClaimsPrincipal principal, string scope)
    {
        return principal.Claims
            .Where(claim => string.Equals(claim.Type, ApiConsumerClaimTypes.Scope, StringComparison.OrdinalIgnoreCase))
            .Any(claim => string.Equals(claim.Value, scope, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsApiConsumer(this ClaimsPrincipal principal)
    {
        return principal.GetApiConsumerId() is not null;
    }
}
