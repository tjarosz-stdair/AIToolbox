using AIToolbox.Web.Security;
using Microsoft.AspNetCore.Authorization;

namespace AIToolbox.Web.Authorization;

public sealed class AdminUserAuthorizationHandler : AuthorizationHandler<AdminUserRequirement>
{
    private readonly IAccessControlService _accessControl;

    public AdminUserAuthorizationHandler(IAccessControlService accessControl)
    {
        _accessControl = accessControl;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdminUserRequirement requirement)
    {
        var access = await _accessControl.GetAccessAsync(context.User);
        if (access.IsAllowed && access.IsAdmin)
        {
            context.Succeed(requirement);
        }
    }
}
