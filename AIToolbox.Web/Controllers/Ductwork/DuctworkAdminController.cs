using AIToolbox.Ductwork.Core.Tasks;
using AIToolbox.Web.Ductwork;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers.Ductwork;

/// <summary>
/// Which task types are enabled, and the evaluation run behind each (plan section 6.2). Administrators only.
/// A task type cannot be bound without a measured run of the same producer, and every change is recorded in
/// the settings audit log.
/// </summary>
[ApiController]
[Authorize(Policy = "AdminOnly")]
[AutoValidateAntiforgeryToken]
[DuctworkApiException]
[Route("ductwork/api/admin/task-bindings")]
public sealed class DuctworkAdminController(TaskBindingRepository bindings) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool includeRetired, CancellationToken ct) =>
        DuctworkJson.Result(await bindings.ListAsync(includeRetired, ct));

    /// <summary>Producers that have been evaluated, with their evaluation runs, so a task type can be bound to one.</summary>
    [HttpGet("producers")]
    public async Task<IActionResult> Producers(CancellationToken ct) => DuctworkJson.Result(await bindings.ListBindableProducersAsync(ct));

    [HttpPost]
    public async Task<IActionResult> Bind([FromBody] BindTaskRequest request, CancellationToken ct) =>
        DuctworkJson.Result(await bindings.BindAsync(request.TaskType ?? "", request.ProducerId, request.EvalRunId, request.SendsDrawingContent,
            request.MaxAttempts == 0 ? 3 : request.MaxAttempts, DuctworkAuditContexts.ForUser(HttpContext), ct), 201);

    [HttpDelete("{taskType}")]
    public async Task<IActionResult> Retire(string taskType, CancellationToken ct) =>
        DuctworkJson.Result(new { retired = await bindings.RetireAsync(taskType, DuctworkAuditContexts.ForUser(HttpContext), ct) });
}
