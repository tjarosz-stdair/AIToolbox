using AIToolbox.Web.Security;
using Microsoft.AspNetCore.Authorization;

namespace AIToolbox.Web.Authorization;

public sealed class ActiveUserAuthorizationHandler : AuthorizationHandler<ActiveUserRequirement>
{
    private readonly IAccessControlService _accessControl;

    public ActiveUserAuthorizationHandler(IAccessControlService accessControl)
    {
        _accessControl = accessControl;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ActiveUserRequirement requirement)
    {
        if (context.User.IsApiConsumer())
        {
            context.Succeed(requirement);
            return;
        }

        var access = await _accessControl.GetAccessAsync(context.User);
        if (access.IsAllowed)
        {
            context.Succeed(requirement);
        }
    }
}
