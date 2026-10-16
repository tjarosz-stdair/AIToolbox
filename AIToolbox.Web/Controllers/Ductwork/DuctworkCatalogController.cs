using AIToolbox.Ductwork.Core.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers.Ductwork;

/// <summary>Things any signed-in Ductwork user needs to build a screen that are not about one job.</summary>
[Route("ductwork/api")]
public sealed class DuctworkCatalogController(TaskBindingRepository bindings, WorkerRegistry workers) : DuctworkUserApiController
{
    /// <summary>The kinds of work that can be queued: the task types an administrator has enabled, and whether each sends drawings to a hosted model.</summary>
    [HttpGet("task-types")]
    public async Task<IActionResult> TaskTypes(CancellationToken ct) =>
        Json((await bindings.ListAsync(includeRetired: false, ct)).Select(b => new { taskType = b.TaskType, sendsDrawingContent = b.SendsDrawingContentToHostedModel, maxAttempts = b.MaxAttempts }));

    /// <summary>
    /// Whether any worker is connected, so a person who queues work is told when nobody is there to do it. Counts only: who the workers
    /// are is for administrators.
    /// </summary>
    [HttpGet("workers/summary")]
    public async Task<IActionResult> WorkerSummary(CancellationToken ct)
    {
        var all = await workers.ListAsync(cancellationToken: ct);
        return Json(new { online = all.Count(w => w.Status == WorkerStatus.Online), draining = all.Count(w => w.Status == WorkerStatus.Draining), total = all.Count });
    }
}
