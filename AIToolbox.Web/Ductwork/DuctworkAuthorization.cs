using AIToolbox.Web.Authorization;
using AIToolbox.Web.Security;
using Microsoft.AspNetCore.Authorization;

namespace AIToolbox.Web.Ductwork;

public static class DuctworkAuthorization
{
    /// <summary>
    /// The two Ductwork policies. Kept in one place so the app and its tests use the same definitions.
    /// </summary>
    public static void AddDuctworkPolicies(this AuthorizationOptions options)
    {
        // A signed-in person with access to the Ductwork tool. An API key is not a person, so it cannot use the browser API.
        options.AddPolicy(DuctworkPolicies.User, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(new ActiveUserRequirement(), new ToolAccessRequirement(DuctworkTool.ToolKey));
            policy.RequireAssertion(context => !context.User.IsApiConsumer());
        });

        // The central worker: an API consumer holding the worker scope and the tool scope, nothing else.
        options.AddPolicy(DuctworkPolicies.Worker, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.RequireRole("ApiConsumer");
            policy.RequireAssertion(context =>
                context.User.IsApiConsumer()
                && context.User.HasApiScope(KnownApiScopes.DuctworkWorker)
                && context.User.HasApiScope($"tool:{DuctworkTool.ToolKey}"));
        });
    }
}
