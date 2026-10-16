using AIToolbox.Ductwork.Core.Drawings;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Web.Ductwork;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers.Ductwork;

/// <summary>
/// Drawing files for a job: resumable chunked upload, the list of files, and download. Starting and finishing an
/// upload are edits (edit lock and fencing token); the pieces between are tied to the browser session that started it.
/// </summary>
[Route("ductwork/api/jobs/{jobId:long}")]
public sealed class DuctworkUploadsController(JobRepository jobs, EditLockService locks, UploadService uploads) : DuctworkUserApiController
{
    public const string ChunkChecksumHeader = "X-Chunk-Sha256";

    [HttpPost("uploads")]
    public async Task<IActionResult> Begin(long jobId, [FromBody] BeginUploadRequest request, CancellationToken ct)
    {
        var audit = Audit();
        var token = await locks.ResolveTokenAsync(jobId, FencingToken(), audit, ct);
        var session = await uploads.BeginAsync(jobId, new BeginUpload(request.FileName ?? "", request.TotalBytes, request.Sha256), token, audit, ct);
        return Json(View(session), 201);
    }

    /// <summary>Where the upload is up to. A browser that lost its connection asks this before sending the next piece.</summary>
    [HttpGet("uploads/{uploadId:guid}")]
    public async Task<IActionResult> Status(long jobId, Guid uploadId, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var session = await uploads.GetAsync(uploadId, ct);
        if (session is null || session.JobId != jobId) throw new KeyNotFoundException("That upload does not exist.");
        return Json(View(session));
    }

    /// <summary>The next piece, as the raw request body. It must start at <c>offset</c>, which must be where the upload is up to.</summary>
    [HttpPut("uploads/{uploadId:guid}")]
    [RequestSizeLimit(DuctworkUploadLimits.MaxRequestBytes)]
    public async Task<IActionResult> Append(long jobId, Guid uploadId, [FromQuery] long offset, CancellationToken ct)
    {
        var existing = await uploads.GetAsync(uploadId, ct);
        if (existing is null || existing.JobId != jobId) throw new KeyNotFoundException("That upload does not exist.");

        if (Request.ContentLength is { } declared && declared > existing.ChunkBytes)
            return Problem(413, "too-large", $"A piece may be at most {existing.ChunkBytes:N0} bytes.");

        var checksum = Request.Headers[ChunkChecksumHeader].ToString();
        var session = await uploads.AppendAsync(uploadId, offset, Request.Body, string.IsNullOrWhiteSpace(checksum) ? null : checksum, Audit(), ct);
        return Json(View(session));
    }

    [HttpPost("uploads/{uploadId:guid}/complete")]
    public async Task<IActionResult> Complete(long jobId, Guid uploadId, CancellationToken ct)
    {
        var audit = Audit();
        var existing = await uploads.GetAsync(uploadId, ct);
        if (existing is null || existing.JobId != jobId) throw new KeyNotFoundException("That upload does not exist.");

        var token = await locks.ResolveTokenAsync(jobId, FencingToken(), audit, ct);
        return Json(FileView(await uploads.CompleteAsync(uploadId, token, audit, ct)), 201);
    }

    [HttpDelete("uploads/{uploadId:guid}")]
    public async Task<IActionResult> Abort(long jobId, Guid uploadId, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var existing = await uploads.GetAsync(uploadId, ct);
        if (existing is null || existing.JobId != jobId) throw new KeyNotFoundException("That upload does not exist.");
        return Json(new { cancelled = await uploads.AbortAsync(uploadId, Audit(), ct) });
    }

    [HttpGet("files")]
    public async Task<IActionResult> ListFiles(long jobId, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        return Json((await uploads.ListFilesAsync(jobId, ct)).Select(FileView));
    }

    /// <summary>The file's bytes. Sent as a download unless <c>inline</c> is asked for, and never allowed to run as a page.</summary>
    [HttpGet("files/{fileId:guid}/content")]
    public async Task<IActionResult> Content(long jobId, Guid fileId, [FromQuery] bool inline, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var file = await uploads.GetFileAsync(jobId, fileId, ct) ?? throw new KeyNotFoundException("That file does not exist.");
        var stream = await uploads.GetFileContentAsync(file, ct) ?? throw new KeyNotFoundException("That file is no longer available.");

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "sandbox";
        Response.Headers["Cache-Control"] = "private, no-store";
        return new FileStreamResult(stream, "application/pdf") { FileDownloadName = inline ? null : file.OriginalName, EnableRangeProcessing = true };
    }

    private static object View(UploadSessionRecord s) => new
    {
        uploadId = s.PublicId, fileName = s.OriginalName, totalBytes = s.TotalBytes, receivedBytes = s.ReceivedBytes, status = s.Status,
        chunkBytes = s.ChunkBytes, expiresUtc = s.ExpiresUtc, isExpired = s.IsExpired, fileId = s.SourceFilePublicId
    };

    private static object FileView(SourceFileRecord f) => new
    {
        fileId = f.PublicId, fileName = f.OriginalName, bytes = f.Bytes, sha256 = f.Sha256, contentType = f.ContentType, revision = f.RevisionNo,
        duplicateOf = f.DuplicateOfPublicId, uploadedBy = f.UploadedBy, uploadedUtc = f.UploadedUtc
    };
}
