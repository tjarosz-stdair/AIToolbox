using AIToolbox.Web.Data;
using AIToolbox.Web.Security;
using AIToolbox.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers;

[Authorize]
[Route("Jobs")]
public sealed class JobsController : Controller
{
    private readonly IPlatformRepository _platform;
    private readonly IProductParsingService _productParser;

    public JobsController(IPlatformRepository platform, IProductParsingService productParser)
    {
        _platform = platform;
        _productParser = productParser;
    }

    [HttpGet("InProgress")]
    public async Task<IActionResult> InProgress(CancellationToken cancellationToken)
    {
        var email = User.GetEmailAddress() ?? User.Identity?.Name ?? "unknown";
        var jobs = await _platform.GetActiveToolJobsAsync(email, cancellationToken);
        return Json(new
        {
            count = jobs.Count,
            jobs = jobs.Select(job => new
            {
                id = job.Id,
                toolKey = job.ToolKey,
                title = GetTitle(job),
                status = job.Status,
                createdUtc = job.CreatedUtc.ToString("O"),
                startedUtc = job.StartedUtc?.ToString("O"),
                elapsedSeconds = GetElapsedSeconds(job),
                canCancel = string.Equals(job.ToolKey, ProductParsingService.ToolKey, StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(job.Status, "Queued", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(job.Status, "Processing", StringComparison.OrdinalIgnoreCase))
            })
        });
    }

    [HttpPost("Cancel/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, CancellationToken cancellationToken)
    {
        var email = User.GetEmailAddress() ?? User.Identity?.Name ?? "unknown";
        var cancelled = await _productParser.RequestCancellationAsync(id, email, cancellationToken);
        return Json(new { cancelled });
    }

    private static string GetTitle(Models.ToolJob job)
    {
        return job.ToolKey switch
        {
            ProductParsingService.ToolKey => $"Product parse #{job.Id}",
            _ => $"{job.ToolKey} #{job.Id}"
        };
    }

    private static int GetElapsedSeconds(Models.ToolJob job)
    {
        var baseline = job.StartedUtc ?? job.CreatedUtc;
        return Math.Max(0, (int)(DateTime.UtcNow - baseline).TotalSeconds);
    }
}
