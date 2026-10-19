using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Activity;
using AIToolbox.Ductwork.Core.Counting;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Llm;
using AIToolbox.Ductwork.Core.Reports;
using AIToolbox.Ductwork.Core.Tasks;
using AIToolbox.Web.Ductwork;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers.Ductwork;

/// <summary>Jobs and everything that hangs off one job: members, tags, workflow, lifecycle, audit, time on job, tasks, entities and the count sheet.</summary>
[Route("ductwork/api/jobs")]
public sealed class DuctworkJobsController(
    JobRepository jobs,
    EditLockService locks,
    ProcessingTaskRepository tasks,
    EntityRepository entities,
    CountSheetRepository countSheets,
    ActivityRepository activity,
    VisionRepository vision,
    ReportService reports) : DuctworkUserApiController
{
    // ------------------------------------------------------------------ jobs

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateJobRequest request, CancellationToken ct)
    {
        var level = DuctworkParse.OptionalEnum<ReviewLevel>(request.ReviewLevel, "review level") ?? ReviewLevel.Sensitive;
        var job = await jobs.CreateAsync(new NewJob
        {
            Name = request.Name ?? "", CustomerName = request.CustomerName, ProjectName = request.ProjectName, SiteAddress = request.SiteAddress, ReviewLevel = level
        }, Audit(), ct);
        return Json(job, 201);
    }

    /// <summary>The jobs the caller is a member of. A job is never listed for someone with no access to it.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string[]? state, [FromQuery] string[]? tag, [FromQuery] string? owner, [FromQuery] int? limit, CancellationToken ct)
    {
        var states = state is { Length: > 0 } ? state.Select(s => DuctworkParse.Enum<JobState>(s, "state")).ToList() : null;
        var list = await jobs.ListAsync(new JobListFilter
        {
            States = states, Tags = tag is { Length: > 0 } ? tag : null, OwnerEmail = owner, MemberEmail = Email, Limit = Math.Clamp(limit ?? 200, 1, 500)
        }, ct);
        return Json(list);
    }

    [HttpGet("{jobId:long}")]
    public async Task<IActionResult> Get(long jobId, CancellationToken ct)
    {
        var role = await RequireMemberAsync(jobs, jobId, Email, ct);
        var job = await jobs.GetAsync(jobId, ct) ?? throw new JobNotFoundException(jobId);

        return Json(new
        {
            job,
            myRole = role,
            canEdit = JobPermissions.HasEditPermission(role),
            members = await jobs.GetMembersAsync(jobId, ct),
            tags = await jobs.GetTagsAsync(jobId, ct),
            steps = await jobs.GetWorkflowAsync(jobId, ct),
            lifecycleEditable = JobLifecycle.IsEditable(job.State)
        });
    }

    [HttpPut("{jobId:long}/details")]
    public async Task<IActionResult> UpdateDetails(long jobId, [FromBody] UpdateDetailsRequest request, CancellationToken ct)
    {
        var audit = Audit();
        var token = await locks.ResolveTokenAsync(jobId, FencingToken(), audit, ct);
        var updated = await jobs.UpdateDetailsAsync(jobId, new JobDetails
        {
            Name = request.Name, CustomerName = request.CustomerName, ProjectName = request.ProjectName, SiteAddress = request.SiteAddress,
            Clear = (request.Clear ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase)
        }, request.ExpectedStateVersion, token, audit, ct);
        return Json(updated);
    }

    [HttpPut("{jobId:long}/review-level")]
    public async Task<IActionResult> SetReviewLevel(long jobId, [FromBody] SetReviewLevelRequest request, CancellationToken ct)
    {
        var audit = Audit();
        var token = await locks.ResolveTokenAsync(jobId, FencingToken(), audit, ct);
        return Json(await jobs.SetReviewLevelAsync(jobId, DuctworkParse.Enum<ReviewLevel>(request.ReviewLevel, "review level"), token, audit, ct));
    }

    /// <summary>The estimator's own estimate of the manual time, for ROI. Metadata, so it can be set on a finished job.</summary>
    [HttpPut("{jobId:long}/baseline")]
    public async Task<IActionResult> SetBaseline(long jobId, [FromBody] SetBaselineRequest request, CancellationToken ct) =>
        Json(await jobs.SetBaselineManualHoursAsync(jobId, request.Hours, Audit(), ct));

    [HttpPut("{jobId:long}/workflow/{step}")]
    public async Task<IActionResult> SetStep(long jobId, string step, [FromBody] SetStepRequest request, CancellationToken ct)
    {
        var audit = Audit();
        var token = await locks.ResolveTokenAsync(jobId, FencingToken(), audit, ct);
        var steps = await jobs.SetWorkflowStepAsync(jobId, DuctworkParse.Enum<WorkflowStep>(step, "step"),
            DuctworkParse.Enum<StepStatus>(request.Status, "status"), token, audit, ct);
        return Json(steps);
    }

    // ------------------------------------------------------------------ lifecycle

    [HttpPost("{jobId:long}/complete")]
    public async Task<IActionResult> BeginComplete(long jobId, CancellationToken ct) => Json(await jobs.BeginCompleteAsync(jobId, Audit(), ct));

    /// <summary>
    /// Finishes completing. The issued reports are made first, from the state the job is frozen in; if that fails the job stays Completing and this
    /// can be called again, so a job is never Complete without its reports.
    /// </summary>
    [HttpPost("{jobId:long}/complete/finish")]
    public async Task<IActionResult> FinishComplete(long jobId, CancellationToken ct)
    {
        var audit = Audit();
        await reports.IssueAsync(jobId, audit, ct);
        return Json(await jobs.FinishCompleteAsync(jobId, audit, ct));
    }

    [HttpPost("{jobId:long}/close")]
    public async Task<IActionResult> Close(long jobId, [FromBody] ReasonRequest request, CancellationToken ct) =>
        Json(await jobs.CloseAsync(jobId, request.Reason ?? "", Audit(), ct));

    [HttpPost("{jobId:long}/reopen")]
    public async Task<IActionResult> Reopen(long jobId, [FromBody] ReasonRequest request, CancellationToken ct) =>
        Json(await jobs.ReopenAsync(jobId, request.Reason ?? "", Audit(), ct));

    // ------------------------------------------------------------------ members and tags

    [HttpPost("{jobId:long}/members")]
    public async Task<IActionResult> AddMember(long jobId, [FromBody] AddMemberRequest request, CancellationToken ct) =>
        Json(await jobs.AddMemberAsync(jobId, request.Email ?? "", DuctworkParse.Enum<JobRole>(request.Role, "role"), Audit(), ct), 201);

    [HttpPut("{jobId:long}/members/{email}")]
    public async Task<IActionResult> ChangeMemberRole(long jobId, string email, [FromBody] ChangeRoleRequest request, CancellationToken ct) =>
        Json(await jobs.ChangeMemberRoleAsync(jobId, email, DuctworkParse.Enum<JobRole>(request.Role, "role"), Audit(), ct));

    [HttpDelete("{jobId:long}/members/{email}")]
    public async Task<IActionResult> RemoveMember(long jobId, string email, CancellationToken ct) =>
        Json(await jobs.RemoveMemberAsync(jobId, email, Audit(), ct));

    [HttpPost("{jobId:long}/tags")]
    public async Task<IActionResult> AddTag(long jobId, [FromBody] TagRequest request, CancellationToken ct) =>
        Json(await jobs.AddTagAsync(jobId, request.Tag ?? "", Audit(), ct));

    [HttpDelete("{jobId:long}/tags/{tag}")]
    public async Task<IActionResult> RemoveTag(long jobId, string tag, CancellationToken ct) =>
        Json(await jobs.RemoveTagAsync(jobId, tag, Audit(), ct));

    // ------------------------------------------------------------------ audit and time on job

    [HttpGet("{jobId:long}/audit")]
    public async Task<IActionResult> ListAudit(long jobId, [FromQuery] string? category, [FromQuery] string? actor, [FromQuery] string? text,
        [FromQuery] long? after, [FromQuery] int? limit, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        return Json(await jobs.ListAuditAsync(jobId, new AuditFilter
        {
            Category = category, ActorEmail = actor, Text = text, AfterEventId = after, Limit = Math.Clamp(limit ?? 500, 1, 2000)
        }, ct));
    }

    [HttpGet("{jobId:long}/audit/verify")]
    public async Task<IActionResult> VerifyAudit(long jobId, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        return Json(await jobs.VerifyAuditChainAsync(jobId, ct));
    }

    /// <summary>The viewer calls this every few seconds while a job is open, saying only whether the person is active or idle. The server measures the time.</summary>
    [HttpPost("{jobId:long}/activity/heartbeat")]
    public async Task<IActionResult> ActivityHeartbeat(long jobId, [FromBody] ActivityHeartbeatRequest request, CancellationToken ct)
    {
        var job = await jobs.GetAsync(jobId, ct);
        var session = await activity.RecordHeartbeatAsync(jobId, Audit(), DuctworkParse.Enum<ActivityState>(request.State, "state"),
            DuctworkParse.OptionalEnum<WorkflowStep>(request.Step, "step"), request.Surface, job?.ReviewLevel, ct);
        return Json(new { session.ActiveSeconds, session.IdleSeconds, session.Heartbeats });
    }

    [HttpPost("{jobId:long}/activity/end")]
    public async Task<IActionResult> EndActivity(long jobId, CancellationToken ct) => Json(new { ended = await activity.EndSessionAsync(jobId, Audit(), ct) });

    /// <summary>Totals are visible to anyone on the job. The per-person breakdown is for the job's owners only.</summary>
    [HttpGet("{jobId:long}/activity")]
    public async Task<IActionResult> ActivitySummary(long jobId, CancellationToken ct)
    {
        var role = await RequireMemberAsync(jobs, jobId, Email, ct);
        var summary = await activity.GetSummaryAsync(jobId, ct);
        return Json(role == JobRole.Owner ? summary : summary with { ByUser = [] });
    }

    // ------------------------------------------------------------------ tasks

    [HttpGet("{jobId:long}/tasks")]
    public async Task<IActionResult> ListTasks(long jobId, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        return Json(await tasks.ListByJobAsync(jobId, ct));
    }

    /// <summary>Queues a task by type. Only a type with an active, evaluated binding can be queued.</summary>
    [HttpPost("{jobId:long}/tasks")]
    public async Task<IActionResult> EnqueueTask(long jobId, [FromBody] EnqueueTaskRequest request, CancellationToken ct)
    {
        var audit = Audit();
        var token = await locks.ResolveTokenAsync(jobId, FencingToken(), audit, ct);
        long? approval = null;
        if (request.VisionApprovalId is { } publicApproval)
            approval = (await vision.GetApprovalAsync(jobId, publicApproval, ct) ?? throw new KeyNotFoundException("That approval does not exist.")).VisionApprovalId;
        var task = await tasks.EnqueueByTypeAsync(jobId, request.TaskType ?? "", request.InputManifestRef, approval, token, audit, ct);
        return Json(task, 201);
    }

    [HttpPost("{jobId:long}/tasks/{taskId:long}/cancel")]
    public async Task<IActionResult> CancelTask(long jobId, long taskId, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var existing = await tasks.GetAsync(taskId, ct);
        if (existing is null || existing.JobId != jobId) throw new KeyNotFoundException($"Task {taskId} does not exist on this job.");
        return Json(await tasks.CancelAsync(taskId, Audit(), ct));
    }

    // ------------------------------------------------------------------ what was found, and the count sheet

    [HttpGet("{jobId:long}/entities")]
    public async Task<IActionResult> ListEntities(long jobId, [FromQuery] long? buildingTypeId, [FromQuery] string? reviewStatus, [FromQuery] string? disposition,
        [FromQuery] long? taskId, [FromQuery] int? limit, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        return Json(await entities.ListByJobAsync(jobId, new EntityFilter
        {
            BuildingTypeId = buildingTypeId,
            ReviewStatus = DuctworkParse.OptionalEnum<ReviewStatus>(reviewStatus, "review status"),
            Disposition = DuctworkParse.OptionalEnum<Disposition>(disposition, "disposition"),
            ProcessingTaskId = taskId,
            Limit = Math.Clamp(limit ?? 1000, 1, 5000)
        }, ct));
    }

    /// <summary>The computed count sheet: extended quantities, job totals, and what is still to be determined.</summary>
    [HttpGet("{jobId:long}/count-sheet")]
    public async Task<IActionResult> CountSheet(long jobId, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        return Json(await countSheets.BuildAsync(jobId, ct));
    }
}
