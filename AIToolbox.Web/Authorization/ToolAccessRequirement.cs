using Microsoft.AspNetCore.Authorization;

namespace AIToolbox.Web.Authorization;

public sealed class ToolAccessRequirement : IAuthorizationRequirement
{
    public ToolAccessRequirement(string toolKey)
    {
        ToolKey = toolKey;
    }

    public string ToolKey { get; }
}
