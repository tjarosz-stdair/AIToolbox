using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace AIToolbox.Web.Security;

public interface IExternalUserClaimsEnricher
{
    Task EnrichAsync(
        ClaimsPrincipal principal,
        HttpContext httpContext,
        bool recordLogin = false,
        CancellationToken cancellationToken = default);
}
