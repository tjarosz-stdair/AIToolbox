using System.Security.Claims;

namespace AIToolbox.Web.Security;

public interface IAccessControlService
{
    Task<UserAccessResult> ValidateExternalUserAsync(string email, string? tenantId, CancellationToken cancellationToken = default);

    Task<UserAccessResult> GetAccessAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);
}
