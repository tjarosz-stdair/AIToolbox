using AIToolbox.Web.Data;
using AIToolbox.Web.Models;
using AIToolbox.Web.Security;
using Microsoft.AspNetCore.Authorization;

namespace AIToolbox.Web.Authorization;

public sealed class ToolAccessAuthorizationHandler : AuthorizationHandler<ToolAccessRequirement>
{
    private readonly IAccessControlService _accessControl;
    private readonly IPlatformRepository _platform;

    public ToolAccessAuthorizationHandler(
        IAccessControlService accessControl,
        IPlatformRepository platform)
    {
        _accessControl = accessControl;
        _platform = platform;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ToolAccessRequirement requirement)
    {
        if (context.User.IsApiConsumer())
        {
            if (context.User.HasApiScope($"tool:{requirement.ToolKey}"))
            {
                context.Succeed(requirement);
            }

            return;
        }

        var appAccess = await _accessControl.GetAccessAsync(context.User);
        if (!appAccess.IsAllowed || string.IsNullOrWhiteSpace(appAccess.Email))
        {
            return;
        }

        var tool = await _platform.GetToolAsync(requirement.ToolKey);
        if (tool is null || !tool.IsEnabled)
        {
            return;
        }

        if (tool.AccessMode == ToolAccessMode.OpenToAllAuthenticated
            || await _platform.IsUserAllowedForToolAsync(tool.ToolKey, appAccess.Email))
        {
            context.Succeed(requirement);
        }
    }
}
