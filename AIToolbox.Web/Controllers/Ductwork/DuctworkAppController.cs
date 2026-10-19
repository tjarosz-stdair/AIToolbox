using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Web.Ductwork;
using AIToolbox.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers.Ductwork;

/// <summary>
/// The pages of the Ductwork workbench. They carry no data of their own: each is a shell that the scripts under
/// <c>wwwroot/js/ductwork</c> fill from the Ductwork API, so every rule (who may see a job, who may edit it) is enforced
/// in one place, the API, and not repeated here. A job page is only served to a member of the job.
/// </summary>
[Authorize(Policy = DuctworkPolicies.User)]
[Route("Ductwork")]
public sealed class DuctworkAppController(JobRepository jobs, ToolPermissionRepository permissions) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        View(new DuctworkPageModel(0, User.GetEmailAddress() ?? "", User.IsInRole("Admin"), await permissions.GetIsJobManagerAsync(User.GetEmailAddress() ?? "", cancellationToken)));

    [HttpGet("Jobs/{jobId:long}")]
    public async Task<IActionResult> Job(long jobId, CancellationToken cancellationToken)
    {
        var email = AuditContext.Normalize(User.GetEmailAddress() ?? "");
        // A person who is not on the job is told it does not exist, as the API does.
        var (role, _) = await jobs.GetAccessAsync(jobId, email, cancellationToken);
        return role is null ? NotFound() : View(new DuctworkPageModel(jobId, email, User.IsInRole("Admin")));
    }

    /// <summary>Archives across every job: for administrators and Job Managers. Anyone else is told there is no such page.</summary>
    [HttpGet("Archives")]
    public async Task<IActionResult> Archives(CancellationToken cancellationToken)
    {
        var email = AuditContext.Normalize(User.GetEmailAddress() ?? "");
        var manager = await permissions.GetIsJobManagerAsync(email, cancellationToken);
        return User.IsInRole("Admin") || manager ? View(new DuctworkPageModel(0, email, User.IsInRole("Admin"), manager)) : NotFound();
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpGet("Admin")]
    public IActionResult Admin() => View(new DuctworkPageModel(0, User.GetEmailAddress() ?? "", true));
}

public sealed record DuctworkPageModel(long JobId, string Email, bool IsAdmin, bool IsJobManager = false);
