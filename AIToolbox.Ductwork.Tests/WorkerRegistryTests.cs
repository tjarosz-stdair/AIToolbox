using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Data;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Tasks;

namespace AIToolbox.Ductwork.Tests;

public sealed class WorkerRegistryTests(SqlFixture db) : IClassFixture<SqlFixture>
{
    private const string Owner = "owner@example.com";
    private const string Editor = "editor@example.com";

    private WorkerRegistry Registry => new(async ct => await db.OpenAsync());

    private static AuditContext Admin => AuditContext.ForUser("admin@example.com", displayName: "admin", sessionId: "s-admin", isAdmin: true);

    private static AuditContext Worker(int consumer, string name) => AuditContext.ForWorker($"c{consumer}:{name}");

    private static WorkerHello Hello(string version = "1.0.0", params string[] types) =>
        new(ContractInfo.Version, version, "FARM-01", 4, types.Length == 0 ? ["detect"] : types);

    private static int NextConsumer = 1000;
    private static int Consumer() => Interlocked.Increment(ref NextConsumer);

    private Task<int> AuditCountAsync(string action) =>
        db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.SettingsAuditEvent WHERE Category = 'worker-registry' AND Action = @a;", ("@a", action));

    // ---- connecting

    [Fact]
    public async Task A_worker_that_connects_is_recorded_with_what_it_says_about_itself_and_the_first_time_is_audited()
    {
        var consumer = Consumer();
        var before = await AuditCountAsync("worker.registered");

        var w = await Registry.RecordHandshakeAsync(consumer, "farm-1", Hello("2.3.4", "b-type", "a-type"), Worker(consumer, "farm-1"));

        Assert.Equal(($"c{consumer}:farm-1", "FARM-01", "2.3.4", 4, WorkerStatus.Online, 0), (w.WorkerId, w.HostName, w.WorkerVersion, w.MaxConcurrency, w.Status, w.RunningTasks));
        Assert.Equal(["a-type", "b-type"], w.TaskTypes);
        Assert.Equal(before + 1, await AuditCountAsync("worker.registered"));
    }

    [Fact]
    public async Task Connecting_again_updates_the_record_without_a_second_registration_and_keeps_a_drain_request()
    {
        var consumer = Consumer();
        var first = await Registry.RecordHandshakeAsync(consumer, "w", Hello("1.0.0"), Worker(consumer, "w"));
        await Registry.SetDrainAsync(first.WorkerInstanceId, true, Admin);
        var before = await AuditCountAsync("worker.registered");

        var again = await Registry.RecordHandshakeAsync(consumer, "w", Hello("1.1.0", "other"), Worker(consumer, "w"));

        Assert.Equal((first.WorkerInstanceId, "1.1.0", true), (again.WorkerInstanceId, again.WorkerVersion, again.DrainRequested));
        Assert.Equal(["other"], again.TaskTypes);
        Assert.Equal(before, await AuditCountAsync("worker.registered"));
    }

    [Fact]
    public async Task Two_keys_may_use_the_same_worker_name_without_being_one_worker()
    {
        var a = Consumer();
        var b = Consumer();
        await Registry.RecordHandshakeAsync(a, "same", Hello(), Worker(a, "same"));
        await Registry.RecordHandshakeAsync(b, "same", Hello(), Worker(b, "same"));

        var all = (await Registry.ListAsync()).Where(w => w.WorkerName == "same" && w.ApiConsumerId is var c && (c == a || c == b)).ToList();

        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task Only_a_worker_can_introduce_itself_and_the_name_must_be_sensible()
    {
        var consumer = Consumer();

        await Assert.ThrowsAsync<ArgumentException>(() => Registry.RecordHandshakeAsync(consumer, "x", Hello(), AuditContext.ForUser(Owner, sessionId: "s")));
        await Assert.ThrowsAsync<ArgumentException>(() => Registry.RecordHandshakeAsync(consumer, "  ", Hello(), Worker(consumer, "x")));
        await Assert.ThrowsAsync<ArgumentException>(() => Registry.RecordHandshakeAsync(consumer, new string('n', 61), Hello(), Worker(consumer, "x")));
    }

    // ---- being alive

    [Fact]
    public async Task Touching_a_worker_notes_it_is_alive_and_a_claim_is_noted_too_but_a_busy_worker_does_not_write_every_time()
    {
        var consumer = Consumer();
        var w = await Registry.RecordHandshakeAsync(consumer, "t", Hello(), Worker(consumer, "t"));
        Assert.Null(w.LastClaimUtc);

        await Registry.TouchAsync(consumer, "t", claimed: true);
        var claimed = (await Registry.ListAsync()).Single(x => x.WorkerInstanceId == w.WorkerInstanceId);
        Assert.NotNull(claimed.LastClaimUtc);

        var seen = claimed.LastSeenUtc;
        await Task.Delay(30);
        await Registry.TouchAsync(consumer, "t", claimed: false);                         // seen a moment ago, so nothing is written
        Assert.Equal(seen, (await Registry.ListAsync()).Single(x => x.WorkerInstanceId == w.WorkerInstanceId).LastSeenUtc);

        await db.ExecAsync("UPDATE ductwork.WorkerInstance SET LastSeenUtc = DATEADD(MINUTE, -5, SYSUTCDATETIME()) WHERE WorkerInstanceId = @i;", ("@i", w.WorkerInstanceId));
        await Registry.TouchAsync(consumer, "t", claimed: false);
        Assert.True((await Registry.ListAsync()).Single(x => x.WorkerInstanceId == w.WorkerInstanceId).SecondsSinceSeen < 5);
    }

    [Fact]
    public async Task Touching_a_worker_that_never_introduced_itself_does_nothing()
    {
        var consumer = Consumer();

        await Registry.TouchAsync(consumer, "ghost", claimed: true);

        Assert.DoesNotContain(await Registry.ListAsync(), w => w.ApiConsumerId == consumer);
    }

    [Fact]
    public async Task A_worker_not_heard_from_lately_is_offline()
    {
        var consumer = Consumer();
        var w = await Registry.RecordHandshakeAsync(consumer, "quiet", Hello(), Worker(consumer, "quiet"));
        await db.ExecAsync("UPDATE ductwork.WorkerInstance SET LastSeenUtc = DATEADD(MINUTE, -10, SYSUTCDATETIME()) WHERE WorkerInstanceId = @i;", ("@i", w.WorkerInstanceId));

        var listed = (await Registry.ListAsync()).Single(x => x.WorkerInstanceId == w.WorkerInstanceId);

        Assert.Equal(WorkerStatus.Offline, listed.Status);
        Assert.Equal(WorkerStatus.Online, (await Registry.ListAsync(TimeSpan.FromHours(1))).Single(x => x.WorkerInstanceId == w.WorkerInstanceId).Status);
    }

    // ---- what it is running, and draining

    private async Task<ProcessingTaskRecord> RunningTaskAsync(int consumer, string name)
    {
        var jobs = new JobRepository(db.ConnectionString);
        var job = await jobs.CreateAsync(new NewJob { Name = "Registry " + Guid.NewGuid().ToString("N")[..8] }, AuditContext.ForUser(Owner, sessionId: "s-o"));
        await jobs.AddMemberAsync(job.JobId, Editor, JobRole.Editor, AuditContext.ForUser(Owner, sessionId: "s-o"));
        var token = (await new EditLockService(db.ConnectionString).AcquireAsync(job.JobId, AuditContext.ForUser(Editor, sessionId: "s-e"))).Token!;
        await using var connection = await db.OpenAsync();
        var producer = await ProducerStore.EnsureProducerAsync(connection, ProducerRef.Algorithm("reg-" + Guid.NewGuid().ToString("N")[..6], "1.0", "h"));
        var type = "reg-" + Guid.NewGuid().ToString("N")[..8];
        var tasks = new ProcessingTaskRepository(db.ConnectionString);
        await tasks.EnqueueAsync(new NewProcessingTask { JobId = job.JobId, TaskType = type, ProducerId = producer }, token, AuditContext.ForUser(Editor, sessionId: "s-e"));
        return (await tasks.ClaimNextAsync(Worker(consumer, name), [type]))!;
    }

    [Fact]
    public async Task What_a_worker_is_running_is_counted_and_draining_with_work_in_hand_says_so_until_it_finishes()
    {
        var consumer = Consumer();
        var w = await Registry.RecordHandshakeAsync(consumer, "busy", Hello(), Worker(consumer, "busy"));
        var task = await RunningTaskAsync(consumer, "busy");

        Assert.Equal((1, WorkerStatus.Online), (await Registry.ListAsync()).Where(x => x.WorkerInstanceId == w.WorkerInstanceId).Select(x => (x.RunningTasks, x.Status)).Single());

        var draining = await Registry.SetDrainAsync(w.WorkerInstanceId, true, Admin);
        Assert.Equal((WorkerStatus.Draining, "admin@example.com", true), (draining.Status, draining.DrainRequestedBy, await Registry.GetDrainAsync(consumer, "busy")));

        await new ProcessingTaskRepository(db.ConnectionString).FailAsync(task.ProcessingTaskId, Worker(consumer, "busy"), "stopped", retryable: false);
        var drained = (await Registry.ListAsync()).Single(x => x.WorkerInstanceId == w.WorkerInstanceId);
        Assert.Equal((WorkerStatus.Drained, 0), (drained.Status, drained.RunningTasks));
    }

    [Fact]
    public async Task Draining_is_an_administrators_decision_it_can_be_lifted_and_each_change_is_audited()
    {
        var consumer = Consumer();
        var w = await Registry.RecordHandshakeAsync(consumer, "d", Hello(), Worker(consumer, "d"));
        var drains = await AuditCountAsync("worker.drain");
        var resumes = await AuditCountAsync("worker.resume");

        await Assert.ThrowsAsync<JobAccessDeniedException>(() => Registry.SetDrainAsync(w.WorkerInstanceId, true, AuditContext.ForUser(Editor, sessionId: "s")));
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => Registry.SetDrainAsync(w.WorkerInstanceId, true, Worker(consumer, "d")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Registry.SetDrainAsync(int.MaxValue, true, Admin));
        Assert.False(await Registry.GetDrainAsync(consumer, "d"));

        await Registry.SetDrainAsync(w.WorkerInstanceId, true, Admin);
        var lifted = await Registry.SetDrainAsync(w.WorkerInstanceId, false, Admin);

        Assert.Equal((false, (string?)null), (lifted.DrainRequested, lifted.DrainRequestedBy));
        Assert.Equal((drains + 1, resumes + 1), (await AuditCountAsync("worker.drain"), await AuditCountAsync("worker.resume")));
    }

    [Fact]
    public async Task A_worker_that_never_connected_is_not_draining()
    {
        Assert.False(await Registry.GetDrainAsync(Consumer(), "nobody"));
    }

    // ---- forgetting

    [Fact]
    public async Task Only_a_worker_that_is_gone_and_holds_nothing_can_be_forgotten_and_only_by_an_administrator()
    {
        var consumer = Consumer();
        var w = await Registry.RecordHandshakeAsync(consumer, "gone", Hello(), Worker(consumer, "gone"));

        var online = await Assert.ThrowsAsync<JobRuleViolationException>(() => Registry.ForgetAsync(w.WorkerInstanceId, Admin));
        Assert.Contains("still connected", online.Message);
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => Registry.ForgetAsync(w.WorkerInstanceId, AuditContext.ForUser(Editor, sessionId: "s")));

        var task = await RunningTaskAsync(consumer, "gone");
        await db.ExecAsync("UPDATE ductwork.WorkerInstance SET LastSeenUtc = DATEADD(MINUTE, -10, SYSUTCDATETIME()) WHERE WorkerInstanceId = @i;", ("@i", w.WorkerInstanceId));
        var holding = await Assert.ThrowsAsync<JobRuleViolationException>(() => Registry.ForgetAsync(w.WorkerInstanceId, Admin));
        Assert.Contains("running tasks", holding.Message);

        await new ProcessingTaskRepository(db.ConnectionString).FailAsync(task.ProcessingTaskId, Worker(consumer, "gone"), "stopped", retryable: false);
        await Registry.ForgetAsync(w.WorkerInstanceId, Admin);

        Assert.DoesNotContain(await Registry.ListAsync(), x => x.WorkerInstanceId == w.WorkerInstanceId);
        Assert.True(await AuditCountAsync("worker.forgotten") >= 1);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Registry.ForgetAsync(w.WorkerInstanceId, Admin));
    }
}
