using System.Text;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Data;
using AIToolbox.Ductwork.Core.Drawings;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Llm;
using AIToolbox.Ductwork.Core.Tasks;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Tests;

public sealed class VisionTests(SqlFixture db) : IClassFixture<SqlFixture>, IDisposable
{
    private const string Owner = "owner@example.com";
    private const string Editor = "editor@example.com";
    private const string Viewer = "viewer@example.com";
    private const int ProviderId = 7;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ductwork-vision-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private Func<CancellationToken, Task<SqlConnection>> Open => async ct =>
    {
        var connection = new SqlConnection(db.ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    };

    private VisionRepository Vision => new(Open);
    private ProcessingTaskRepository Tasks => new(Open);

    private static AuditContext Ctx(string email, bool admin = false, string? session = null) =>
        AuditContext.ForUser(email, displayName: email.Split('@')[0], sessionId: session ?? "s-" + email, userAgent: "tests", isAdmin: admin);

    private static AuditContext Admin => Ctx("admin@example.com", admin: true);

    private static ProviderSnapshotInfo Snapshot(string model) => new(2, "foundry.example.invalid", model, "2024-10-21", 4000, true, "Microsoft Foundry");

    private static ProviderDisclosure Provider(string model) => new(ProviderId, "Foundry Vision", model, "foundry.example.invalid", "Microsoft Foundry", Snapshot(model));

    private async Task<VisionPolicyRecord> TurnOnAsync(Action<VisionPolicyUpdateBuilder>? configure = null)
    {
        var b = new VisionPolicyUpdateBuilder();
        configure?.Invoke(b);
        return await Vision.SetPolicyAsync(new VisionPolicyUpdate
        {
            Enabled = b.Enabled, AllowedProviderIds = b.Providers, ApproverRule = b.Approver, MaxPagesPerApproval = b.MaxPages, MaxImagesPerPage = b.ImagesPerPage, RequireReason = b.RequireReason,
            ApprovalLifetimeMinutes = b.Lifetime, StandingTaskTypes = b.StandingTypes, StandingMaxPagesPerJob = b.StandingPages, NoticeText = b.Notice
        }, Admin);
    }

    private sealed class VisionPolicyUpdateBuilder
    {
        public bool Enabled { get; set; } = true;
        public int[] Providers { get; set; } = [ProviderId];
        public VisionApproverRule Approver { get; set; } = VisionApproverRule.EditPermission;
        public int MaxPages { get; set; } = 10;
        public int ImagesPerPage { get; set; } = 3;
        public bool RequireReason { get; set; } = true;
        public int Lifetime { get; set; } = 60;
        public string[] StandingTypes { get; set; } = [];
        public int StandingPages { get; set; }
        public string? Notice { get; set; }
    }

    private sealed record Setup(JobRecord Job, EditToken Token, IReadOnlyList<SheetRecord> Sheets, string TaskType, long ProducerId, string Model);

    /// <summary>A job with three sheets and a task type bound to a vision-capable model.</summary>
    private async Task<Setup> JobAsync(int sheets = 3, string? bindType = null, string? model = null)
    {
        var jobs = new JobRepository(db.ConnectionString);
        var job = await jobs.CreateAsync(new NewJob { Name = "Vision " + Guid.NewGuid().ToString("N")[..8] }, Ctx(Owner));
        await jobs.AddMemberAsync(job.JobId, Editor, JobRole.Editor, Ctx(Owner));
        await jobs.AddMemberAsync(job.JobId, Viewer, JobRole.Viewer, Ctx(Owner));
        var token = (await new EditLockService(db.ConnectionString).AcquireAsync(job.JobId, Ctx(Editor))).Token!;

        var data = new byte[300];
        Array.Fill(data, (byte)Random.Shared.Next(1, 250));
        Encoding.ASCII.GetBytes("%PDF-1.7\n").CopyTo(data, 0);
        var uploads = new UploadService(Open, new FileSystemArtifactStore(_root), new UploadSettings { ChunkBytes = 1000 });
        var session = await uploads.BeginAsync(job.JobId, new BeginUpload("v.pdf", data.Length, null), token, Ctx(Editor));
        await uploads.AppendAsync(session.PublicId, 0, new MemoryStream(data), null, Ctx(Editor));
        var file = await uploads.CompleteAsync(session.PublicId, token, Ctx(Editor));
        var registered = await new SheetRepository(Open).RegisterAsync(job.JobId, file.PublicId,
            Enumerable.Range(1, sheets).Select(i => new NewSheet { PageNo = i, WidthPts = 2592, HeightPts = 1728, SheetNumber = $"M1.{i}" }).ToList(), token, Ctx(Editor));

        model ??= "vision-" + Guid.NewGuid().ToString("N")[..6];
        await using var connection = await Open(default);
        var snapshot = await ProducerStore.EnsureProviderSnapshotAsync(connection, Snapshot(model));
        var producer = await ProducerStore.EnsureProducerAsync(connection, ProducerRef.Llm(5000 + snapshot, snapshot));
        var type = bindType ?? "classify-" + Guid.NewGuid().ToString("N")[..8];
        await new TaskBindingRepository(db.ConnectionString).BindAsync(type, producer, await EvalRunAsync(producer), true, 2, Admin);
        return new Setup(job, token, registered, type, producer, model);
    }

    private async Task<int> EvalRunAsync(long producerId)
    {
        var goldSet = await db.ScalarAsync<int>("INSERT INTO ductwork.GoldSet (Name, Role) OUTPUT INSERTED.GoldSetId VALUES (@n, 'Development');", ("@n", "gs-" + Guid.NewGuid().ToString("N")));
        return await db.ScalarAsync<int>(
            "INSERT INTO ductwork.EvalRun (GoldSetId, ProducerId, TaskKey, MatchMode, ContractVersion) VALUES (@g, @p, 'bind', 'Count', '0.1.0'); SELECT CAST(SCOPE_IDENTITY() AS INT);",
            ("@g", goldSet), ("@p", producerId));
    }

    private Task<VisionApprovalRecord> ApproveAsync(Setup s, int count = 2, string? reason = "Local methods could not read the text-less sheets.", AuditContext? by = null, int? notice = null)
    {
        var policy = Vision.GetPolicyAsync().Result;
        return Vision.CreateApprovalAsync(s.Job.JobId, new ApprovalRequest
        {
            SheetIds = s.Sheets.Take(count).Select(x => x.PublicId).ToList(), Provider = Provider(s.Model), Reason = reason, AcknowledgedNoticeVersion = notice ?? policy.NoticeVersion
        }, by ?? Ctx(Editor));
    }

    private Task<int> EventsAsync(long jobId, string action) =>
        db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.JobAuditEvent WHERE JobId = @j AND Action = @a;", ("@j", jobId), ("@a", action));

    private async Task<(ProcessingTaskRecord Task, AuditContext Worker)> RunningAsync(Setup s, long? approval, string worker = "w1")
    {
        var queued = await Tasks.EnqueueByTypeAsync(s.Job.JobId, s.TaskType, null, approval, s.Token, Ctx(Editor));
        var ctx = AuditContext.ForWorker(worker);
        var claimed = await Tasks.ClaimNextAsync(ctx, [s.TaskType]);
        Assert.Equal(queued.ProcessingTaskId, claimed!.ProcessingTaskId);
        return (claimed, ctx);
    }

    // ---- the policy

    [Fact]
    public async Task With_nothing_saved_hosted_vision_is_off_and_only_an_administrator_can_change_it()
    {
        var defaults = new VisionPolicyRecord();                                   // what applies when nothing has been saved
        Assert.False(defaults.Enabled);
        Assert.Empty(defaults.AllowedProviderIds);
        Assert.Contains("customer information", defaults.NoticeText);

        await Assert.ThrowsAsync<JobAccessDeniedException>(() => Vision.SetPolicyAsync(new VisionPolicyUpdate { Enabled = true, AllowedProviderIds = [1] }, Ctx(Editor)));
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => Vision.SetPolicyAsync(new VisionPolicyUpdate { Enabled = true }, AuditContext.ForWorker("w1")));
    }

    [Fact]
    public async Task A_policy_change_is_a_new_version_the_notice_is_versioned_and_each_change_is_audited()
    {
        var before = await Vision.GetPolicyAsync();
        var on = await TurnOnAsync();
        var same = await TurnOnAsync(b => b.MaxPages = 5);
        var reworded = await TurnOnAsync(b => b.Notice = "New wording that the approver must read again.");

        Assert.True(on.Enabled);
        Assert.Equal(before.NoticeVersion, same.NoticeVersion);                       // unchanged wording keeps its version
        Assert.Equal(same.NoticeVersion + 1, reworded.NoticeVersion);
        Assert.Equal(5, same.MaxPages());

        Assert.True(await db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.SettingsAuditEvent WHERE Category = 'vision-policy';") >= 3);
        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync("UPDATE ductwork.VisionPolicy SET Enabled = 0;"));
        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync("DELETE FROM ductwork.VisionPolicy;"));
        await TurnOnAsync();                                                           // restore the usual notice for the other tests' expectations
    }

    [Fact]
    public async Task Bad_policy_values_are_refused()
    {
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Vision.SetPolicyAsync(new VisionPolicyUpdate { Enabled = true, MaxPagesPerApproval = 0 }, Admin));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Vision.SetPolicyAsync(new VisionPolicyUpdate { Enabled = true, StandingTaskTypes = ["x"], StandingMaxPagesPerJob = 0 }, Admin));
    }

    // ---- the disclosure and the approval

    [Fact]
    public async Task The_disclosure_says_what_will_be_sent_and_where_and_lists_why_it_cannot_be_approved()
    {
        await TurnOnAsync();
        var s = await JobAsync(3);

        var ok = await Vision.PreviewApprovalAsync(s.Job.JobId, s.Sheets.Take(2).Select(x => x.PublicId).ToList(), Provider(s.Model), Ctx(Editor));
        Assert.Equal((2, 6, 60, "Microsoft Foundry", 0), (ok.PageCount, ok.ImageBudget, ok.LifetimeMinutes, ok.Provider.GovernanceRoute, ok.Problems.Count));
        Assert.Equal(["M1.1", "M1.2"], ok.Sheets.Select(x => x.SheetNumber));
        Assert.False(string.IsNullOrWhiteSpace(ok.Policy.NoticeText));

        var other = Provider(s.Model) with { ProviderId = 99 };
        var bad = await Vision.PreviewApprovalAsync(s.Job.JobId, [Guid.NewGuid()], other, Ctx(Editor));
        Assert.Contains(bad.Problems, p => p.Contains("not been allowed"));
        Assert.Contains(bad.Problems, p => p.Contains("not on this job"));

        var viewer = await Vision.PreviewApprovalAsync(s.Job.JobId, s.Sheets.Take(1).Select(x => x.PublicId).ToList(), Provider(s.Model), Ctx(Viewer));
        Assert.Contains(viewer.Problems, p => p.Contains("edit permission"));
    }

    [Fact]
    public async Task A_person_approves_listed_sheets_for_one_model_and_the_approval_expires_and_is_audited()
    {
        await TurnOnAsync();
        var s = await JobAsync();

        var approval = await ApproveAsync(s, 2);

        Assert.Equal((ApprovalKind.PerRun, ApprovalStatus.Active, 2, 6, 0, Editor), (approval.Kind, approval.Status, approval.PageCount, approval.ImageBudget, approval.ImagesSent, approval.ApprovedBy));
        Assert.Equal(s.Sheets.Take(2).Select(x => x.PublicId), approval.SheetIds);
        Assert.True(approval.ExpiresUtc > DateTime.UtcNow.AddMinutes(55) && approval.ExpiresUtc < DateTime.UtcNow.AddMinutes(65));
        Assert.Equal(1, await EventsAsync(s.Job.JobId, "vision.approved"));
        Assert.True((await new JobRepository(db.ConnectionString).VerifyAuditChainAsync(s.Job.JobId)).IsValid);
    }

    [Fact]
    public async Task An_approval_needs_the_policy_on_an_allowed_model_a_reason_and_the_current_notice_and_real_in_scope_sheets()
    {
        var s = await JobAsync();
        await TurnOnAsync(b => b.Enabled = false);
        var off = await Assert.ThrowsAsync<JobRuleViolationException>(() => ApproveAsync(s));
        Assert.Contains("switched off", off.Message);

        var policy = await TurnOnAsync(b => b.MaxPages = 2);
        await Assert.ThrowsAsync<JobRuleViolationException>(() => ApproveAsync(s, reason: " "));
        var stale = await Assert.ThrowsAsync<JobRuleViolationException>(() => ApproveAsync(s, notice: policy.NoticeVersion - 1));
        Assert.Contains("notice has changed", stale.Message);
        var many = await Assert.ThrowsAsync<JobRuleViolationException>(() => ApproveAsync(s, count: 3));
        Assert.Contains("At most 2", many.Message);

        await new SheetRepository(Open).UpdateSheetAsync(s.Sheets[0].SheetId, new SheetEdit { InScope = false }, s.Token, Ctx(Editor));
        var outOfScope = await Assert.ThrowsAsync<JobRuleViolationException>(() => ApproveAsync(s, 1));
        Assert.Contains("out of scope", outOfScope.Message);

        var wrongModel = await Assert.ThrowsAsync<JobRuleViolationException>(() => Vision.CreateApprovalAsync(s.Job.JobId, new ApprovalRequest
        {
            SheetIds = [s.Sheets[1].PublicId], Provider = Provider(s.Model) with { ProviderId = 123 }, Reason = "x", AcknowledgedNoticeVersion = policy.NoticeVersion
        }, Ctx(Editor)));
        Assert.Contains("not been allowed", wrongModel.Message);
        await TurnOnAsync();
    }

    [Fact]
    public async Task Who_may_approve_follows_the_administrators_rule()
    {
        var s = await JobAsync();

        await TurnOnAsync(b => b.Approver = VisionApproverRule.JobManager);
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => ApproveAsync(s, by: Ctx(Editor)));
        await db.ExecAsync("INSERT INTO ductwork.ToolPermission (UserEmail, Permission, GrantedBy) VALUES (@e, 'JobManager', 'test');", ("@e", Editor));
        try { Assert.Equal(ApprovalStatus.Active, (await ApproveAsync(s, by: Ctx(Editor))).Status); }
        finally { await db.ExecAsync("DELETE FROM ductwork.ToolPermission WHERE UserEmail = @e;", ("@e", Editor)); }

        await TurnOnAsync(b => b.Approver = VisionApproverRule.Admin);
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => ApproveAsync(s, by: Ctx(Editor)));
        Assert.Equal(ApprovalStatus.Active, (await ApproveAsync(s, by: Ctx(Owner, admin: true))).Status);

        await TurnOnAsync();
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => ApproveAsync(s, by: Ctx(Viewer)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => ApproveAsync(s, by: AuditContext.ForWorker("w1")));
    }

    [Fact]
    public async Task A_person_can_revoke_an_approval_with_a_reason_and_a_revoked_one_cannot_be_used()
    {
        await TurnOnAsync();
        var s = await JobAsync();
        var approval = await ApproveAsync(s);

        await Assert.ThrowsAsync<JobRuleViolationException>(() => Vision.RevokeApprovalAsync(s.Job.JobId, approval.VisionApprovalId, " ", Ctx(Editor)));
        var revoked = await Vision.RevokeApprovalAsync(s.Job.JobId, approval.VisionApprovalId, "Wrong sheets", Ctx(Editor));
        Assert.Equal((ApprovalStatus.Revoked, Editor), (revoked.Status, revoked.RevokedBy));
        Assert.Equal(1, await EventsAsync(s.Job.JobId, "vision.revoked"));

        var refused = await Assert.ThrowsAsync<JobRuleViolationException>(() => Tasks.EnqueueByTypeAsync(s.Job.JobId, s.TaskType, null, approval.VisionApprovalId, s.Token, Ctx(Editor)));
        Assert.Contains("expired or been revoked", refused.Message);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Vision.RevokeApprovalAsync(long.MaxValue / 2, approval.VisionApprovalId, "x", Ctx(Editor)).ContinueWith(t => t.IsFaulted ? throw new KeyNotFoundException() : t.Result));
    }

    // ---- queueing a task that sends drawings

    [Fact]
    public async Task A_task_that_sends_drawings_binds_one_live_approval_for_its_own_model_and_no_other_run_can_reuse_it()
    {
        await TurnOnAsync();
        var s = await JobAsync();
        var approval = await ApproveAsync(s);

        var task = await Tasks.EnqueueByTypeAsync(s.Job.JobId, s.TaskType, null, approval.VisionApprovalId, s.Token, Ctx(Editor));
        Assert.Equal((true, approval.VisionApprovalId), (task.SendsDrawingContentToHostedModel, task.VisionApprovalId));
        var used = (await Vision.GetApprovalAsync(s.Job.JobId, approval.PublicId))!;
        Assert.Equal((ApprovalStatus.Used, task.ProcessingTaskId), (used.Status, used.UsedByTaskId));

        var again = await Assert.ThrowsAsync<JobRuleViolationException>(() => Tasks.EnqueueByTypeAsync(s.Job.JobId, s.TaskType, null, approval.VisionApprovalId, s.Token, Ctx(Editor)));
        Assert.Contains("already used", again.Message);
    }

    [Fact]
    public async Task An_approval_for_a_different_model_or_job_or_an_expired_or_missing_one_is_refused()
    {
        await TurnOnAsync();
        var s = await JobAsync(model: "model-a");
        var other = await JobAsync(model: "model-b");
        var forOtherModel = await ApproveAsync(other);            // approved for model-b

        var wrongModel = await Assert.ThrowsAsync<JobRuleViolationException>(() => Tasks.EnqueueByTypeAsync(other.Job.JobId, s.TaskType, null, forOtherModel.VisionApprovalId, other.Token, Ctx(Editor)));
        Assert.Contains("different model", wrongModel.Message);

        var otherJob = await ApproveAsync(s);
        var wrongJob = await Assert.ThrowsAsync<JobRuleViolationException>(() => Tasks.EnqueueByTypeAsync(other.Job.JobId, other.TaskType, null, otherJob.VisionApprovalId, other.Token, Ctx(Editor)));
        Assert.Contains("not on this job", wrongJob.Message);

        var soon = await ApproveAsync(s);
        await db.ExecAsync("UPDATE ductwork.VisionApproval SET ApprovedUtc = DATEADD(HOUR, -3, SYSUTCDATETIME()), ExpiresUtc = DATEADD(HOUR, -2, SYSUTCDATETIME()) WHERE VisionApprovalId = @a;", ("@a", soon.VisionApprovalId));
        var expired = await Assert.ThrowsAsync<JobRuleViolationException>(() => Tasks.EnqueueByTypeAsync(s.Job.JobId, s.TaskType, null, soon.VisionApprovalId, s.Token, Ctx(Editor)));
        Assert.Contains("expired", expired.Message);

        await Assert.ThrowsAsync<JobRuleViolationException>(() => Tasks.EnqueueByTypeAsync(s.Job.JobId, s.TaskType, null, null, s.Token, Ctx(Editor)));
        Assert.True(await Vision.ExpireStaleAsync() >= 1);
        Assert.Equal(ApprovalStatus.Expired, (await Vision.GetApprovalAsync(s.Job.JobId, soon.PublicId))!.Status);
    }

    [Fact]
    public async Task With_the_policy_off_nothing_that_sends_drawings_can_be_queued_at_all()
    {
        await TurnOnAsync();
        var s = await JobAsync();
        var approval = await ApproveAsync(s);
        await TurnOnAsync(b => b.Enabled = false);

        var refused = await Assert.ThrowsAsync<JobRuleViolationException>(() => Tasks.EnqueueByTypeAsync(s.Job.JobId, s.TaskType, null, approval.VisionApprovalId, s.Token, Ctx(Editor)));

        Assert.Contains("switched off", refused.Message);
        await TurnOnAsync();
    }

    [Fact]
    public async Task A_task_type_the_administrator_approved_in_advance_gets_a_standing_approval_within_its_page_limit()
    {
        var type = "standing-" + Guid.NewGuid().ToString("N")[..8];
        await TurnOnAsync(b => { b.StandingTypes = [type]; b.StandingPages = 4; });
        var s = await JobAsync(3, bindType: type);

        var first = await Tasks.EnqueueByTypeAsync(s.Job.JobId, type, null, null, s.Token, Ctx(Editor));
        var approval = (await Vision.ListApprovalsAsync(s.Job.JobId)).Single();
        Assert.Equal((ApprovalKind.Standing, ApprovalStatus.Used, 3, "policy:standing", first.ProcessingTaskId), (approval.Kind, approval.Status, approval.PageCount, approval.ApprovedBy, approval.UsedByTaskId!.Value));
        Assert.Equal(1, await EventsAsync(s.Job.JobId, "vision.standing-issued"));

        // 3 of the job's 4 standing pages are spent, and a second run would need 3 more.
        var second = await Assert.ThrowsAsync<JobRuleViolationException>(() => Tasks.EnqueueByTypeAsync(s.Job.JobId, type, null, null, s.Token, Ctx(Editor)));
        Assert.Contains("1 remain", second.Message);
        await TurnOnAsync();
    }

    // ---- the gateway's checks

    [Fact]
    public async Task A_send_is_authorized_only_for_the_approved_sheets_and_reserves_its_images_before_anything_leaves()
    {
        await TurnOnAsync();
        var s = await JobAsync();
        var approval = await ApproveAsync(s, 2);
        var (task, worker) = await RunningAsync(s, approval.VisionApprovalId);
        var sheets = s.Sheets.Take(2).Select(x => x.PublicId).ToList();

        var grant = await Vision.AuthorizeSendAsync(task.ProcessingTaskId, worker, sheets, 4, 1000, "classify the crop", default);

        Assert.Equal((approval.VisionApprovalId, true), (grant.ApprovalId, grant.AllowedProviderIds.Contains(ProviderId)));
        Assert.Equal(4, (await Vision.GetApprovalAsync(s.Job.JobId, approval.PublicId))!.ImagesSent);
        var logged = (await Vision.ListTransmissionsAsync(s.Job.JobId)).Single();
        Assert.Equal((4, 1000L, "Reserved"), (logged.Images, logged.Bytes, logged.Outcome));
        Assert.Equal(1, await EventsAsync(s.Job.JobId, "vision.sending"));
        Assert.DoesNotContain("classify the crop", await db.ScalarAsync<string>("SELECT TOP 1 AfterJson FROM ductwork.JobAuditEvent WHERE JobId = @j AND Action = 'vision.sending';", ("@j", s.Job.JobId)) ?? "");   // the prompt is recorded as a hash only
    }

    [Fact]
    public async Task The_budget_cannot_be_overspent_and_a_sheet_outside_the_approval_is_refused()
    {
        await TurnOnAsync();
        var s = await JobAsync();
        var approval = await ApproveAsync(s, 2);                                 // 2 pages x 3 images = 6
        var (task, worker) = await RunningAsync(s, approval.VisionApprovalId);
        var covered = s.Sheets.Take(2).Select(x => x.PublicId).ToList();

        await Vision.AuthorizeSendAsync(task.ProcessingTaskId, worker, covered, 5, 0, "d", default);
        var over = await Assert.ThrowsAsync<VisionRefusedException>(() => Vision.AuthorizeSendAsync(task.ProcessingTaskId, worker, covered, 2, 0, "d", default));
        Assert.Equal("budget", over.Code);
        var stray = await Assert.ThrowsAsync<VisionRefusedException>(() => Vision.AuthorizeSendAsync(task.ProcessingTaskId, worker, [s.Sheets[2].PublicId], 1, 0, "d", default));
        Assert.Equal("sheet-not-approved", stray.Code);
        Assert.Equal(5, (await Vision.GetApprovalAsync(s.Job.JobId, approval.PublicId))!.ImagesSent);
        Assert.Equal("no-images", (await Assert.ThrowsAsync<VisionRefusedException>(() => Vision.AuthorizeSendAsync(task.ProcessingTaskId, worker, covered, 0, 0, "d", default))).Code);
    }

    [Fact]
    public async Task Only_the_worker_holding_the_task_can_send_and_only_under_the_tasks_own_approval()
    {
        await TurnOnAsync();
        var s = await JobAsync();
        var (task, worker) = await RunningAsync(s, (await ApproveAsync(s)).VisionApprovalId);
        var sheets = s.Sheets.Take(1).Select(x => x.PublicId).ToList();

        Assert.Equal("not-running", (await Assert.ThrowsAsync<VisionRefusedException>(() => Vision.AuthorizeSendAsync(task.ProcessingTaskId, AuditContext.ForWorker("w2"), sheets, 1, 0, "d", default))).Code);
        Assert.Equal("not-running", (await Assert.ThrowsAsync<VisionRefusedException>(() => Vision.AuthorizeSendAsync(long.MaxValue / 2, worker, sheets, 1, 0, "d", default))).Code);
        await Assert.ThrowsAsync<ArgumentException>(() => Vision.AuthorizeSendAsync(task.ProcessingTaskId, Ctx(Editor), sheets, 1, 0, "d", default));
    }

    [Fact]
    public async Task A_task_that_does_not_send_drawings_has_no_standing_to_send_any()
    {
        await TurnOnAsync();
        var s = await JobAsync();
        await using var connection = await Open(default);
        var algorithm = await ProducerStore.EnsureProducerAsync(connection, ProducerRef.Algorithm("plain-" + Guid.NewGuid().ToString("N")[..6], "1.0", "h"));
        var type = "plain-" + Guid.NewGuid().ToString("N")[..8];
        await new TaskBindingRepository(db.ConnectionString).BindAsync(type, algorithm, await EvalRunAsync(algorithm), false, 2, Admin);
        await Tasks.EnqueueByTypeAsync(s.Job.JobId, type, null, null, s.Token, Ctx(Editor));
        var worker = AuditContext.ForWorker("w1");
        var claimed = (await Tasks.ClaimNextAsync(worker, [type]))!;

        var refused = await Assert.ThrowsAsync<VisionRefusedException>(() => Vision.AuthorizeSendAsync(claimed.ProcessingTaskId, worker, s.Sheets.Take(1).Select(x => x.PublicId).ToList(), 1, 0, "d", default));

        Assert.Equal("not-approved", refused.Code);
    }

    [Fact]
    public async Task A_revoked_or_expired_approval_or_a_policy_turned_off_stops_the_next_send()
    {
        await TurnOnAsync();
        var s = await JobAsync();
        var approval = await ApproveAsync(s);
        var (task, worker) = await RunningAsync(s, approval.VisionApprovalId);
        var sheets = s.Sheets.Take(1).Select(x => x.PublicId).ToList();
        await Vision.AuthorizeSendAsync(task.ProcessingTaskId, worker, sheets, 1, 0, "d", default);

        await db.ExecAsync("UPDATE ductwork.VisionApproval SET ApprovedUtc = DATEADD(HOUR, -3, SYSUTCDATETIME()), ExpiresUtc = DATEADD(HOUR, -2, SYSUTCDATETIME()) WHERE VisionApprovalId = @a;", ("@a", approval.VisionApprovalId));
        Assert.Equal("expired", (await Assert.ThrowsAsync<VisionRefusedException>(() => Vision.AuthorizeSendAsync(task.ProcessingTaskId, worker, sheets, 1, 0, "d", default))).Code);

        await db.ExecAsync("UPDATE ductwork.VisionApproval SET ExpiresUtc = DATEADD(HOUR, 2, SYSUTCDATETIME()), ApprovedUtc = DATEADD(HOUR, -1, SYSUTCDATETIME()) WHERE VisionApprovalId = @a;", ("@a", approval.VisionApprovalId));
        await Vision.RevokeApprovalAsync(s.Job.JobId, approval.VisionApprovalId, "Changed my mind", Ctx(Editor));
        Assert.Equal("revoked", (await Assert.ThrowsAsync<VisionRefusedException>(() => Vision.AuthorizeSendAsync(task.ProcessingTaskId, worker, sheets, 1, 0, "d", default))).Code);
    }

    [Fact]
    public async Task A_send_that_never_left_the_server_gives_its_images_back_and_one_that_did_does_not()
    {
        await TurnOnAsync();
        var s = await JobAsync();
        var approval = await ApproveAsync(s);
        var (task, worker) = await RunningAsync(s, approval.VisionApprovalId);
        var sheets = s.Sheets.Take(1).Select(x => x.PublicId).ToList();

        var refused = await Vision.AuthorizeSendAsync(task.ProcessingTaskId, worker, sheets, 2, 10, "d", default);
        await Vision.CompleteSendAsync(refused.TransmissionId, sent: false, leftTheServer: false, "The image was too large.", worker);
        Assert.Equal(0, (await Vision.GetApprovalAsync(s.Job.JobId, approval.PublicId))!.ImagesSent);

        var sent = await Vision.AuthorizeSendAsync(task.ProcessingTaskId, worker, sheets, 2, 10, "d", default);
        await Vision.CompleteSendAsync(sent.TransmissionId, sent: true, leftTheServer: true, null, worker);
        var failedAfter = await Vision.AuthorizeSendAsync(task.ProcessingTaskId, worker, sheets, 1, 10, "d", default);
        await Vision.CompleteSendAsync(failedAfter.TransmissionId, sent: false, leftTheServer: true, "The provider timed out.", worker);

        Assert.Equal(3, (await Vision.GetApprovalAsync(s.Job.JobId, approval.PublicId))!.ImagesSent);   // a timeout may still have disclosed the image
        Assert.Equal(["Failed", "Sent", "Failed"], (await Vision.ListTransmissionsAsync(s.Job.JobId)).Select(x => x.Outcome));
        await Vision.CompleteSendAsync(sent.TransmissionId, sent: false, leftTheServer: false, "again", worker);   // a finished record is not rewritten
        Assert.Equal("Sent", (await Vision.ListTransmissionsAsync(s.Job.JobId))[1].Outcome);
    }

    [Fact]
    public async Task The_record_of_what_was_sent_cannot_be_deleted()
    {
        await TurnOnAsync();
        var s = await JobAsync();
        var (task, worker) = await RunningAsync(s, (await ApproveAsync(s)).VisionApprovalId);
        await Vision.AuthorizeSendAsync(task.ProcessingTaskId, worker, s.Sheets.Take(1).Select(x => x.PublicId).ToList(), 1, 5, "d", default);

        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync("DELETE FROM ductwork.VisionTransmission WHERE JobId = @j;", ("@j", s.Job.JobId)));
    }

    // ---- switching off

    [Fact]
    public async Task Switching_it_off_revokes_unfinished_approvals_cancels_vision_tasks_and_stops_the_gateway_at_once()
    {
        await TurnOnAsync();
        var s = await JobAsync();
        var unused = await ApproveAsync(s, 1);
        var forRunning = await ApproveAsync(s, 1);
        var (running, worker) = await RunningAsync(s, forRunning.VisionApprovalId);
        var queuedApproval = await ApproveAsync(s, 1);
        var queued = await Tasks.EnqueueByTypeAsync(s.Job.JobId, s.TaskType, null, queuedApproval.VisionApprovalId, s.Token, Ctx(Editor));
        var sheets = s.Sheets.Take(1).Select(x => x.PublicId).ToList();

        await TurnOnAsync(b => b.Enabled = false);

        foreach (var approval in new[] { unused, forRunning, queuedApproval })
            Assert.Equal(ApprovalStatus.Revoked, (await Vision.GetApprovalAsync(s.Job.JobId, approval.PublicId))!.Status);
        Assert.Equal(ProcessingTaskStatus.Cancelled, (await Tasks.GetAsync(queued.ProcessingTaskId))!.Status);
        Assert.True((await Tasks.GetAsync(running.ProcessingTaskId))!.CancelRequested);
        Assert.Equal("disabled", (await Assert.ThrowsAsync<VisionRefusedException>(() => Vision.AuthorizeSendAsync(running.ProcessingTaskId, worker, sheets, 1, 0, "d", default))).Code);
        Assert.True(await EventsAsync(s.Job.JobId, "vision.revoked") >= 3);
        await TurnOnAsync();
    }
}

internal static class VisionTestExtensions
{
    public static int MaxPages(this VisionPolicyRecord policy) => policy.MaxPagesPerApproval;
}

/// <summary>A fresh database, so "nothing saved" really is nothing saved.</summary>
public sealed class VisionDefaultsTests(SqlFixture db) : IClassFixture<SqlFixture>
{
    [Fact]
    public async Task A_new_database_has_hosted_vision_off_with_no_provider_allowed()
    {
        var policy = await new VisionRepository(async ct =>
        {
            var connection = new SqlConnection(db.ConnectionString);
            await connection.OpenAsync(ct);
            return connection;
        }).GetPolicyAsync();

        Assert.Equal((false, 0, VisionApproverRule.EditPermission, 0), (policy.Enabled, policy.AllowedProviderIds.Count, policy.ApproverRule, policy.StandingTaskTypes.Count));
    }
}
