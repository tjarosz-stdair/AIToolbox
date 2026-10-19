using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Web.Ductwork;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers.Ductwork;

/// <summary>
/// Who holds the Job Manager permission: archive, purge and restore any job, with no access to a job's contents. Given by an
/// administrator, and every grant and removal is recorded in the settings audit log.
/// </summary>
[ApiController]
[Authorize(Policy = "AdminOnly")]
[AutoValidateAntiforgeryToken]
[DuctworkApiException]
[Route("ductwork/api/admin/job-managers")]
public sealed class DuctworkJobManagersAdminController(ToolPermissionRepository permissions) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => DuctworkJson.Result(await permissions.ListJobManagersAsync(ct));

    [HttpPost]
    public async Task<IActionResult> Grant([FromBody] GrantJobManagerRequest request, CancellationToken ct) =>
        DuctworkJson.Result(await permissions.GrantJobManagerAsync(request.Email ?? "", DuctworkAuditContexts.ForUser(HttpContext), ct), 201);

    [HttpDelete("{email}")]
    public async Task<IActionResult> Revoke(string email, CancellationToken ct) =>
        DuctworkJson.Result(await permissions.RevokeJobManagerAsync(email, DuctworkAuditContexts.ForUser(HttpContext), ct));
}
