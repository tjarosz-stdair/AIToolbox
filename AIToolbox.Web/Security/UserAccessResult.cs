namespace AIToolbox.Web.Security;

public sealed class UserAccessResult
{
    public bool IsAllowed { get; init; }

    public bool IsAdmin { get; init; }

    public string Email { get; init; } = string.Empty;

    public string? TenantId { get; init; }

    public string? FailureReason { get; init; }

    public static UserAccessResult Denied(string reason, string email = "", string? tenantId = null)
    {
        return new UserAccessResult
        {
            IsAllowed = false,
            IsAdmin = false,
            Email = email,
            TenantId = tenantId,
            FailureReason = reason
        };
    }
}
