using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Web.Ductwork;
using AIToolbox.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers.Ductwork;

/// <summary>
/// Base for the browser-facing Ductwork API. Cookie-authenticated, so every unsafe request must carry the
/// antiforgery token. The acting person, session and administrator flag come from the authenticated request
/// and never from the body. Data-layer refusals become the HTTP answers in <see cref="DuctworkApiExceptionAttribute"/>.
/// </summary>
[ApiController]
[Authorize(Policy = DuctworkPolicies.User)]
[AutoValidateAntiforgeryToken]
[DuctworkApiException]
public abstract class DuctworkUserApiController : ControllerBase
{
    protected string Email => AuditContext.Normalize(User.GetEmailAddress() ?? throw new DuctworkBadRequestException("The signed-in user has no email address."));

    protected AuditContext Audit() => DuctworkAuditContexts.ForUser(HttpContext);

    protected static IActionResult Json(object? value, int status = 200) => DuctworkJson.Result(value, status);

    protected static IActionResult Problem(int status, string code, string message) => DuctworkJson.Result(new { code, error = message }, status);

    /// <summary>The fencing token the editor was given when it took the lock. Every content edit must send it back.</summary>
    protected long FencingToken()
    {
        var raw = Request.Headers[DuctworkTool.FencingHeader].ToString().Trim();
        return long.TryParse(raw, out var token) && token > 0
            ? token
            : throw new DuctworkBadRequestException($"Send the fencing token you were given when you took the editing lock in the {DuctworkTool.FencingHeader} header.");
    }

    /// <summary>A person with no access to a job is told it does not exist, so job ids reveal nothing.</summary>
    protected static async Task<JobRole> RequireMemberAsync(JobRepository jobs, long jobId, string email, CancellationToken cancellationToken)
    {
        var (role, _) = await jobs.GetAccessAsync(jobId, email, cancellationToken);
        return role ?? throw new JobNotFoundException(jobId);
    }
}
