using AIToolbox.Web.Data;
using AIToolbox.Web.Services;
using AIToolbox.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers;

[Authorize(Policy = "AdminOnly")]
[Route("Admin/Batches")]
public sealed class AdminBatchesController : Controller
{
    private readonly IAiBatchRepository _batches;
    private readonly IAiBatchService _service;

    public AdminBatchesController(IAiBatchRepository batches, IAiBatchService service)
    {
        _batches = batches;
        _service = service;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        return View(await _batches.GetJobsAsync(null, null, includeAll: true, cancellationToken));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Detail(int id, CancellationToken cancellationToken)
    {
        var detail = await _batches.GetJobDetailAsync(id, null, null, includeAll: true, cancellationToken);
        return detail is null
            ? NotFound()
            : View(new AdminBatchDetailViewModel { Job = detail.Job, Requests = detail.Requests });
    }

    [HttpPost("{id:int}/Cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, CancellationToken cancellationToken)
    {
        await _service.CancelAsync(id, null, null, isAdmin: true, cancellationToken);
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("{id:int}/Poll")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Poll(int id, CancellationToken cancellationToken)
    {
        await _service.PollJobAsync(id, cancellationToken);
        return RedirectToAction(nameof(Detail), new { id });
    }
}
