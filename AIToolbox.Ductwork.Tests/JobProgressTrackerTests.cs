using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Data;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Progress;
using AIToolbox.Ductwork.Core.Tasks;

namespace AIToolbox.Ductwork.Tests;

/// <summary>Progress as the people on a job see it: each change reported once, only for the jobs somebody has open, whoever made the change.</summary>
public class JobProgressTrackerTests(SqlFixture db) : IClassFixture<SqlFixture>
{
    private const string Owner = "owner@example.com";
    private const string Editor = "editor@example.com";

    private sealed class Collect : IJobProgressSink
    {
        public List<(long JobId, JobProgressEvent Event)> Sent { get; } = [];

        public Task SendAsync(long jobId, IReadOnlyList<JobProgressEvent> events, CancellationToken cancellationToken)
        {
            lock (Sent) Sent.AddRange(events.Select(e => (jobId, e)));
            return Task.CompletedTask;
        }

        public List<JobProgressEvent> For(long jobId) { lock (Sent) return Sent.Where(s => s.JobId == jobId).Select(s => s.Event).ToList(); }
    }

    private JobRepository Jobs() => new(db.ConnectionString);
    private ProcessingTaskRepository Tasks() => new(db.ConnectionString);
    private EditLockService Locks() => new(db.ConnectionString);

    private static AuditContext Ctx(string email) =>
        AuditContext.ForUser(email, displayName: email.Split('@')[0], sessionId: "s-" + email, clientIp: "10.0.0.3", userAgent: "tests");

    private static string UniqueType() => "t-" + Guid.NewGuid().ToString("N")[..10];

    private (JobProgressTracker Tracker, Collect Sink) NewTracker()
    {
        var sink = new Collect();
        return (new JobProgressTracker(ct => db.OpenAsync(), sink), sink);
    }

    private async Task<(JobRecord Job, EditToken Token)> NewJobAsync()
    {
        var job = await Jobs().CreateAsync(new NewJob { Name = "Progress job " + Guid.NewGuid().ToString("N")[..8] }, Ctx(Owner));
        await Jobs().AddMemberAsync(job.JobId, Editor, JobRole.Editor, Ctx(Owner));
        return (job, (await Locks().AcquireAsync(job.JobId, Ctx(Editor))).Token!);
    }

    private async Task<ProcessingTaskRecord> EnqueueAsync(JobRecord job, EditToken token, string type)
    {
        await using var connection = await db.OpenAsync();
        var producer = await ProducerStore.EnsureProducerAsync(connection, ProducerRef.Algorithm("progress-" + type, "0.1.0", "h"));
        return await Tasks().EnqueueAsync(new NewProcessingTask { JobId = job.JobId, TaskType = type, ProducerId = producer, MaxAttempts = 3, InputManifestRef = "inputs/x" }, token, Ctx(Editor));
    }

    [Fact]
    public async Task Nothing_is_read_or_sent_while_nobody_is_watching()
    {
        var (job, token) = await NewJobAsync();
        await EnqueueAsync(job, token, UniqueType());
        var (tracker, sink) = NewTracker();

        Assert.Equal(0, await tracker.PollOnceAsync());

        Assert.Empty(sink.Sent);
        Assert.Empty(tracker.WatchedJobs);
    }

    [Fact]
    public async Task A_task_is_reported_when_it_changes_and_not_again_until_it_changes_again()
    {
        var (job, token) = await NewJobAsync();
        var type = UniqueType();
        var task = await EnqueueAsync(job, token, type);
        var (tracker, sink) = NewTracker();
        tracker.Watch(job.JobId);

        await tracker.PollOnceAsync();
        await tracker.PollOnceAsync();
        Assert.Equal(["Queued"], sink.For(job.JobId).Select(e => e.Status));                   // seen once, not repeated

        await Tasks().ClaimNextAsync(AuditContext.ForWorker("worker-1"), [type]);
        await Tasks().HeartbeatAsync(task.ProcessingTaskId, "worker-1", 3, 10, "page 3 of 10");
        await tracker.PollOnceAsync();
        var running = sink.For(job.JobId).Last();
        Assert.Equal((JobProgressEvent.TaskKind, task.ProcessingTaskId.ToString(), "Running", 3L, 10L, "page 3 of 10"), (running.Kind, running.Id, running.Status, running.Done, running.Total, running.Message));

        await Tasks().HeartbeatAsync(task.ProcessingTaskId, "worker-1", 7, 10, "page 7 of 10");
        await tracker.PollOnceAsync();
        Assert.Equal(7L, sink.For(job.JobId).Last().Done);

        await Tasks().CompleteAsync(task.ProcessingTaskId, AuditContext.ForWorker("worker-1"), null);
        await tracker.PollOnceAsync();
        var done = sink.For(job.JobId).Last();
        Assert.Equal(("Succeeded", true), (done.Status, done.IsTerminal));
        var count = sink.For(job.JobId).Count;
        await tracker.PollOnceAsync();
        Assert.Equal(count, sink.For(job.JobId).Count);
    }

    [Fact]
    public async Task Only_the_watched_jobs_are_reported_and_each_goes_with_its_own_job()
    {
        var (mine, myToken) = await NewJobAsync();
        var (other, otherToken) = await NewJobAsync();
        await EnqueueAsync(mine, myToken, UniqueType());
        await EnqueueAsync(other, otherToken, UniqueType());
        var (tracker, sink) = NewTracker();
        tracker.Watch(mine.JobId);

        await tracker.PollOnceAsync();

        Assert.Single(sink.For(mine.JobId));
        Assert.Empty(sink.For(other.JobId));
        Assert.All(sink.Sent, s => Assert.Equal(mine.JobId, s.Event.JobId));
    }

    [Fact]
    public async Task Opening_a_job_reports_only_what_is_still_active_not_its_finished_history()
    {
        var (job, token) = await NewJobAsync();
        var finishedType = UniqueType();
        var finished = await EnqueueAsync(job, token, finishedType);
        await Tasks().ClaimNextAsync(AuditContext.ForWorker("worker-1"), [finishedType]);
        await Tasks().CompleteAsync(finished.ProcessingTaskId, AuditContext.ForWorker("worker-1"), null);
        var waiting = await EnqueueAsync(job, token, UniqueType());
        var (tracker, sink) = NewTracker();
        tracker.Watch(job.JobId);

        await tracker.PollOnceAsync();

        Assert.Equal([waiting.ProcessingTaskId.ToString()], sink.For(job.JobId).Select(e => e.Id));
    }

    [Fact]
    public async Task A_job_stays_watched_until_the_last_watcher_leaves_and_then_starts_fresh()
    {
        var (job, token) = await NewJobAsync();
        await EnqueueAsync(job, token, UniqueType());
        var (tracker, sink) = NewTracker();
        tracker.Watch(job.JobId);
        tracker.Watch(job.JobId);
        await tracker.PollOnceAsync();
        Assert.Single(sink.For(job.JobId));

        tracker.Unwatch(job.JobId);
        Assert.Equal([job.JobId], tracker.WatchedJobs);
        tracker.Unwatch(job.JobId);
        Assert.Empty(tracker.WatchedJobs);
        tracker.Unwatch(job.JobId);                                                           // leaving twice is harmless

        tracker.Watch(job.JobId);
        await tracker.PollOnceAsync();
        Assert.Equal(2, sink.For(job.JobId).Count);                                           // the active task is reported again to the new watcher
    }
}
