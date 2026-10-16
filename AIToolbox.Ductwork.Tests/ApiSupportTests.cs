using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Activity;
using AIToolbox.Ductwork.Core.Data;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Tasks;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Tests;

public class ApiSupportTests(SqlFixture db) : IClassFixture<SqlFixture>
{
    private const string Owner = "owner@example.com";
    private const string Editor = "editor@example.com";
    private const string Viewer = "viewer@example.com";

    private JobRepository Jobs() => new(db.ConnectionString);
    private TaskBindingRepository Bindings() => new(db.ConnectionString);
    private ProcessingTaskRepository Tasks() => new(db.ConnectionString);
    private EditLockService Locks() => new(db.ConnectionString);

    private static AuditContext Ctx(string email, bool admin = false, string? session = null) =>
        AuditContext.ForUser(email, displayName: email.Split('@')[0], sessionId: session ?? "s-" + email, clientIp: "10.0.0.5", userAgent: "tests", isAdmin: admin);

    private static AuditContext Admin => Ctx("admin@example.com", admin: true);

    private async Task<(long ProducerId, int EvalRunId)> EvaluatedAlgorithmAsync(string name = "residential-label-counter")
    {
        await using var connection = await db.OpenAsync();
        var producer = await ProducerStore.EnsureProducerAsync(connection, ProducerRef.Algorithm(name + "-" + Guid.NewGuid().ToString("N")[..6], "1.0", "hash"));
        return (producer, await EvalRunAsync(producer));
    }

    private async Task<int> EvalRunAsync(long producerId)
    {
        var goldSet = await db.ScalarAsync<int>("INSERT INTO ductwork.GoldSet (Name, Role) OUTPUT INSERTED.GoldSetId VALUES (@n, 'Development');", ("@n", "gs-" + Guid.NewGuid().ToString("N")));
        // EvalRun has a trigger (the holdout guard), so no OUTPUT clause.
        return await db.ScalarAsync<int>(
            "INSERT INTO ductwork.EvalRun (GoldSetId, ProducerId, TaskKey, MatchMode, ContractVersion) VALUES (@g, @p, 'bind', 'Count', '0.1.0'); SELECT CAST(SCOPE_IDENTITY() AS INT);",
            ("@g", goldSet), ("@p", producerId));
    }

    private async Task<(JobRecord Job, EditToken Token)> NewJobAsync()
    {
        var job = await Jobs().CreateAsync(new NewJob { Name = "Api job " + Guid.NewGuid().ToString("N")[..8] }, Ctx(Owner));
        await Jobs().AddMemberAsync(job.JobId, Editor, JobRole.Editor, Ctx(Owner));
        await Jobs().AddMemberAsync(job.JobId, Viewer, JobRole.Viewer, Ctx(Owner));
        return (job, (await Locks().AcquireAsync(job.JobId, Ctx(Editor))).Token!);
    }

    private static string UniqueType() => "type-" + Guid.NewGuid().ToString("N")[..10];

    // ---- task bindings: the evaluation gate

    [Fact]
    public async Task A_task_type_is_enabled_by_binding_a_producer_to_an_evaluation_of_that_producer()
    {
        var (producer, run) = await EvaluatedAlgorithmAsync();
        var type = UniqueType();

        var binding = await Bindings().BindAsync(type, producer, run, false, 4, Admin);

        Assert.Equal((type, producer, run, 4, true), (binding.TaskType, binding.ProducerId, binding.EvalRunId, binding.MaxAttempts, binding.IsActive));
        Assert.Equal(binding.TaskBindingId, (await Bindings().GetActiveAsync(type))!.TaskBindingId);
    }

    [Fact]
    public async Task Only_an_administrator_changes_bindings_and_every_change_is_recorded()
    {
        var (producer, run) = await EvaluatedAlgorithmAsync();
        var type = UniqueType();

        await Assert.ThrowsAsync<JobAccessDeniedException>(() => Bindings().BindAsync(type, producer, run, false, 3, Ctx(Editor)));
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => Bindings().RetireAsync(type, Ctx(Editor)));

        await Bindings().BindAsync(type, producer, run, false, 3, Admin);
        await Bindings().RetireAsync(type, Admin);

        var actions = new List<string>();
        await using var connection = await db.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Action FROM ductwork.SettingsAuditEvent WHERE Summary LIKE '%' + @t + '%' ORDER BY SettingsAuditEventId;";
        command.Parameters.AddWithValue("@t", type);
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) actions.Add(reader.GetString(0));
        Assert.Equal(["binding.created", "binding.retired"], actions);
    }

    [Fact]
    public async Task A_binding_cannot_rest_on_an_evaluation_of_a_different_producer_or_on_none()
    {
        var (producerA, runA) = await EvaluatedAlgorithmAsync("a");
        var (producerB, _) = await EvaluatedAlgorithmAsync("b");

        var wrong = await Assert.ThrowsAsync<JobRuleViolationException>(() => Bindings().BindAsync(UniqueType(), producerB, runA, false, 3, Admin));
        Assert.Contains("different producer", wrong.Message);
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Bindings().BindAsync(UniqueType(), producerA, 999_999, false, 3, Admin));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Bindings().BindAsync(" ", producerA, runA, false, 3, Admin));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Bindings().BindAsync(UniqueType(), producerA, runA, false, 0, Admin));
        // The database refuses a binding with no run at all.
        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync(
            "INSERT INTO ductwork.TaskBinding (TaskType, ProducerId, EvalRunId, CreatedBy) VALUES ('x', @p, 999999, 'x');", ("@p", producerA)));
    }

    [Fact]
    public async Task Only_a_vision_enabled_model_can_be_bound_to_work_that_sends_drawings_to_a_hosted_model()
    {
        await using var connection = await db.OpenAsync();
        var visionSnapshot = await ProducerStore.EnsureProviderSnapshotAsync(connection, new ProviderSnapshotInfo(2, "vision.example.invalid", "m-" + Guid.NewGuid().ToString("N")[..6], null, 4000, true, "Microsoft Foundry"));
        var textSnapshot = await ProducerStore.EnsureProviderSnapshotAsync(connection, new ProviderSnapshotInfo(2, "text.example.invalid", "m-" + Guid.NewGuid().ToString("N")[..6], null, 4000, false, null));
        var vision = await ProducerStore.EnsureProducerAsync(connection, ProducerRef.Llm(1000 + visionSnapshot, visionSnapshot));
        var text = await ProducerStore.EnsureProducerAsync(connection, ProducerRef.Llm(2000 + textSnapshot, textSnapshot));
        var algorithm = (await EvaluatedAlgorithmAsync()).ProducerId;

        await Bindings().BindAsync(UniqueType(), vision, await EvalRunAsync(vision), true, 2, Admin);
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Bindings().BindAsync(UniqueType(), text, EvalRunAsync(text).Result, true, 2, Admin));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Bindings().BindAsync(UniqueType(), algorithm, EvalRunAsync(algorithm).Result, true, 2, Admin));
    }

    [Fact]
    public async Task Rebinding_retires_the_old_binding_and_keeps_the_history()
    {
        var (p1, r1) = await EvaluatedAlgorithmAsync("one");
        var (p2, r2) = await EvaluatedAlgorithmAsync("two");
        var type = UniqueType();

        var first = await Bindings().BindAsync(type, p1, r1, false, 3, Admin);
        var second = await Bindings().BindAsync(type, p2, r2, false, 3, Admin);

        Assert.Equal(second.TaskBindingId, (await Bindings().GetActiveAsync(type))!.TaskBindingId);
        var all = (await Bindings().ListAsync(includeRetired: true)).Where(b => b.TaskType == type).ToList();
        Assert.Equal(2, all.Count);
        Assert.NotNull(all.Single(b => b.TaskBindingId == first.TaskBindingId).RetiredUtc);
        Assert.Single(await Bindings().ListAsync(), b => b.TaskType == type);
        Assert.False(await Bindings().RetireAsync("never-bound-" + type, Admin));
    }

    [Fact]
    public async Task The_settings_audit_log_is_append_only()
    {
        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync("UPDATE ductwork.SettingsAuditEvent SET Summary = 'x';"));
        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync("DELETE FROM ductwork.SettingsAuditEvent;"));
    }

    // ---- enqueue by type

    [Fact]
    public async Task Queuing_by_type_uses_the_active_binding_and_refuses_a_type_with_none_or_a_retired_one()
    {
        var (job, token) = await NewJobAsync();
        var (producer, run) = await EvaluatedAlgorithmAsync();
        var type = UniqueType();

        var refused = await Assert.ThrowsAsync<JobRuleViolationException>(() => Tasks().EnqueueByTypeAsync(job.JobId, type, null, null, token, Ctx(Editor)));
        Assert.Contains("no active, evaluated binding", refused.Message);

        await Bindings().BindAsync(type, producer, run, false, 5, Admin);
        var queued = await Tasks().EnqueueByTypeAsync(job.JobId, type, "inputs/set-1", null, token, Ctx(Editor));
        Assert.Equal((producer, 5, "inputs/set-1", false), (queued.ProducerId, queued.MaxAttempts, queued.InputManifestRef, queued.SendsDrawingContentToHostedModel));

        await Bindings().RetireAsync(type, Admin);
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Tasks().EnqueueByTypeAsync(job.JobId, type, null, null, token, Ctx(Editor)));
    }

    [Fact]
    public async Task Queuing_work_that_sends_drawings_out_needs_an_approval_even_when_the_type_is_bound()
    {
        var (job, token) = await NewJobAsync();
        await using var connection = await db.OpenAsync();
        var snapshot = await ProducerStore.EnsureProviderSnapshotAsync(connection, new ProviderSnapshotInfo(2, "v.example.invalid", "m-" + Guid.NewGuid().ToString("N")[..6], null, 4000, true, "Microsoft Foundry"));
        var producer = await ProducerStore.EnsureProducerAsync(connection, ProducerRef.Llm(3000 + snapshot, snapshot));
        var type = UniqueType();
        await Bindings().BindAsync(type, producer, await EvalRunAsync(producer), true, 2, Admin);

        await Assert.ThrowsAsync<JobRuleViolationException>(() => Tasks().EnqueueByTypeAsync(job.JobId, type, null, null, token, Ctx(Editor)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Tasks().EnqueueByTypeAsync(job.JobId, type, null, 42, token, Ctx(Editor)));   // 42 is not an approval
    }

    [Fact]
    public async Task Queuing_by_type_still_needs_the_lock_and_permission()
    {
        var (job, token) = await NewJobAsync();
        var (producer, run) = await EvaluatedAlgorithmAsync();
        var type = UniqueType();
        await Bindings().BindAsync(type, producer, run, false, 3, Admin);

        await Assert.ThrowsAsync<JobAccessDeniedException>(() => Tasks().EnqueueByTypeAsync(job.JobId, type, null, null, token, Ctx(Viewer)));
        await Locks().ForceTakeoverAsync(job.JobId, Ctx(Owner));
        await Assert.ThrowsAsync<StaleEditTokenException>(() => Tasks().EnqueueByTypeAsync(job.JobId, type, null, null, token, Ctx(Editor)));
    }

    // ---- fencing token carried by the browser

    [Fact]
    public async Task A_browser_that_sends_the_token_it_was_given_gets_the_token_the_data_layer_needs()
    {
        var (created, token) = await NewJobAsync();
        var job = (await Jobs().GetAsync(created.JobId))!;

        var resolved = await Locks().ResolveTokenAsync(job.JobId, token.FencingToken, Ctx(Editor));
        var updated = await Jobs().UpdateDetailsAsync(job.JobId, new JobDetails { ProjectName = "Phase 2" }, job.StateVersion, resolved, Ctx(Editor));

        Assert.Equal("Phase 2", updated.ProjectName);
    }

    [Fact]
    public async Task An_older_token_is_refused_even_if_the_same_session_holds_the_lock_again_and_the_refusal_is_audited()
    {
        var (job, oldToken) = await NewJobAsync();
        await Locks().ReleaseAsync(oldToken, Ctx(Editor));
        var newToken = (await Locks().AcquireAsync(job.JobId, Ctx(Editor))).Token!;
        Assert.NotEqual(oldToken.FencingToken, newToken.FencingToken);

        await Assert.ThrowsAsync<StaleEditTokenException>(() => Locks().ResolveTokenAsync(job.JobId, oldToken.FencingToken, Ctx(Editor)));
        Assert.Contains("write.rejected", (await Jobs().ListAuditAsync(job.JobId)).Select(e => e.Action));
        await Locks().ResolveTokenAsync(job.JobId, newToken.FencingToken, Ctx(Editor));
    }

    [Fact]
    public async Task A_token_is_refused_for_someone_else_with_no_lock_or_without_permission()
    {
        var (job, token) = await NewJobAsync();

        await Assert.ThrowsAsync<StaleEditTokenException>(() => Locks().ResolveTokenAsync(job.JobId, token.FencingToken, Ctx(Owner)));        // another session
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => Locks().ResolveTokenAsync(job.JobId, token.FencingToken, Ctx(Viewer)));       // permission first
        await Locks().ReleaseAsync(token, Ctx(Editor));
        await Assert.ThrowsAsync<StaleEditTokenException>(() => Locks().ResolveTokenAsync(job.JobId, token.FencingToken, Ctx(Editor)));        // no lock any more
    }

    // ---- time on job

    private ActivityRepository Activity() => new(db.ConnectionString, TimeSpan.FromMilliseconds(100));

    [Fact]
    public async Task The_first_heartbeat_starts_a_session_and_later_ones_credit_server_measured_time()
    {
        var (job, _) = await NewJobAsync();
        var activity = Activity();

        var first = await activity.RecordHeartbeatAsync(job.JobId, Ctx(Editor), ActivityState.Active, WorkflowStep.Review, "viewer", ReviewLevel.Normal);
        Assert.Equal((0.0, 0.0, 1), (first.ActiveSeconds, first.IdleSeconds, first.Heartbeats));
        Assert.Equal(("Review", "viewer", "Normal"), (first.WorkflowStep, first.Surface, first.ReviewLevel));

        await Task.Delay(150);
        var active = await activity.RecordHeartbeatAsync(job.JobId, Ctx(Editor), ActivityState.Active, null, null, null);
        await Task.Delay(150);
        var idle = await activity.RecordHeartbeatAsync(job.JobId, Ctx(Editor), ActivityState.Idle, null, null, null);

        Assert.InRange(active.ActiveSeconds, 0.1, 0.2);
        Assert.Equal(active.ActiveSeconds, idle.ActiveSeconds, 6);          // the idle interval did not become active time
        Assert.InRange(idle.IdleSeconds, 0.1, 0.2);
        Assert.Equal(3, idle.Heartbeats);
        Assert.Equal("Review", idle.WorkflowStep);                           // earlier labels are kept when a heartbeat omits them
    }

    [Fact]
    public async Task A_long_gap_credits_only_two_intervals_and_the_rest_is_recorded_as_uncredited()
    {
        var (job, _) = await NewJobAsync();
        var activity = Activity();
        await activity.RecordHeartbeatAsync(job.JobId, Ctx(Editor), ActivityState.Active, null, null, null);

        await Task.Delay(600);   // a closed laptop
        var after = await activity.RecordHeartbeatAsync(job.JobId, Ctx(Editor), ActivityState.Active, null, null, null);

        Assert.InRange(after.ActiveSeconds, 0.19, 0.21);                    // capped at two 100 ms intervals
        Assert.True(after.UncreditedSeconds > 0.3);
    }

    [Fact]
    public async Task Anyone_who_can_view_the_job_is_measured_and_a_stranger_is_not()
    {
        var (job, _) = await NewJobAsync();
        var activity = Activity();

        await activity.RecordHeartbeatAsync(job.JobId, Ctx(Viewer), ActivityState.Active, null, null, null);
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => activity.RecordHeartbeatAsync(job.JobId, Ctx("stranger@example.com"), ActivityState.Active, null, null, null));
        await Assert.ThrowsAsync<JobNotFoundException>(() => activity.RecordHeartbeatAsync(long.MaxValue, Ctx(Viewer), ActivityState.Active, null, null, null));
        await Assert.ThrowsAsync<ArgumentException>(() => activity.RecordHeartbeatAsync(job.JobId, AuditContext.ForUser(Viewer), ActivityState.Active, null, null, null));   // no session
        await Assert.ThrowsAsync<ArgumentException>(() => activity.RecordHeartbeatAsync(job.JobId, AuditContext.ForWorker("w"), ActivityState.Active, null, null, null));
    }

    [Fact]
    public async Task An_ended_session_accrues_nothing_more()
    {
        var (job, _) = await NewJobAsync();
        var activity = Activity();
        await activity.RecordHeartbeatAsync(job.JobId, Ctx(Editor), ActivityState.Active, null, null, null);
        await Task.Delay(120);
        var before = await activity.RecordHeartbeatAsync(job.JobId, Ctx(Editor), ActivityState.Active, null, null, null);

        Assert.True(await activity.EndSessionAsync(job.JobId, Ctx(Editor)));
        Assert.False(await activity.EndSessionAsync(job.JobId, Ctx(Editor)));
        Assert.False(await activity.EndSessionAsync(job.JobId, Ctx(Viewer)));      // nobody else's session
        await Task.Delay(120);
        var after = await activity.RecordHeartbeatAsync(job.JobId, Ctx(Editor), ActivityState.Active, null, null, null);

        Assert.Equal(before.ActiveSeconds, after.ActiveSeconds, 6);
        Assert.NotNull(after.EndedUtc);
    }

    [Fact]
    public async Task The_summary_groups_time_by_person_and_step_and_one_person_can_have_several_sessions()
    {
        var (job, _) = await NewJobAsync();
        var activity = Activity();
        foreach (var (who, session, step) in new[] { (Editor, "editor-tab-1", WorkflowStep.Setup), (Editor, "editor-tab-2", WorkflowStep.Review), (Viewer, "viewer-tab-1", WorkflowStep.Review) })
        {
            await activity.RecordHeartbeatAsync(job.JobId, Ctx(who, session: session), ActivityState.Active, step, null, null);
            await Task.Delay(110);
            await activity.RecordHeartbeatAsync(job.JobId, Ctx(who, session: session), ActivityState.Active, null, null, null);
        }

        var summary = await activity.GetSummaryAsync(job.JobId);

        Assert.Equal(3, summary.Sessions);
        Assert.Equal([Editor, Viewer], summary.ByUser.Select(b => b.Key));
        Assert.Equal(2, summary.ByUser.Single(b => b.Key == Editor).Sessions);
        Assert.Equal(["Review", "Setup"], summary.ByStep.Select(b => b.Key));
        Assert.Equal(summary.ActiveSeconds, summary.ByUser.Sum(b => b.ActiveSeconds), 6);
    }

    [Fact]
    public async Task A_session_id_cannot_be_shared_between_two_people()
    {
        var (job, _) = await NewJobAsync();
        var activity = Activity();
        await activity.RecordHeartbeatAsync(job.JobId, Ctx(Editor, session: "shared"), ActivityState.Active, null, null, null);

        await Assert.ThrowsAsync<JobRuleViolationException>(() =>
            activity.RecordHeartbeatAsync(job.JobId, Ctx(Viewer, session: "shared"), ActivityState.Active, null, null, null));
        Assert.Equal(1, (await activity.ListSessionsAsync(job.JobId)).Single().Heartbeats);
    }

    [Fact]
    public async Task Concurrent_heartbeats_from_one_session_never_lose_a_count()
    {
        var (job, _) = await NewJobAsync();
        var activity = Activity();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => activity.RecordHeartbeatAsync(job.JobId, Ctx(Editor), ActivityState.Active, null, null, null)));

        var session = Assert.Single(await activity.ListSessionsAsync(job.JobId));
        Assert.Equal(12, session.Heartbeats);
    }

    [Fact]
    public void The_persisted_and_in_memory_crediting_rules_are_the_same_function()
    {
        var gap = TimeSpan.FromSeconds(100);
        var cap = TimeSpan.FromSeconds(30);

        Assert.Equal((30.0, 0.0, 70.0), ActivityAccumulator.Credit(gap, ActivityState.Active, cap));
        Assert.Equal((0.0, 30.0, 70.0), ActivityAccumulator.Credit(gap, ActivityState.Idle, cap));
        Assert.Equal((10.0, 0.0, 0.0), ActivityAccumulator.Credit(TimeSpan.FromSeconds(10), ActivityState.Active, cap));
        Assert.Equal((0.0, 0.0, 0.0), ActivityAccumulator.Credit(TimeSpan.FromSeconds(-5), ActivityState.Active, cap));
    }
}
