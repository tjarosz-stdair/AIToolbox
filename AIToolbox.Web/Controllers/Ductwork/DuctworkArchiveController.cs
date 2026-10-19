using AIToolbox.Ductwork.Core.Archive;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Web.Ductwork;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers.Ductwork;

/// <summary>
/// Archive, purge and restore (plan section 8.3). A request freezes the job and queues the work; the page then follows its progress.
/// Owners and Job Managers may archive and restore; a Job Manager needs no membership and gets no view of a job's contents, only the
/// master record (name, owner, state) and the archive. A person with no part in a job is told it does not exist.
/// </summary>
[Route("ductwork/api")]
public sealed class DuctworkArchiveController(JobRepository jobs, ArchiveService archives, ToolPermissionRepository permissions) : DuctworkUserApiController
{
    private bool IsAdmin => User.IsInRole("Admin");

    private static object Shape(ArchiveRecord a, bool admin) => new
    {
        archiveId = a.ArchiveId, jobId = a.JobId, completionVersion = a.CompletionVersion, kind = a.Kind, status = a.Status, stateAtArchive = a.StateAtArchive,
        formatVersion = a.FormatVersion, fileName = a.FileName, sizeBytes = a.SizeBytes, sha256 = a.Sha256, sourceFileCount = a.SourceFileCount, sourceBytes = a.SourceBytes,
        tableCounts = a.TableCounts, lastProblem = a.LastProblem, lastProblemUtc = a.LastProblemUtc, createdUtc = a.CreatedUtc, createdBy = a.CreatedBy,
        verifiedUtc = a.VerifiedUtc, purgedUtc = a.PurgedUtc, restoredUtc = a.RestoredUtc, supersededUtc = a.SupersededUtc,
        // Where the file is kept is for administrators; everyone else needs only to know it exists.
        storageKey = admin ? a.StorageKey : null, locations = admin ? a.Locations : null
    };

    private static object Shape(ArchiveOperationRecord o) => new
    {
        operationId = o.OperationId, jobId = o.JobId, archiveId = o.ArchiveId, kind = o.Kind, status = o.Status, step = o.Step, message = o.Message,
        progressDone = o.ProgressDone, progressTotal = o.ProgressTotal, attempt = o.Attempt, maxAttempts = o.MaxAttempts, requestedBy = o.RequestedBy,
        requestedUtc = o.RequestedUtc, startedUtc = o.StartedUtc, finishedUtc = o.FinishedUtc, error = o.Error
    };

    /// <summary>A member of the job or a Job Manager. Anyone else is told it does not exist.</summary>
    private async Task<(JobRole? Role, bool Manager)> RequireInvolvedAsync(long jobId, CancellationToken ct)
    {
        var (role, manager) = await jobs.GetAccessAsync(jobId, Email, ct);
        if (role is null && !manager) throw new JobNotFoundException(jobId);
        return (role, manager);
    }

    /// <summary>An administrator or a Job Manager, for the pages that span every job.</summary>
    private async Task RequireOverviewAsync(CancellationToken ct)
    {
        if (!IsAdmin && !await permissions.GetIsJobManagerAsync(Email, ct))
            throw new JobAccessDeniedException("Only an administrator or a Job Manager can see this.");
    }

    // ------------------------------------------------------------------ one job

    [HttpGet("jobs/{jobId:long}/archive")]
    public async Task<IActionResult> ForJob(long jobId, CancellationToken ct)
    {
        var (role, manager) = await RequireInvolvedAsync(jobId, ct);
        var job = await jobs.GetAsync(jobId, ct) ?? throw new JobNotFoundException(jobId);
        var list = await archives.ListArchivesAsync(jobId, ct);
        var ops = await archives.ListOperationsAsync(jobId, 5, ct);
        return Json(new
        {
            state = job.State,
            canArchive = JobPermissions.Can(role, manager, JobCapability.Archive),
            canRestore = JobPermissions.Can(role, manager, JobCapability.Restore),
            archives = list.Select(a => Shape(a, IsAdmin)),
            operations = ops.Select(Shape)
        });
    }

    [HttpPost("jobs/{jobId:long}/archive")]
    public async Task<IActionResult> Archive(long jobId, [FromBody] ArchiveJobRequest request, CancellationToken ct)
    {
        await RequireInvolvedAsync(jobId, ct);
        var kind = DuctworkParse.Enum<ArchiveKind>(request.Kind, "kind");
        return Json(Shape(await archives.RequestArchiveAsync(jobId, kind, request.ConfirmName, Audit(), ct)), 202);
    }

    [HttpPost("jobs/{jobId:long}/restore")]
    public async Task<IActionResult> Restore(long jobId, CancellationToken ct)
    {
        await RequireInvolvedAsync(jobId, ct);
        return Json(Shape(await archives.RequestRestoreAsync(jobId, Audit(), ct)), 202);
    }

    // ------------------------------------------------------------------ across jobs: administrators and Job Managers

    /// <summary>
    /// Every job that can be, or has been, archived, with its latest archive and what is happening to it. Only the master record is
    /// shown: a Job Manager is not given the contents of any job.
    /// </summary>
    [HttpGet("archives")]
    public async Task<IActionResult> Overview(CancellationToken ct)
    {
        await RequireOverviewAsync(ct);
        var states = new[] { JobState.Complete, JobState.Closed, JobState.Archiving, JobState.Archived, JobState.Restoring };
        var all = await jobs.ListAsync(new JobListFilter { States = states, Limit = 500 }, ct);
        var latest = (await archives.ListAllArchivesAsync(1000, ct)).GroupBy(a => a.Archive.JobId).ToDictionary(g => g.Key, g => g.First().Archive);
        var operations = (await archives.ListRecentOperationsAsync(1000, ct)).GroupBy(o => o.JobId).ToDictionary(g => g.Key, g => g.First());
        var problems = (await archives.ListAllArchivesAsync(1000, ct)).Where(a => a.Archive.LastProblem is not null).Select(a => new { a.Archive.ArchiveId, a.Archive.JobId, jobName = a.JobName, problem = a.Archive.LastProblem, a.Archive.LastProblemUtc }).ToList();

        return Json(new
        {
            admin = IsAdmin,
            problems,
            jobs = all.Select(j => new
            {
                jobId = j.JobId, publicId = j.PublicId, name = j.Name, customerName = j.CustomerName, projectName = j.ProjectName, ownerEmail = j.OwnerEmail,
                state = j.State, completionVersion = j.CompletionVersion, updatedUtc = j.UpdatedUtc,
                latestArchive = latest.TryGetValue(j.JobId, out var a) ? Shape(a, IsAdmin) : null,
                operation = operations.TryGetValue(j.JobId, out var o) ? Shape(o) : null
            })
        });
    }

    [HttpPost("archives/{archiveId:long}/check")]
    public async Task<IActionResult> Check(long archiveId, CancellationToken ct)
    {
        await RequireOverviewAsync(ct);
        var result = await archives.CheckArchiveAsync(archiveId, Audit(), ct);
        return Json(new { ok = result.Ok, problems = result.Problems });
    }

    [HttpPost("archives/{archiveId:long}/relink")]
    public async Task<IActionResult> Relink(long archiveId, [FromBody] RelinkArchiveRequest request, CancellationToken ct) =>
        Json(Shape(await archives.RelinkArchiveAsync(archiveId, request.StorageKey ?? "", Audit(), ct), IsAdmin));

    [HttpPost("archives/operations/{operationId:long}/retry")]
    public async Task<IActionResult> Retry(long operationId, CancellationToken ct)
    {
        await RequireOverviewAsync(ct);
        return Json(Shape(await archives.RetryOperationAsync(operationId, Audit(), ct)));
    }
}
