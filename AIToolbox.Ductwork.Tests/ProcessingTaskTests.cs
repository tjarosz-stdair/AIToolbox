using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Counting;
using AIToolbox.Ductwork.Core.Data;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Tasks;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Tests;

public class ProcessingTaskTests(SqlFixture db) : IClassFixture<SqlFixture>
{
    private const string Owner = "owner@example.com";
    private const string Editor = "editor@example.com";
    private const string Viewer = "viewer@example.com";

    private static readonly TaskSettings Quick = new()
    {
        LeaseDuration = TimeSpan.FromMilliseconds(500),
        RetryBackoffBase = TimeSpan.FromMilliseconds(300),
        RetryBackoffMax = TimeSpan.FromMilliseconds(600)
    };

    private JobRepository Jobs(ICompletionProbe? probe = null) => new(db.ConnectionString, probe);
    private ProcessingTaskRepository Tasks(TaskSettings? settings = null) => new(db.ConnectionString, settings);
    private EditLockService Locks() => new(db.ConnectionString);

    private static AuditContext Ctx(string email) =>
        AuditContext.ForUser(email, displayName: email.Split('@')[0], sessionId: "s-" + email, clientIp: "10.0.0.3", userAgent: "tests");

    private static AuditContext W(string workerId) => AuditContext.ForWorker(workerId);

    private async Task<long> ProducerAsync(string name = "residential-label-counter")
    {
        await using var connection = await db.OpenAsync();
        return await ProducerStore.EnsureProducerAsync(connection, ProducerRef.Algorithm(name, "0.1.0", "hash-" + name));
    }

    private async Task<(JobRecord Job, EditToken Token)> NewJobAsync()
    {
        var job = await Jobs().CreateAsync(new NewJob { Name = "Task job " + Guid.NewGuid().ToString("N")[..8] }, Ctx(Owner));
        await Jobs().AddMemberAsync(job.JobId, Editor, JobRole.Editor, Ctx(Owner));
        await Jobs().AddMemberAsync(job.JobId, Viewer, JobRole.Viewer, Ctx(Owner));
        var token = (await Locks().AcquireAsync(job.JobId, Ctx(Editor))).Token!;
        return (job, token);
    }

    private async Task<ProcessingTaskRecord> EnqueueAsync(JobRecord job, EditToken token, string type = "detect-residential-labels", int maxAttempts = 3,
        TaskSettings? settings = null) =>
        await Tasks(settings).EnqueueAsync(new NewProcessingTask
        {
            JobId = job.JobId, TaskType = type, ProducerId = await ProducerAsync(), MaxAttempts = maxAttempts, InputManifestRef = "inputs/" + type
        }, token, Ctx(Editor));

    // Tasks from every test share one database, so claims name their task type to stay out of each other's way.
    private static string UniqueType() => "t-" + Guid.NewGuid().ToString("N")[..10];

    // ---- enqueue

    [Fact]
    public async Task Queuing_work_needs_the_lock_and_records_who_what_and_for_which_producer()
    {
        var (job, token) = await NewJobAsync();
        var type = UniqueType();

        var task = await EnqueueAsync(job, token, type);

        Assert.Equal((ProcessingTaskStatus.Queued, 0, 3), (task.Status, task.Attempt, task.MaxAttempts));
        Assert.Equal(ContractInfo.Version, task.ContractVersion);
        Assert.Equal(Editor, task.CreatedBy);
        var created = (await Jobs().ListAuditAsync(job.JobId)).Single(e => e.Action == "task.enqueued");
        Assert.Equal((task.ProcessingTaskId, task.ProducerId), (created.ProcessingTaskId, created.ProducerId));
        Assert.Equal("processing", created.Category);
    }

    [Fact]
    public async Task Viewers_cannot_queue_work_and_a_job_that_cannot_be_edited_takes_none()
    {
        var (job, token) = await NewJobAsync();
        var producer = await ProducerAsync();
        var work = new NewProcessingTask { JobId = job.JobId, TaskType = "x", ProducerId = producer };

        await Assert.ThrowsAsync<JobAccessDeniedException>(() => Tasks().EnqueueAsync(work, token, Ctx(Viewer)));

        await Jobs().CloseAsync(job.JobId, "done", Ctx(Editor));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Tasks().EnqueueAsync(work, token, Ctx(Editor)));
    }

    [Fact]
    public async Task A_stale_token_cannot_queue_work()
    {
        var (job, token) = await NewJobAsync();
        await Locks().ForceTakeoverAsync(job.JobId, Ctx(Owner));
        var work = new NewProcessingTask { JobId = job.JobId, TaskType = "x", ProducerId = await ProducerAsync() };

        await Assert.ThrowsAsync<StaleEditTokenException>(() => Tasks().EnqueueAsync(work, token, Ctx(Editor)));
        Assert.Empty(await Tasks().ListByJobAsync(job.JobId));
    }

    [Fact]
    public async Task Work_that_sends_drawings_to_a_hosted_model_cannot_be_queued_without_an_approval()
    {
        var (job, token) = await NewJobAsync();
        var producer = await ProducerAsync();

        var refused = await Assert.ThrowsAsync<JobRuleViolationException>(() => Tasks().EnqueueAsync(
            new NewProcessingTask { JobId = job.JobId, TaskType = "crop-classify", ProducerId = producer, SendsDrawingContentToHostedModel = true }, token, Ctx(Editor)));
        Assert.Contains("approval", refused.Message);

        var approved = await Tasks().EnqueueAsync(
            new NewProcessingTask { JobId = job.JobId, TaskType = "crop-classify", ProducerId = producer, SendsDrawingContentToHostedModel = true, VisionApprovalId = 77 },
            token, Ctx(Editor));
        Assert.Equal(77, approved.VisionApprovalId);

        // And the database refuses it even if the check above were bypassed.
        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync(
            "INSERT INTO ductwork.ProcessingTask (JobId, TaskType, ProducerId, ContractVersion, SendsDrawingContentToHostedModel, CreatedBy) VALUES (@j, 'x', @p, '0.1.0', 1, 'x');",
            ("@j", job.JobId), ("@p", producer)));
    }

    [Fact]
    public async Task A_child_task_must_belong_to_the_same_job()
    {
        var (a, tokenA) = await NewJobAsync();
        var (b, tokenB) = await NewJobAsync();
        var parent = await EnqueueAsync(a, tokenA);

        var child = await Tasks().EnqueueAsync(new NewProcessingTask { JobId = a.JobId, TaskType = "child", ProducerId = parent.ProducerId, ParentTaskId = parent.ProcessingTaskId }, tokenA, Ctx(Editor));
        Assert.Equal(parent.ProcessingTaskId, child.ParentTaskId);
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Tasks().EnqueueAsync(
            new NewProcessingTask { JobId = b.JobId, TaskType = "child", ProducerId = parent.ProducerId, ParentTaskId = parent.ProcessingTaskId }, tokenB, Ctx(Editor)));
    }

    // ---- claiming

    [Fact]
    public async Task A_worker_claims_the_oldest_matching_task_which_becomes_running_with_a_lease()
    {
        var (job, token) = await NewJobAsync();
        var type = UniqueType();
        var first = await EnqueueAsync(job, token, type);
        var second = await EnqueueAsync(job, token, type);

        var claimed = await Tasks().ClaimNextAsync(W("worker-1"), [type]);

        Assert.Equal(first.ProcessingTaskId, claimed!.ProcessingTaskId);
        Assert.Equal((ProcessingTaskStatus.Running, "worker-1", 1), (claimed.Status, claimed.LeaseOwner, claimed.Attempt));
        Assert.True(claimed.LeaseExpiresUtc > DateTime.UtcNow.AddSeconds(30));
        Assert.NotNull(claimed.StartedUtc);
        Assert.Equal(ProcessingTaskStatus.Queued, (await Tasks().GetAsync(second.ProcessingTaskId))!.Status);

        var started = (await Jobs().ListAuditAsync(job.JobId)).Single(e => e.Action == "task.started");
        Assert.Equal((ActorType.Worker, "worker-1", first.ProcessingTaskId), (started.ActorType, started.ActorDisplayName, started.ProcessingTaskId));
    }

    [Fact]
    public async Task Nothing_is_claimed_when_nothing_matches()
    {
        Assert.Null(await Tasks().ClaimNextAsync(W("worker-1"), [UniqueType()]));
        await Assert.ThrowsAsync<ArgumentException>(() => Tasks().ClaimNextAsync(W(" ")));
        await Assert.ThrowsAsync<ArgumentException>(() => Tasks().ClaimNextAsync(Ctx(Editor)));   // a user is not a worker
    }

    [Fact]
    public async Task Many_workers_claiming_at_once_each_get_a_different_task_and_none_is_given_twice()
    {
        var (job, token) = await NewJobAsync();
        var type = UniqueType();
        for (var i = 0; i < 6; i++) await EnqueueAsync(job, token, type);

        var claims = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Tasks().ClaimNextAsync(W($"worker-{i}"), [type])));

        var got = claims.Where(c => c is not null).Select(c => c!.ProcessingTaskId).ToList();
        Assert.Equal(6, got.Count);
        Assert.Equal(6, got.Distinct().Count());
        Assert.True((await Jobs().VerifyAuditChainAsync(job.JobId)).IsValid);
    }

    // ---- leases, heartbeat and reclaim

    [Fact]
    public async Task Heartbeats_extend_the_lease_record_progress_and_only_the_holder_may_send_them()
    {
        var (job, token) = await NewJobAsync();
        var type = UniqueType();
        var tasks = Tasks(Quick);
        var task = await EnqueueAsync(job, token, type);
        var claimed = (await tasks.ClaimNextAsync(W("worker-1"), [type]))!;

        await Task.Delay(300);
        var beat = await tasks.HeartbeatAsync(task.ProcessingTaskId, "worker-1", 3, 10, "page 3 of 10");
        Assert.True(beat.Continue);

        var after = (await tasks.GetAsync(task.ProcessingTaskId))!;
        Assert.Equal((3, 10, "page 3 of 10"), (after.ProgressDone, after.ProgressTotal, after.ProgressMessage));
        Assert.True(after.LeaseExpiresUtc > claimed.LeaseExpiresUtc);

        Assert.False((await tasks.HeartbeatAsync(task.ProcessingTaskId, "worker-2", 4, 10, null)).Continue);
        Assert.False((await tasks.HeartbeatAsync(long.MaxValue, "worker-1", 4, 10, null)).Continue);
        Assert.DoesNotContain(await Jobs().ListAuditAsync(job.JobId), e => e.Action.Contains("heartbeat"));   // liveness is not audited
    }

    [Fact]
    public async Task A_worker_that_stops_heartbeating_loses_the_task_to_another_worker()
    {
        var (job, token) = await NewJobAsync();
        var type = UniqueType();
        var tasks = Tasks(Quick);
        var task = await EnqueueAsync(job, token, type);
        await tasks.ClaimNextAsync(W("worker-1"), [type]);

        Assert.Null(await tasks.ClaimNextAsync(W("worker-2"), [type]));   // the lease is still live
        await Task.Delay(800);

        var reclaimed = (await tasks.ClaimNextAsync(W("worker-2"), [type]))!;
        Assert.Equal((task.ProcessingTaskId, "worker-2", 2), (reclaimed.ProcessingTaskId, reclaimed.LeaseOwner, reclaimed.Attempt));
        Assert.Contains("task.reclaimed", (await Jobs().ListAuditAsync(job.JobId)).Select(e => e.Action));

        // The first worker's late report is refused, so it cannot overwrite the new holder's work.
        Assert.False((await tasks.HeartbeatAsync(task.ProcessingTaskId, "worker-1", 1, 2, null)).Continue);
        Assert.False(await tasks.CompleteAsync(task.ProcessingTaskId, W("worker-1"), "late.json"));
        Assert.Equal(ProcessingTaskStatus.Running, (await tasks.GetAsync(task.ProcessingTaskId))!.Status);
        Assert.True(await tasks.CompleteAsync(task.ProcessingTaskId, W("worker-2"), "result.json"));
    }

    [Fact]
    public async Task A_task_whose_workers_keep_dying_is_failed_once_its_attempts_are_used()
    {
        var (job, token) = await NewJobAsync();
        var type = UniqueType();
        var tasks = Tasks(Quick);
        var task = await EnqueueAsync(job, token, type, maxAttempts: 1);
        await tasks.ClaimNextAsync(W("worker-1"), [type]);
        await Task.Delay(800);

        Assert.Null(await tasks.ClaimNextAsync(W("worker-2"), [type]));

        var done = (await tasks.GetAsync(task.ProcessingTaskId))!;
        Assert.Equal(ProcessingTaskStatus.Failed, done.Status);
        Assert.Contains("no attempts are left", done.ErrorMessage);
        Assert.Null(done.LeaseOwner);
        Assert.NotNull(done.FinishedUtc);
        Assert.Contains("task.failed", (await Jobs().ListAuditAsync(job.JobId)).Select(e => e.Action));
    }

    // ---- finishing

    [Fact]
    public async Task Completing_records_the_result_clears_the_lease_and_is_audited()
    {
        var (job, token) = await NewJobAsync();
        var type = UniqueType();
        var tasks = Tasks();
        var task = await EnqueueAsync(job, token, type);
        await tasks.ClaimNextAsync(W("worker-1"), [type]);
        await tasks.HeartbeatAsync(task.ProcessingTaskId, "worker-1", 4, 8, "half");

        Assert.True(await tasks.CompleteAsync(task.ProcessingTaskId, W("worker-1"), "results/manifest.json"));

        var done = (await tasks.GetAsync(task.ProcessingTaskId))!;
        Assert.Equal((ProcessingTaskStatus.Succeeded, "results/manifest.json", 8), (done.Status, done.ResultManifestRef, done.ProgressDone));
        Assert.Null(done.LeaseOwner);
        Assert.NotNull(done.FinishedUtc);
        Assert.False(await tasks.CompleteAsync(task.ProcessingTaskId, W("worker-1"), "again.json"));   // only once
        Assert.Contains("task.finished", (await Jobs().ListAuditAsync(job.JobId)).Select(e => e.Action));
    }

    [Fact]
    public async Task A_retryable_failure_waits_a_growing_delay_then_runs_again_and_a_permanent_one_does_not()
    {
        var (job, token) = await NewJobAsync();
        var type = UniqueType();
        var tasks = Tasks(Quick);
        var task = await EnqueueAsync(job, token, type);
        await tasks.ClaimNextAsync(W("worker-1"), [type]);

        Assert.True(await tasks.FailAsync(task.ProcessingTaskId, W("worker-1"), "provider timed out", retryable: true));
        var waiting = (await tasks.GetAsync(task.ProcessingTaskId))!;
        Assert.Equal((ProcessingTaskStatus.Queued, "provider timed out"), (waiting.Status, waiting.ErrorMessage));
        Assert.NotNull(waiting.NotBeforeUtc);
        Assert.Null(await tasks.ClaimNextAsync(W("worker-1"), [type]));          // not yet

        await Task.Delay(600);
        var second = (await tasks.ClaimNextAsync(W("worker-1"), [type]))!;
        Assert.Equal(2, second.Attempt);

        Assert.True(await tasks.FailAsync(task.ProcessingTaskId, W("worker-1"), "bad input", retryable: false));
        Assert.Equal(ProcessingTaskStatus.Failed, (await tasks.GetAsync(task.ProcessingTaskId))!.Status);
        var actions = (await Jobs().ListAuditAsync(job.JobId)).Select(e => e.Action).ToList();
        Assert.Contains("task.retrying", actions);
        Assert.Contains("task.failed", actions);
    }

    [Fact]
    public async Task A_retryable_failure_on_the_last_attempt_fails_for_good()
    {
        var (job, token) = await NewJobAsync();
        var type = UniqueType();
        var tasks = Tasks();
        var task = await EnqueueAsync(job, token, type, maxAttempts: 1);
        await tasks.ClaimNextAsync(W("worker-1"), [type]);

        await tasks.FailAsync(task.ProcessingTaskId, W("worker-1"), "out of memory", retryable: true);

        Assert.Equal(ProcessingTaskStatus.Failed, (await tasks.GetAsync(task.ProcessingTaskId))!.Status);
    }

    // ---- cancelling

    [Fact]
    public async Task Cancelling_a_queued_task_is_immediate_and_a_running_one_is_asked_to_stop()
    {
        var (job, token) = await NewJobAsync();
        var type = UniqueType();
        var tasks = Tasks();
        var running = await EnqueueAsync(job, token, type);
        var queued = await EnqueueAsync(job, token, type);
        await tasks.ClaimNextAsync(W("worker-1"), [type]);          // takes the older one, leaving the other queued

        var cancelledQueued = await tasks.CancelAsync(queued.ProcessingTaskId, Ctx(Editor));
        var asked = await tasks.CancelAsync(running.ProcessingTaskId, Ctx(Editor));

        Assert.Equal((ProcessingTaskStatus.Cancelled, false), (cancelledQueued.Status, cancelledQueued.CancelRequested));
        Assert.NotNull(cancelledQueued.FinishedUtc);
        Assert.Equal((ProcessingTaskStatus.Running, true), (asked.Status, asked.CancelRequested));
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => tasks.CancelAsync(asked.ProcessingTaskId, Ctx(Viewer)));
    }

    [Fact]
    public async Task A_running_task_that_is_cancelled_tells_its_worker_on_the_next_heartbeat_and_ends_cancelled()
    {
        var (job, token) = await NewJobAsync();
        var type = UniqueType();
        var tasks = Tasks();
        var task = await EnqueueAsync(job, token, type);
        await tasks.ClaimNextAsync(W("worker-1"), [type]);

        var asked = await tasks.CancelAsync(task.ProcessingTaskId, Ctx(Editor));
        Assert.Equal((ProcessingTaskStatus.Running, true), (asked.Status, asked.CancelRequested));

        var beat = await tasks.HeartbeatAsync(task.ProcessingTaskId, "worker-1", 1, 5, null);
        Assert.Equal((false, true), (beat.Continue, beat.CancelRequested));

        Assert.False(await tasks.CompleteAsync(task.ProcessingTaskId, W("worker-1"), "ignored.json"));   // a cancelled task never reports success
        var done = (await tasks.GetAsync(task.ProcessingTaskId))!;
        Assert.Equal(ProcessingTaskStatus.Cancelled, done.Status);
        Assert.Null(done.ResultManifestRef);
    }

    [Fact]
    public async Task Cancelling_something_already_finished_changes_nothing()
    {
        var (job, token) = await NewJobAsync();
        var type = UniqueType();
        var tasks = Tasks();
        var task = await EnqueueAsync(job, token, type);
        await tasks.ClaimNextAsync(W("worker-1"), [type]);
        await tasks.CompleteAsync(task.ProcessingTaskId, W("worker-1"), "r.json");
        var events = (await Jobs().ListAuditAsync(job.JobId)).Count;

        var result = await tasks.CancelAsync(task.ProcessingTaskId, Ctx(Editor));

        Assert.Equal(ProcessingTaskStatus.Succeeded, result.Status);
        Assert.Equal(events, (await Jobs().ListAuditAsync(job.JobId)).Count);
    }

    // ---- completing a job

    [Fact]
    public async Task A_job_cannot_be_completed_or_closed_while_it_has_work_queued_or_running()
    {
        var (job, token) = await NewJobAsync();
        var type = UniqueType();
        var probe = new JobCompletionProbe();
        var jobs = Jobs(probe);
        foreach (var step in Enum.GetValues<WorkflowStep>())
            await jobs.SetWorkflowStepAsync(job.JobId, step, StepStatus.Done, token, Ctx(Editor));
        var task = await EnqueueAsync(job, token, type);

        var blocked = await Assert.ThrowsAsync<JobRuleViolationException>(() => jobs.BeginCompleteAsync(job.JobId, Ctx(Editor)));
        Assert.Contains("1 processing task", blocked.Message);
        await Assert.ThrowsAsync<JobRuleViolationException>(() => jobs.CloseAsync(job.JobId, "reason", Ctx(Editor)));
        Assert.Equal(1, await Tasks().GetActiveCountAsync(job.JobId));

        await Tasks().CancelAsync(task.ProcessingTaskId, Ctx(Editor));
        Assert.Equal(0, await Tasks().GetActiveCountAsync(job.JobId));
        var completing = await jobs.BeginCompleteAsync(job.JobId, Ctx(Editor));
        Assert.Equal(JobState.Completing, completing.State);
    }

    // ---- structure

    [Fact]
    public async Task The_database_keeps_running_tasks_leased_and_finished_tasks_unleased()
    {
        var (job, _) = await NewJobAsync();
        var producer = await ProducerAsync();

        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync(
            "INSERT INTO ductwork.ProcessingTask (JobId, TaskType, Status, ProducerId, ContractVersion, CreatedBy) VALUES (@j, 'x', 'Running', @p, '0.1.0', 'x');",
            ("@j", job.JobId), ("@p", producer)));
        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync(
            "INSERT INTO ductwork.ProcessingTask (JobId, TaskType, Status, ProducerId, ContractVersion, CreatedBy) VALUES (@j, 'x', 'Succeeded', @p, '0.1.0', 'x');",
            ("@j", job.JobId), ("@p", producer)));                                    // finished without a finish time
        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync(
            "INSERT INTO ductwork.ProcessingTask (JobId, TaskType, Attempt, MaxAttempts, ProducerId, ContractVersion, CreatedBy) VALUES (@j, 'x', 4, 3, @p, '0.1.0', 'x');",
            ("@j", job.JobId), ("@p", producer)));                                    // more attempts than allowed
        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync(
            "INSERT INTO ductwork.ProcessingTask (JobId, TaskType, ProducerId, ContractVersion, CreatedBy) VALUES (@j, 'x', 999999999, '0.1.0', 'x');",
            ("@j", job.JobId)));                                                      // a task needs a real producer
    }
}
