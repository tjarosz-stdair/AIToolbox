using AIToolbox.Web.Data;
using AIToolbox.Web.Models;
using AIToolbox.Web.Security;
using AIToolbox.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers.Api;

[ApiController]
[Authorize(Policy = "BatchApiAccess")]
[Route("api/v1/batches")]
public sealed class BatchesController : ControllerBase
{
    private readonly IAiBatchRepository _batches;
    private readonly IAiBatchService _service;

    public BatchesController(IAiBatchRepository batches, IAiBatchService service)
    {
        _batches = batches;
        _service = service;
    }

    [HttpPost]
    [RequestSizeLimit(100 * 1024 * 1024)]
    public async Task<IActionResult> Create([FromBody] CreateAiBatchJobRequest request, CancellationToken cancellationToken)
    {
        if (!HasScope(KnownApiScopes.BatchWrite) || !HasScope($"tool:{request.ToolKey}"))
        {
            return Forbid();
        }

        var userEmail = User.GetEmailAddress();
        if (string.IsNullOrWhiteSpace(userEmail))
        {
            return Unauthorized();
        }

        try
        {
            var result = await _service.CreateAsync(request, userEmail, User.GetApiConsumerId(), cancellationToken);
            return AcceptedAtAction(nameof(Get), new { id = result.Id }, result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (!HasScope(KnownApiScopes.BatchRead))
        {
            return Forbid();
        }

        return Ok(await _batches.GetJobsAsync(
            User.GetEmailAddress(),
            User.GetApiConsumerId(),
            User.IsInRole("Admin"),
            cancellationToken));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken cancellationToken)
    {
        if (!HasScope(KnownApiScopes.BatchRead))
        {
            return Forbid();
        }

        var job = await _batches.GetJobAsync(
            id,
            User.GetEmailAddress(),
            User.GetApiConsumerId(),
            User.IsInRole("Admin"),
            cancellationToken);
        return job is null ? NotFound() : Ok(job);
    }

    [HttpGet("{id:int}/requests")]
    public async Task<IActionResult> Requests(int id, CancellationToken cancellationToken)
    {
        if (!HasScope(KnownApiScopes.BatchRead))
        {
            return Forbid();
        }

        var detail = await _batches.GetJobDetailAsync(
            id,
            User.GetEmailAddress(),
            User.GetApiConsumerId(),
            User.IsInRole("Admin"),
            cancellationToken);
        return detail is null ? NotFound() : Ok(detail.Requests);
    }

    [HttpGet("{id:int}/results")]
    public async Task<IActionResult> Results(int id, CancellationToken cancellationToken)
    {
        if (!HasScope(KnownApiScopes.BatchRead))
        {
            return Forbid();
        }

        var detail = await _batches.GetJobDetailAsync(
            id,
            User.GetEmailAddress(),
            User.GetApiConsumerId(),
            User.IsInRole("Admin"),
            cancellationToken);
        return detail is null
            ? NotFound()
            : Ok(detail.Requests.Where(request => !string.IsNullOrWhiteSpace(request.ResponseJson) || !string.IsNullOrWhiteSpace(request.ErrorJson)));
    }

    [HttpPost("{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id, CancellationToken cancellationToken)
    {
        if (!HasScope(KnownApiScopes.BatchCancel))
        {
            return Forbid();
        }

        try
        {
            await _service.CancelAsync(id, User.GetEmailAddress(), User.GetApiConsumerId(), User.IsInRole("Admin"), cancellationToken);
            return Accepted();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private bool HasScope(string scope)
    {
        return !User.IsApiConsumer() || User.HasApiScope(scope);
    }
}
