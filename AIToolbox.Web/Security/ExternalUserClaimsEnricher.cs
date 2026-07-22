using System.Security.Claims;
using AIToolbox.Web.Data;
using AIToolbox.Web.Models;

namespace AIToolbox.Web.Security;

public sealed class ExternalUserClaimsEnricher : IExternalUserClaimsEnricher
{
    private readonly IAccessControlService _accessControl;
    private readonly IAuthorizedUserRepository _users;
    private readonly IEventLogRepository _events;
    private readonly ILogger<ExternalUserClaimsEnricher> _logger;

    public ExternalUserClaimsEnricher(
        IAccessControlService accessControl,
        IAuthorizedUserRepository users,
        IEventLogRepository events,
        ILogger<ExternalUserClaimsEnricher> logger)
    {
        _accessControl = accessControl;
        _users = users;
        _events = events;
        _logger = logger;
    }

    public async Task EnrichAsync(
        ClaimsPrincipal principal,
        HttpContext httpContext,
        bool recordLogin = false,
        CancellationToken cancellationToken = default)
    {
        if (principal.Identity is not ClaimsIdentity identity)
        {
            return;
        }

        var email = principal.GetEmailAddress();
        var tenantId = principal.GetTenantId();
        if (string.IsNullOrWhiteSpace(email))
        {
            _logger.LogWarning("External authentication did not include a readable email claim.");
            return;
        }

        AddClaimIfMissing(identity, ClaimTypes.Email, email);
        AddClaimIfMissing(identity, "tenant_id", tenantId ?? string.Empty);
        AddClaimIfMissing(identity, ClaimTypes.Role, "User");

        var access = await _accessControl.ValidateExternalUserAsync(email, tenantId, cancellationToken);
        var namedUser = await _users.GetByEmailAsync(email, cancellationToken);
        if (access.IsAdmin)
        {
            AddClaimIfMissing(identity, ClaimTypes.Role, "Admin");
        }

        if (recordLogin)
        {
            await _events.RecordAsync(new EventLogEntry
            {
                EventType = EventLogTypes.UserLogin,
                UserEmail = email,
                DisplayName = principal.Identity?.Name,
                TenantId = tenantId,
                IsNamedUser = namedUser is not null,
                IsAdmin = access.IsAdmin,
                WasAllowed = access.IsAllowed,
                FailureReason = access.FailureReason,
                IpAddress = httpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = httpContext.Request.Headers.UserAgent.ToString()
            }, cancellationToken);

            if (namedUser is not null && access.IsAllowed)
            {
                await _users.MarkLastLoginAsync(email, cancellationToken);
            }
        }

        if (!access.IsAllowed)
        {
            _logger.LogWarning(
                "External authentication completed but app access was denied for {Email} from tenant {TenantId}: {Reason}",
                email,
                tenantId,
                access.FailureReason);
        }
    }

    private static void AddClaimIfMissing(ClaimsIdentity identity, string type, string value)
    {
        if (!identity.HasClaim(type, value))
        {
            identity.AddClaim(new Claim(type, value));
        }
    }
}
