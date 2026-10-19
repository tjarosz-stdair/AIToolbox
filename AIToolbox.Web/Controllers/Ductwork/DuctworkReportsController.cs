using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Reports;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers.Ductwork;

/// <summary>
/// Count sheet files (plan section 12). A draft is drawn from the job as it is now and is never stored. Issued reports were made when the job
/// was completed and are served exactly as stored, after being checked against the hash recorded then. Files are always downloads, never
/// pages: an HTML report is served with a policy that forbids scripts as well as being encoded when it was made.
/// </summary>
[Route("ductwork/api/jobs/{jobId:long}/reports")]
public sealed class DuctworkReportsController(JobRepository jobs, ReportService reports) : DuctworkUserApiController
{
    [HttpGet]
    public async Task<IActionResult> List(long jobId, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        return Json((await reports.ListReportsAsync(jobId, ct)).Select(r => new
        {
            reportId = r.PublicId, completionVersion = r.CompletionVersion, format = r.Format, fileName = r.FileName, bytes = r.Bytes,
            isFinal = r.IsFinal, generatedBy = r.GeneratedBy, generatedUtc = r.GeneratedUtc, sha256 = r.Sha256
        }));
    }

    [HttpGet("draft/{format}")]
    public async Task<IActionResult> Draft(long jobId, string format, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var (bytes, name, contentType) = await reports.GetDraftAsync(jobId, format, Email, ct);
        return Download(bytes, name, contentType);
    }

    [HttpGet("{reportId:guid}/download")]
    public async Task<IActionResult> Download(long jobId, Guid reportId, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var (report, bytes, contentType) = await reports.GetStoredAsync(jobId, reportId, ct);
        return Download(bytes, report.FileName, contentType);
    }

    private FileContentResult Download(byte[] bytes, string fileName, string contentType)
    {
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
        Response.Headers["Cache-Control"] = "private, no-store";
        return File(bytes, contentType, fileName);
    }
}
