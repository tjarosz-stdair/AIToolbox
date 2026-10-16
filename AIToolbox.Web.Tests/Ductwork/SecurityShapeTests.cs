using System.Reflection;
using AIToolbox.Web.Authorization;
using AIToolbox.Web.Controllers.Ductwork;
using AIToolbox.Web.Ductwork;
using AIToolbox.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using static AIToolbox.Web.Tests.Ductwork.ApiHarness;

namespace AIToolbox.Web.Tests.Ductwork;

/// <summary>The policies as the app defines them, and the attributes every Ductwork controller must carry.</summary>
public class SecurityShapeTests
{
    private static IAuthorizationService Authorization()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(o => o.AddDuctworkPolicies());
        // Stand-ins for the person-level checks, which have their own tests: everyone is active with tool access.
        services.AddSingleton<IAuthorizationHandler, AllowPersonHandler>();
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private sealed class AllowPersonHandler : IAuthorizationHandler
    {
        public Task HandleAsync(AuthorizationHandlerContext context)
        {
            foreach (var requirement in context.PendingRequirements.ToList())
                if (requirement is ActiveUserRequirement or ToolAccessRequirement) context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    private static async Task<bool> AllowedAsync(System.Security.Claims.ClaimsPrincipal user, string policy) =>
        (await Authorization().AuthorizeAsync(user, null, policy)).Succeeded;

    [Fact]
    public async Task The_user_policy_admits_people_and_never_an_api_key()
    {
        Assert.True(await AllowedAsync(Person(Editor), DuctworkPolicies.User));
        Assert.False(await AllowedAsync(ApiConsumer(1, "k", KnownApiScopes.DuctworkWorker, "tool:" + DuctworkTool.ToolKey), DuctworkPolicies.User));
        Assert.False(await AllowedAsync(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity()), DuctworkPolicies.User));
    }

    [Fact]
    public async Task The_worker_policy_needs_the_api_role_and_both_scopes_and_no_person_qualifies()
    {
        Assert.True(await AllowedAsync(ApiConsumer(1, "k", KnownApiScopes.DuctworkWorker, "tool:" + DuctworkTool.ToolKey), DuctworkPolicies.Worker));
        Assert.False(await AllowedAsync(ApiConsumer(1, "k", KnownApiScopes.DuctworkWorker), DuctworkPolicies.Worker));
        Assert.False(await AllowedAsync(ApiConsumer(1, "k", "tool:" + DuctworkTool.ToolKey), DuctworkPolicies.Worker));
        Assert.False(await AllowedAsync(ApiConsumer(1, "k"), DuctworkPolicies.Worker));
        Assert.False(await AllowedAsync(Person(Admin, admin: true), DuctworkPolicies.Worker));
    }

    private static IEnumerable<Type> Controllers() =>
        typeof(DuctworkJobsController).Assembly.GetTypes()
            .Where(t => t.Namespace == "AIToolbox.Web.Controllers.Ductwork" && typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract && t != typeof(DuctworkAppController));

    [Fact]
    public void Every_ductwork_controller_requires_a_policy_and_maps_data_layer_refusals()
    {
        var found = Controllers().ToList();
        Assert.True(found.Count >= 5);
        foreach (var type in found)
        {
            Assert.True(type.GetCustomAttributes<AuthorizeAttribute>(true).Any(a => !string.IsNullOrEmpty(a.Policy)), $"{type.Name} has no authorization policy.");
            Assert.True(type.GetCustomAttributes<DuctworkApiExceptionAttribute>(true).Any(), $"{type.Name} does not map refusals to HTTP answers.");
            Assert.False(type.GetCustomAttributes<AllowAnonymousAttribute>(true).Any(), $"{type.Name} allows anonymous access.");
        }
    }

    [Fact]
    public void Browser_controllers_validate_antiforgery_and_the_worker_controller_does_not_need_to()
    {
        foreach (var type in Controllers().Where(t => t != typeof(DuctworkWorkerApiController)))
            Assert.True(type.GetCustomAttributes<AutoValidateAntiforgeryTokenAttribute>(true).Any(), $"{type.Name} lacks antiforgery validation.");

        var worker = typeof(DuctworkWorkerApiController).GetCustomAttribute<AuthorizeAttribute>()!;
        Assert.Equal(DuctworkPolicies.Worker, worker.Policy);
    }

    [Fact]
    public void Routes_are_unique_and_only_the_worker_is_under_the_api_key_prefix()
    {
        var routes = new List<string>();
        foreach (var type in Controllers())
        {
            var prefix = type.GetCustomAttributes<RouteAttribute>(true).Select(r => r.Template).FirstOrDefault() ?? "";
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                foreach (var verb in method.GetCustomAttributes().OfType<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>())
                {
                    var route = $"{string.Join(",", verb.HttpMethods)} {prefix}/{verb.Template}".TrimEnd('/');
                    routes.Add(route);
                    if (type == typeof(DuctworkWorkerApiController)) Assert.Contains("api/v1/ductwork/worker", route);
                    else Assert.DoesNotContain("api/v1/", route);
                }
        }

        Assert.NotEmpty(routes);
        Assert.Equal(routes.Count, routes.Distinct().Count());
    }

    [Fact]
    public void The_exception_filter_answers_known_refusals_and_leaves_everything_else_alone()
    {
        Assert.Equal(404, DuctworkApiExceptionAttribute.Map(new AIToolbox.Ductwork.Core.Jobs.JobNotFoundException(1))!.Value.Status);
        Assert.Null(DuctworkApiExceptionAttribute.Map(new InvalidOperationException("boom")));
    }

    [Fact]
    public void The_pages_need_the_user_policy_and_the_settings_page_needs_an_administrator()
    {
        Assert.Equal(DuctworkPolicies.User, typeof(DuctworkAppController).GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.False(typeof(DuctworkAppController).GetCustomAttributes<AllowAnonymousAttribute>(true).Any());
        var admin = typeof(DuctworkAppController).GetMethod(nameof(DuctworkAppController.Admin))!;
        Assert.Equal("AdminOnly", admin.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }
}
