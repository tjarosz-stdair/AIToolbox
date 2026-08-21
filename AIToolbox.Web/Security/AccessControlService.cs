using AIToolbox.Web.Configuration;
using AIToolbox.Web.Data;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace AIToolbox.Web.Security;

public sealed class AccessControlService : IAccessControlService
{
    private readonly IAuthorizedUserRepository _users;
    private readonly IOptions<MicrosoftEntraOptions> _options;

    public AccessControlService(
        IAuthorizedUserRepository users,
        IOptions<MicrosoftEntraOptions> options)
    {
        _users = users;
        _options = options;
    }

    public async Task<UserAccessResult> GetAccessAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        var email = principal.GetEmailAddress();
        var tenantId = principal.GetTenantId();

        if (string.IsNullOrWhiteSpace(email))
        {
            return UserAccessResult.Denied("No email claim was present on the signed-in user.", tenantId: tenantId);
        }

        return await ValidateExternalUserAsync(email, tenantId, cancellationToken);
    }

    public async Task<UserAccessResult> ValidateExternalUserAsync(
        string email,
        string? tenantId,
        CancellationToken cancellationToken = default)
    {
        email = email.Trim();
        if (string.IsNullOrWhiteSpace(email))
        {
            return UserAccessResult.Denied("Email is required.", tenantId: tenantId);
        }

        if (!TenantIsAllowed(tenantId))
        {
            return UserAccessResult.Denied("The user's Microsoft 365 tenant is not allowed.", email, tenantId);
        }

        if (!DomainIsAllowed(email))
        {
            return UserAccessResult.Denied("The user's email domain is not allowed.", email, tenantId);
        }

        var bootstrapAdmin = IsBootstrapAdmin(email);
        var user = await _users.GetByEmailAsync(email, cancellationToken);

        if (user is null && !bootstrapAdmin)
        {
            if (!_options.Value.EnableAutoProvisioning)
            {
                return UserAccessResult.Denied("The user has not been granted access.", email, tenantId);
            }

            user = await _users.ProvisionAsync(email, tenantId, cancellationToken);
        }

        if (user is not null && !user.IsEnabled)
        {
            return UserAccessResult.Denied("The user grant is disabled.", email, tenantId);
        }

        if (user is not null
            && !string.IsNullOrWhiteSpace(user.TenantId)
            && !string.Equals(user.TenantId.Trim(), tenantId?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return UserAccessResult.Denied("The user grant is tied to a different tenant.", email, tenantId);
        }

        return new UserAccessResult
        {
            IsAllowed = true,
            IsAdmin = bootstrapAdmin || user?.IsAdmin == true,
            Email = email,
            TenantId = tenantId
        };
    }

    private bool TenantIsAllowed(string? tenantId)
    {
        var allowedTenantIds = _options.Value.AllowedTenantIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return allowedTenantIds.Count == 0
            || (!string.IsNullOrWhiteSpace(tenantId) && allowedTenantIds.Contains(tenantId.Trim()));
    }

    private bool DomainIsAllowed(string email)
    {
        var allowedDomains = _options.Value.AllowedDomains
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().TrimStart('@'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (allowedDomains.Count == 0)
        {
            return true;
        }

        var atIndex = email.LastIndexOf('@');
        if (atIndex < 0 || atIndex == email.Length - 1)
        {
            return false;
        }

        var domain = email[(atIndex + 1)..];
        return allowedDomains.Contains(domain);
    }

    private bool IsBootstrapAdmin(string email)
    {
        return _options.Value.BootstrapAdminEmails
            .Any(adminEmail => string.Equals(adminEmail.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
