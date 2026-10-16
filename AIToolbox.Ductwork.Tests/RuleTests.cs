using System.Text;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Counting;
using AIToolbox.Ductwork.Core.Data;
using AIToolbox.Ductwork.Core.Drawings;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Rules;
using AIToolbox.Ductwork.Core.Tasks;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Tests;

public sealed class RuleTests(SqlFixture db) : IClassFixture<SqlFixture>, IDisposable
{
    private const string Owner = "owner@example.com";
    private const string Editor = "editor@example.com";
    private const string Manager = "manager@example.com";
    private const string Viewer = "viewer@example.com";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ductwork-rules-" + Guid.NewGuid().ToString("N"));

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

    private RuleRepository Rules => new(Open);
    private FindingRepository Findings => new(Open);
    private EntityReviewRepository Review => new(Open);
    private EntityRepository Entities => new(db.ConnectionString);

    private static AuditContext Ctx(string email, string? session = null, bool admin = false) =>
        AuditContext.ForUser(email, displayName: email.Split('@')[0], sessionId: session ?? "s-" + email, userAgent: "tests", isAdmin: admin);

    private sealed record Setup(JobRecord Job, EditToken Token, IReadOnlyList<EntityRecord> Items);

    private static readonly ProducerRef Algorithm = ProducerRef.Algorithm("rule-counter", "1.0", "h");

    private static DetectionProposal Boot(string id, string size, string building = "Type 4", string? tag = null, double quantity = 1) => new()
    {
        DetectionId = id, Type = "Boot", Size = size, Tag = tag, BuildingType = building, Quantity = quantity, Confidence = 0.95,
        Method = DetectionMethod.Text, Family = DetectorFamily.LabelFlagCount, SheetViewRef = "M1.2", Evidence = "label"
    };

    /// <summary>A job with items from a detector: two "4x12" boots in Type 4, one in Type 5, and one "4x10x7" that needs no rule.</summary>
    private async Task<Setup> JobAsync(IReadOnlyList<DetectionProposal>? detections = null, IReadOnlyList<FindingProposal>? findings = null)
    {
        var jobs = new JobRepository(db.ConnectionString);
        var job = await jobs.CreateAsync(new NewJob { Name = "Rules " + Guid.NewGuid().ToString("N")[..8] }, Ctx(Owner));
        await jobs.AddMemberAsync(job.JobId, Editor, JobRole.Editor, Ctx(Owner));
        await jobs.AddMemberAsync(job.JobId, Manager, JobRole.Editor, Ctx(Owner));
        await jobs.AddMemberAsync(job.JobId, Viewer, JobRole.Viewer, Ctx(Owner));
        var token = (await new EditLockService(db.ConnectionString).AcquireAsync(job.JobId, Ctx(Editor))).Token!;
        await Ingest(job, token, detections ?? [Boot("d1", "4x12"), Boot("d2", "4x12"), Boot("d3", "4x12", "Type 5"), Boot("d4", "4x10x7")], findings ?? []);
        return new Setup(job, token, await Entities.ListByJobAsync(job.JobId));
    }

    private async Task Ingest(JobRecord job, EditToken token, IReadOnlyList<DetectionProposal> detections, IReadOnlyList<FindingProposal> findings)
    {
        await using var connection = await Open(default);
        var producer = await ProducerStore.EnsureProducerAsync(connection, Algorithm);
        var type = "detect-" + Guid.NewGuid().ToString("N")[..8];
        var tasks = new ProcessingTaskRepository(db.ConnectionString);
        await tasks.EnqueueAsync(new NewProcessingTask { JobId = job.JobId, TaskType = type, ProducerId = producer }, token, Ctx(Editor));
        var worker = AuditContext.ForWorker("w1");
        var claimed = (await tasks.ClaimNextAsync(worker, [type]))!;
        await new TaskResultIngestor(db.ConnectionString).SubmitAsync(claimed.ProcessingTaskId, worker, new TaskResultManifest
        {
            ContractVersion = ContractInfo.Version, TaskType = type, TaskPublicId = claimed.PublicId.ToString("D"), Producer = Algorithm, Detections = detections, Findings = findings
        });
    }

    private static NewRule Suffix(string name = "Two-dimension boot means depth 7", RuleScope scope = RuleScope.Job) => new()
    {
        Name = name, Description = "Estimator convention", Scope = scope, Match = new RuleMatch { Type = "Boot", SizePattern = @"^\d+x\d+$" }, Action = new RuleAction { SizeSuffix = "x7" }
    };

    private async Task<RuleRecord> ApprovedAsync(Setup s, NewRule? rule = null, AuditContext? by = null)
    {
        var ctx = by ?? Ctx(Editor);
        var created = await Rules.CreateAsync(s.Job.JobId, rule ?? Suffix(), s.Token, ctx);
        return await Rules.ApproveAsync(s.Job.JobId, created.RuleId, s.Token, ctx);
    }

    private Task<int> EventsAsync(long jobId, string action) =>
        db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.JobAuditEvent WHERE JobId = @j AND Action = @a;", ("@j", jobId), ("@a", action));

    private async Task<EntityRecord> ItemAsync(long id) => (await Review.GetAsync(id))!;

    // ---- preview, approve, apply

    [Fact]
    public async Task A_draft_rule_shows_exactly_what_it_would_change_and_changes_nothing()
    {
        var s = await JobAsync();

        var preview = await Rules.GetDraftPreviewAsync(s.Job.JobId, Suffix());

        Assert.Equal(3, preview.Changes.Count);
        Assert.All(preview.Changes, c => Assert.Contains("4x12 -> 4x12x7", c.Change));
        Assert.Empty(preview.Skipped);
        Assert.All(await Entities.ListByJobAsync(s.Job.JobId), e => Assert.Equal(ResolutionSource.Detector, e.ResolutionSource));
        Assert.DoesNotContain(await Rules.ListForJobAsync(s.Job.JobId), r => r.Scope != RuleScope.Instance);
    }

    [Fact]
    public async Task A_rule_changes_nothing_until_a_person_approves_it_and_then_applies_it()
    {
        var s = await JobAsync();
        var created = await Rules.CreateAsync(s.Job.JobId, Suffix(), s.Token, Ctx(Editor));
        Assert.Equal((RuleStatus.Proposed, 1, RuleScope.Job), (created.Status, created.Version, created.Scope));

        var early = await Assert.ThrowsAsync<JobRuleViolationException>(() => Rules.ApplyAsync(s.Job.JobId, created.RuleId, s.Token, Ctx(Editor)));
        Assert.Contains("approved", early.Message);

        var approved = await Rules.ApproveAsync(s.Job.JobId, created.RuleId, s.Token, Ctx(Editor));
        Assert.Equal((RuleStatus.Approved, Editor), (approved.Status, approved.ApprovedBy));
        Assert.All(await Entities.ListByJobAsync(s.Job.JobId), e => Assert.Equal(ResolutionSource.Detector, e.ResolutionSource));   // approving applies nothing

        var result = await Rules.ApplyAsync(s.Job.JobId, approved.RuleId, s.Token, Ctx(Editor));
        Assert.Equal((3, 0, 0), (result.Changed, result.Skipped, result.Conflicts));

        var items = await Entities.ListByJobAsync(s.Job.JobId);
        Assert.Equal(["4x12x7", "4x12x7", "4x12x7", "4x10x7"], items.Select(i => i.Size));
        Assert.All(items.Take(3), i => Assert.Equal((ResolutionSource.Rule, approved.RuleId, 2), (i.ResolutionSource, i.ResolvedByRuleId, i.Revision)));
        Assert.Equal((ResolutionSource.Detector, 1), (items[3].ResolutionSource, items[3].Revision));

        Assert.Equal((1, 1, 1, 1), (await EventsAsync(s.Job.JobId, "rule.created"), await EventsAsync(s.Job.JobId, "rule.approved"), await EventsAsync(s.Job.JobId, "rule.applied"), 1));
        Assert.Equal(3, await db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.RuleApplication WHERE RuleId = @r;", ("@r", approved.RuleId)));
        Assert.Equal("Rule", (await Review.ListRevisionsAsync(items[0].EntityId)).Single().Action);
        Assert.True((await new JobRepository(db.ConnectionString).VerifyAuditChainAsync(s.Job.JobId)).IsValid);
    }

    [Fact]
    public async Task Applying_again_changes_nothing_so_a_multiplying_rule_cannot_double()
    {
        var s = await JobAsync([Boot("d1", "4x10x7", "Type 4", "PT-FLAG", 1)]);
        var rule = await ApprovedAsync(s, new NewRule { Name = "Pass-through flag means two grilles", Match = new RuleMatch { Tag = "PT-FLAG" }, Action = new RuleAction { MultiplyQuantity = 2 } });

        await Rules.ApplyAsync(s.Job.JobId, rule.RuleId, s.Token, Ctx(Editor));
        var again = await Rules.ApplyAsync(s.Job.JobId, rule.RuleId, s.Token, Ctx(Editor));

        Assert.Equal((0, 1), (again.Changed, again.Skipped));
        Assert.Equal(2m, (await Entities.ListByJobAsync(s.Job.JobId)).Single().Quantity);
    }

    [Fact]
    public async Task What_a_person_set_what_they_added_and_what_they_rejected_is_never_touched_by_a_rule()
    {
        var s = await JobAsync();
        var overridden = s.Items[0];
        var rejected = s.Items[1];
        await Review.EditAsync(overridden.EntityId, new EntityEdit { Tag = "T-1" }, overridden.Revision, null, s.Token, Ctx(Editor));
        await Review.SetReviewStatusAsync(rejected.EntityId, ReviewStatus.Rejected, rejected.Revision, null, s.Token, Ctx(Editor));
        var added = await Review.AddAsync(s.Job.JobId, new NewManualEntity { Type = "Boot", Size = "4x12", BuildingTypeId = null }, s.Token, Ctx(Editor));
        var rule = await ApprovedAsync(s);

        var preview = await Rules.GetPreviewAsync(s.Job.JobId, rule.RuleId);
        Assert.Equal([s.Items[2].EntityId], preview.Changes.Select(c => c.EntityId));
        Assert.Equal(2, preview.Skipped.Count);                          // the override and the hand-added item; the rejected one is not even listed

        await Rules.ApplyAsync(s.Job.JobId, rule.RuleId, s.Token, Ctx(Editor));
        Assert.Equal("4x12", (await ItemAsync(overridden.EntityId)).Size);
        Assert.Equal("4x12", (await ItemAsync(rejected.EntityId)).Size);
        Assert.Equal("4x12", (await ItemAsync(added.EntityId)).Size);
    }

    [Fact]
    public async Task A_person_editing_a_rule_decided_item_makes_it_their_own_override()
    {
        var s = await JobAsync();
        var rule = await ApprovedAsync(s);
        await Rules.ApplyAsync(s.Job.JobId, rule.RuleId, s.Token, Ctx(Editor));
        var decided = await ItemAsync(s.Items[0].EntityId);

        var edited = await Review.EditAsync(decided.EntityId, new EntityEdit { Size = "4x12x9" }, decided.Revision, null, s.Token, Ctx(Editor));

        Assert.Equal((ResolutionSource.Override, (long?)null), (edited.ResolutionSource, edited.ResolvedByRuleId));
    }

    // ---- undoing a rule

    [Fact]
    public async Task Undoing_a_rule_restores_what_it_changed_and_leaves_what_changed_since()
    {
        var s = await JobAsync();
        var rule = await ApprovedAsync(s);
        await Rules.ApplyAsync(s.Job.JobId, rule.RuleId, s.Token, Ctx(Editor));
        var touched = await ItemAsync(s.Items[0].EntityId);
        await Review.SetReviewStatusAsync(touched.EntityId, ReviewStatus.Verified, touched.Revision, null, s.Token, Ctx(Editor));   // a later change on one item

        var undone = await Rules.UndoApplicationAsync(s.Job.JobId, rule.RuleId, s.Token, Ctx(Editor));

        Assert.Equal((2, 1), (undone.Undone, undone.Skipped));
        var items = await Entities.ListByJobAsync(s.Job.JobId);
        Assert.Equal(["4x12x7", "4x12", "4x12", "4x10x7"], items.Select(i => i.Size));
        Assert.Equal(ResolutionSource.Detector, items[1].ResolutionSource);
        Assert.Equal(1, await EventsAsync(s.Job.JobId, "rule.undone"));
    }

    // ---- who may, and what is checked

    [Fact]
    public async Task Only_a_person_with_the_lock_and_edit_permission_can_write_approve_or_apply()
    {
        var s = await JobAsync();

        await Assert.ThrowsAsync<JobAccessDeniedException>(() => Rules.CreateAsync(s.Job.JobId, Suffix(), s.Token, Ctx(Viewer)));
        await Assert.ThrowsAsync<StaleEditTokenException>(() => Rules.CreateAsync(s.Job.JobId, Suffix(), s.Token, Ctx(Editor, "other-tab")));
        await Assert.ThrowsAsync<StaleEditTokenException>(() => Rules.CreateAsync(s.Job.JobId, Suffix(), s.Token, AuditContext.ForWorker("w1")));
        Assert.DoesNotContain(await Rules.ListForJobAsync(s.Job.JobId), r => r.Scope != RuleScope.Instance);
    }

    [Fact]
    public async Task A_rule_that_does_nothing_or_matches_everything_or_has_a_bad_pattern_is_refused()
    {
        var s = await JobAsync();
        var ctx = Ctx(Editor);

        await Assert.ThrowsAsync<JobRuleViolationException>(() => Rules.CreateAsync(s.Job.JobId, new NewRule { Name = "x", Match = new RuleMatch { Type = "Boot" }, Action = new RuleAction() }, s.Token, ctx));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Rules.CreateAsync(s.Job.JobId, new NewRule { Name = "x", Match = new RuleMatch(), Action = new RuleAction { SetSize = "1x1" } }, s.Token, ctx));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Rules.CreateAsync(s.Job.JobId, new NewRule { Name = "x", Match = new RuleMatch { SizePattern = "([" }, Action = new RuleAction { SetSize = "1x1" } }, s.Token, ctx));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Rules.CreateAsync(s.Job.JobId, new NewRule { Name = " ", Match = new RuleMatch { Type = "Boot" }, Action = new RuleAction { SetSize = "1x1" } }, s.Token, ctx));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Rules.CreateAsync(s.Job.JobId, new NewRule { Name = "x", Match = new RuleMatch { Method = "Telepathy" }, Action = new RuleAction { SetSize = "1x1" } }, s.Token, ctx));
        Assert.DoesNotContain(await Rules.ListForJobAsync(s.Job.JobId), r => r.Scope != RuleScope.Instance);
    }

    // ---- scope and precedence

    [Fact]
    public async Task A_building_type_rule_only_reaches_that_type_and_a_type_from_another_job_is_refused()
    {
        var s = await JobAsync();
        var other = await JobAsync();
        var types = new BuildingTypeRepository(db.ConnectionString);
        var type4 = (await types.ListByJobAsync(s.Job.JobId)).Single(t => t.Name == "Type 4");
        var foreign = (await types.ListByJobAsync(other.Job.JobId)).First();

        await Assert.ThrowsAsync<JobRuleViolationException>(() => Rules.CreateAsync(s.Job.JobId, Suffix(scope: RuleScope.BuildingType) with { BuildingTypeId = foreign.BuildingTypeId }, s.Token, Ctx(Editor)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Rules.CreateAsync(s.Job.JobId, Suffix(scope: RuleScope.BuildingType), s.Token, Ctx(Editor)));

        var rule = await ApprovedAsync(s, Suffix(scope: RuleScope.BuildingType) with { BuildingTypeId = type4.BuildingTypeId });
        var result = await Rules.ApplyAsync(s.Job.JobId, rule.RuleId, s.Token, Ctx(Editor));

        Assert.Equal(2, result.Changed);                                    // the two Type 4 boots, not the Type 5 one
        Assert.Equal("4x12", (await Entities.ListByJobAsync(s.Job.JobId)).Single(e => e.DetectionId == "d3").Size);
    }

    [Fact]
    public async Task A_sheet_rule_only_reaches_items_placed_on_views_of_that_sheet()
    {
        var s = await JobAsync();
        var (sheet, viewId) = await ViewAsync(s);
        var first = s.Items[0];
        await Review.EditAsync(first.EntityId, new EntityEdit { SheetViewId = viewId }, first.Revision, null, s.Token, Ctx(Editor));

        var rule = await ApprovedAsync(s, Suffix(scope: RuleScope.Sheet) with { SheetId = sheet });
        var result = await Rules.ApplyAsync(s.Job.JobId, rule.RuleId, s.Token, Ctx(Editor));

        Assert.Equal(1, result.Changed);
        Assert.Equal("4x12x7", (await ItemAsync(first.EntityId)).Size);
        Assert.Equal("4x12", (await ItemAsync(s.Items[1].EntityId)).Size);
    }

    private async Task<(long SheetId, long ViewId)> ViewAsync(Setup s)
    {
        var data = new byte[300];
        Array.Fill(data, (byte)7);
        Encoding.ASCII.GetBytes("%PDF-1.7\n").CopyTo(data, 0);
        var uploads = new UploadService(Open, new FileSystemArtifactStore(_root), new UploadSettings { ChunkBytes = 1000 });
        var session = await uploads.BeginAsync(s.Job.JobId, new BeginUpload("r.pdf", data.Length, null), s.Token, Ctx(Editor));
        await uploads.AppendAsync(session.PublicId, 0, new MemoryStream(data), null, Ctx(Editor));
        var file = await uploads.CompleteAsync(session.PublicId, s.Token, Ctx(Editor));
        var sheets = new SheetRepository(Open);
        var sheet = (await sheets.RegisterAsync(s.Job.JobId, file.PublicId, [new NewSheet { PageNo = 1, WidthPts = 2592, HeightPts = 1728 }], s.Token, Ctx(Editor)))[0];
        return (sheet.SheetId, (await sheets.CreateViewAsync(sheet.SheetId, new NewView { Title = "Plan" }, s.Token, Ctx(Editor))).SheetViewId);
    }

    [Fact]
    public async Task An_instance_rule_needs_a_job_manager_or_an_administrator_and_then_reaches_every_job()
    {
        var a = await JobAsync();
        var b = await JobAsync();
        var rule = Suffix("Shared boot convention", RuleScope.Instance);

        await Assert.ThrowsAsync<JobAccessDeniedException>(() => Rules.CreateAsync(a.Job.JobId, rule, a.Token, Ctx(Editor)));

        await db.ExecAsync("INSERT INTO ductwork.ToolPermission (UserEmail, Permission, GrantedBy) VALUES (@e, 'JobManager', 'test');", ("@e", Editor));
        try
        {
            var created = await Rules.CreateAsync(a.Job.JobId, rule, a.Token, Ctx(Editor));
            var approved = await Rules.ApproveAsync(a.Job.JobId, created.RuleId, a.Token, Ctx(Editor));
            Assert.Equal((RuleScope.Instance, (long?)null), (approved.Scope, approved.JobId));
        }
        finally
        {
            await db.ExecAsync("DELETE FROM ductwork.ToolPermission WHERE UserEmail = @e;", ("@e", Editor));
        }

        var listed = (await Rules.ListForJobAsync(b.Job.JobId)).Single(r => r.Name == "Shared boot convention");
        var tokenB = (await new EditLockService(db.ConnectionString).AcquireAsync(b.Job.JobId, Ctx(Manager))).Token ?? b.Token;
        _ = tokenB;
        var applied = await Rules.ApplyAsync(b.Job.JobId, listed.RuleId, b.Token, Ctx(Editor));       // using a shared rule needs only edit permission on the job
        Assert.Equal(3, applied.Changed);
    }

    [Fact]
    public async Task A_job_rule_of_another_job_is_not_reachable()
    {
        var a = await JobAsync();
        var b = await JobAsync();
        var rule = await ApprovedAsync(a);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => Rules.ApplyAsync(b.Job.JobId, rule.RuleId, b.Token, Ctx(Editor)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Rules.GetPreviewAsync(b.Job.JobId, rule.RuleId));
        Assert.Null(await Rules.GetByPublicIdAsync(b.Job.JobId, rule.PublicId));
        Assert.DoesNotContain(await Rules.ListForJobAsync(b.Job.JobId), r => r.RuleId == rule.RuleId);
    }

    [Fact]
    public async Task A_stronger_rule_is_never_overwritten_by_a_weaker_one_and_equals_that_disagree_raise_a_finding()
    {
        var s = await JobAsync([Boot("d1", "4x12")]);
        var type4 = (await new BuildingTypeRepository(db.ConnectionString).ListByJobAsync(s.Job.JobId)).Single();

        // Same strength, different rules, both want to decide the item.
        var first = await ApprovedAsync(s, new NewRule { Name = "Depth 7", Match = new RuleMatch { Type = "Boot", SizePattern = @"^\d+x\d+$" }, Action = new RuleAction { SizeSuffix = "x7" } });
        var second = await ApprovedAsync(s, new NewRule { Name = "Depth 4", Match = new RuleMatch { Type = "Boot", SizePattern = @"^\d+x\d+x7$" }, Action = new RuleAction { SizeSuffix = "x4" } });
        await Rules.ApplyAsync(s.Job.JobId, first.RuleId, s.Token, Ctx(Editor));

        var clash = await Rules.ApplyAsync(s.Job.JobId, second.RuleId, s.Token, Ctx(Editor));
        Assert.Equal((0, 1, 1), (clash.Changed, clash.Conflicts, clash.FindingsRaised));
        var open = await Findings.ListByJobAsync(s.Job.JobId, FindingState.Open);
        Assert.Contains("disagree", open.Single().Question);

        var again = await Rules.ApplyAsync(s.Job.JobId, second.RuleId, s.Token, Ctx(Editor));
        Assert.Equal(0, again.FindingsRaised);                              // not raised twice
        Assert.Equal("4x12x7", (await Entities.ListByJobAsync(s.Job.JobId)).Single().Size);

        // A building-type rule is weaker than a job rule, so it is skipped rather than winning.
        var weaker = await ApprovedAsync(s, new NewRule
        {
            Name = "Type rule", Scope = RuleScope.BuildingType, BuildingTypeId = type4.BuildingTypeId, Match = new RuleMatch { Type = "Boot" }, Action = new RuleAction { SetSize = "9x9x9" }
        });
        var skipped = await Rules.ApplyAsync(s.Job.JobId, weaker.RuleId, s.Token, Ctx(Editor));
        Assert.Equal((0, 1, 0), (skipped.Changed, skipped.Skipped, skipped.Conflicts));
        Assert.Equal("4x12x7", (await Entities.ListByJobAsync(s.Job.JobId)).Single().Size);
    }

    [Fact]
    public async Task A_stronger_rule_replaces_what_a_weaker_one_decided()
    {
        var s = await JobAsync([Boot("d1", "4x12")]);
        var type4 = (await new BuildingTypeRepository(db.ConnectionString).ListByJobAsync(s.Job.JobId)).Single();
        var weak = await ApprovedAsync(s, new NewRule { Name = "Type depth 4", Scope = RuleScope.BuildingType, BuildingTypeId = type4.BuildingTypeId, Match = new RuleMatch { Type = "Boot", SizePattern = @"^\d+x\d+$" }, Action = new RuleAction { SizeSuffix = "x4" } });
        await Rules.ApplyAsync(s.Job.JobId, weak.RuleId, s.Token, Ctx(Editor));
        Assert.Equal("4x12x4", (await Entities.ListByJobAsync(s.Job.JobId)).Single().Size);

        var strong = await ApprovedAsync(s, new NewRule { Name = "Job: set 4x12x7", Match = new RuleMatch { Type = "Boot", Size = "4x12x4" }, Action = new RuleAction { SetSize = "4x12x7" } });
        var result = await Rules.ApplyAsync(s.Job.JobId, strong.RuleId, s.Token, Ctx(Editor));

        Assert.Equal(1, result.Changed);
        var item = (await Entities.ListByJobAsync(s.Job.JobId)).Single();
        Assert.Equal(("4x12x7", strong.RuleId), (item.Size, item.ResolvedByRuleId));
    }

    // ---- versions and retiring

    [Fact]
    public async Task A_change_is_a_new_version_and_approving_it_retires_the_one_in_force()
    {
        var s = await JobAsync();
        var v1 = await ApprovedAsync(s);

        var v2 = await Rules.ReviseAsync(s.Job.JobId, v1.RuleId, Suffix("Depth 7 (revised)") with { Action = new RuleAction { SizeSuffix = "x8" } }, s.Token, Ctx(Editor));
        Assert.Equal((2, RuleStatus.Proposed, v1.RuleKey), (v2.Version, v2.Status, v2.RuleKey));
        Assert.Equal(RuleStatus.Approved, (await Rules.GetAsync(v1.RuleId))!.Status);            // still the one in force

        await Rules.ApproveAsync(s.Job.JobId, v2.RuleId, s.Token, Ctx(Editor));
        var old = (await Rules.GetAsync(v1.RuleId))!;
        Assert.Equal(RuleStatus.Retired, old.Status);
        Assert.Contains("Superseded by version 2", old.RetireReason);
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Rules.ApplyAsync(s.Job.JobId, v1.RuleId, s.Token, Ctx(Editor)));

        await Assert.ThrowsAsync<JobRuleViolationException>(() => Rules.ReviseAsync(s.Job.JobId, v2.RuleId, Suffix(scope: RuleScope.Instance), s.Token, Ctx(Editor)));
    }

    [Fact]
    public async Task Retiring_needs_a_reason_and_stops_a_rule_without_undoing_what_it_did()
    {
        var s = await JobAsync();
        var rule = await ApprovedAsync(s);
        await Rules.ApplyAsync(s.Job.JobId, rule.RuleId, s.Token, Ctx(Editor));

        await Assert.ThrowsAsync<JobRuleViolationException>(() => Rules.RetireAsync(s.Job.JobId, rule.RuleId, " ", s.Token, Ctx(Editor)));
        var retired = await Rules.RetireAsync(s.Job.JobId, rule.RuleId, "Estimator changed the convention", s.Token, Ctx(Editor));

        Assert.Equal((RuleStatus.Retired, Editor), (retired.Status, retired.RetiredBy));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Rules.ApplyAsync(s.Job.JobId, rule.RuleId, s.Token, Ctx(Editor)));
        Assert.Equal("4x12x7", (await Entities.ListByJobAsync(s.Job.JobId)).First().Size);
    }

    [Fact]
    public async Task The_database_keeps_rules_immutable_undeletable_and_a_retired_one_retired()
    {
        var s = await JobAsync();
        var rule = await ApprovedAsync(s);

        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync("UPDATE ductwork.Rule SET ActionJson = '{}' WHERE RuleId = @r;", ("@r", rule.RuleId)));
        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync("DELETE FROM ductwork.Rule WHERE RuleId = @r;", ("@r", rule.RuleId)));

        await Rules.RetireAsync(s.Job.JobId, rule.RuleId, "done", s.Token, Ctx(Editor));
        await Assert.ThrowsAnyAsync<SqlException>(() => db.ExecAsync("UPDATE ductwork.Rule SET Status = 'Approved', RetiredBy = NULL, RetiredUtc = NULL, RetireReason = NULL WHERE RuleId = @r;", ("@r", rule.RuleId)));
        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync(
            "UPDATE ductwork.Rule SET Status = 'Proposed', RetiredBy = NULL, RetiredUtc = NULL, RetireReason = NULL WHERE RuleKey = @k AND Version = 1;", ("@k", rule.RuleKey)));
    }

    // ---- findings

    private static FindingProposal Question(string id, string detection) => new()
    {
        FindingId = id, Kind = FindingKind.GrammarVariant, Question = "A boot labeled 4x12: is the depth 7?", Candidates = ["4x12x7", "4x12x4"], DetectionId = detection, Evidence = "label 4x12"
    };

    [Fact]
    public async Task An_open_finding_keeps_the_count_sheet_from_being_final_until_it_is_answered()
    {
        var s = await JobAsync([Boot("d1", "4x12x7")], [Question("f1", "d1")]);
        var sheets = new CountSheetRepository(Open);

        var before = await sheets.BuildAsync(s.Job.JobId);
        Assert.Equal(1, before.OpenFindings.Count);
        Assert.Contains("depth 7", before.OpenFindings[0].Question);
        Assert.False(before.IsFinal);

        var finding = (await Findings.ListByJobAsync(s.Job.JobId)).Single();
        await Findings.WaiveAsync(s.Job.JobId, finding.FindingId, "Confirmed with the engineer", s.Token, Ctx(Editor));

        Assert.Empty((await sheets.BuildAsync(s.Job.JobId)).OpenFindings);
    }

    [Fact]
    public async Task A_finding_is_answered_by_a_rule_only_after_the_rule_has_decided_the_item()
    {
        var s = await JobAsync([Boot("d1", "4x12")], [Question("f1", "d1")]);
        var finding = (await Findings.ListByJobAsync(s.Job.JobId)).Single();
        var rule = await ApprovedAsync(s);

        var tooEarly = await Assert.ThrowsAsync<JobRuleViolationException>(() => Findings.ResolveByRuleAsync(s.Job.JobId, finding.FindingId, rule.RuleId, null, s.Token, Ctx(Editor)));
        Assert.Contains("Apply the rule first", tooEarly.Message);

        await Rules.ApplyAsync(s.Job.JobId, rule.RuleId, s.Token, Ctx(Editor));
        var resolved = await Findings.ResolveByRuleAsync(s.Job.JobId, finding.FindingId, rule.RuleId, "Estimator convention", s.Token, Ctx(Editor));

        Assert.Equal((FindingState.Resolved, FindingResolutionKind.Rule, rule.PublicId, Editor), (resolved.State, resolved.ResolvedByKind, resolved.ResolvedRulePublicId, resolved.ResolvedBy));
        Assert.Equal("Estimator convention", resolved.ResolutionNote);
        Assert.Equal(1, await EventsAsync(s.Job.JobId, "finding.resolved-by-rule"));
    }

    [Fact]
    public async Task A_rule_that_is_not_approved_or_belongs_elsewhere_cannot_answer_a_finding()
    {
        var s = await JobAsync([Boot("d1", "4x12")], [Question("f1", "d1")]);
        var other = await JobAsync();
        var finding = (await Findings.ListByJobAsync(s.Job.JobId)).Single();
        var proposed = await Rules.CreateAsync(s.Job.JobId, Suffix(), s.Token, Ctx(Editor));
        var foreign = await ApprovedAsync(other);

        await Assert.ThrowsAsync<JobRuleViolationException>(() => Findings.ResolveByRuleAsync(s.Job.JobId, finding.FindingId, proposed.RuleId, null, s.Token, Ctx(Editor)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Findings.ResolveByRuleAsync(s.Job.JobId, finding.FindingId, foreign.RuleId, null, s.Token, Ctx(Editor)));
    }

    [Fact]
    public async Task A_finding_is_answered_by_override_only_once_the_item_was_changed_or_rejected()
    {
        var s = await JobAsync([Boot("d1", "4x12"), Boot("d2", "4x12")], [Question("f1", "d1"), Question("f2", "d2")]);
        var findings = await Findings.ListByJobAsync(s.Job.JobId);
        var item = s.Items[0];

        await Assert.ThrowsAsync<JobRuleViolationException>(() => Findings.ResolveByOverrideAsync(s.Job.JobId, findings[0].FindingId, null, s.Token, Ctx(Editor)));

        await Review.EditAsync(item.EntityId, new EntityEdit { Size = "4x12x7" }, item.Revision, null, s.Token, Ctx(Editor));
        var done = await Findings.ResolveByOverrideAsync(s.Job.JobId, findings[0].FindingId, "Set by hand", s.Token, Ctx(Editor));
        Assert.Equal((FindingState.Resolved, FindingResolutionKind.Override), (done.State, done.ResolvedByKind));

        await Review.SetReviewStatusAsync(s.Items[1].EntityId, ReviewStatus.Rejected, 1, "Not real", s.Token, Ctx(Editor));
        Assert.Equal(FindingState.Resolved, (await Findings.ResolveByOverrideAsync(s.Job.JobId, findings[1].FindingId, null, s.Token, Ctx(Editor))).State);
    }

    [Fact]
    public async Task Waiving_needs_a_reason_a_closed_finding_can_be_reopened_and_only_a_person_may_answer()
    {
        var s = await JobAsync([Boot("d1", "4x12")], [Question("f1", "d1")]);
        var finding = (await Findings.ListByJobAsync(s.Job.JobId)).Single();

        await Assert.ThrowsAsync<JobRuleViolationException>(() => Findings.WaiveAsync(s.Job.JobId, finding.FindingId, "  ", s.Token, Ctx(Editor)));
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => Findings.WaiveAsync(s.Job.JobId, finding.FindingId, "x", s.Token, Ctx(Viewer)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Findings.WaiveAsync(s.Job.JobId, finding.FindingId, "x", s.Token, AuditContext.ForWorker("w1")));

        var waived = await Findings.WaiveAsync(s.Job.JobId, finding.FindingId, "Not needed for this bid", s.Token, Ctx(Editor));
        Assert.Equal((FindingState.Waived, FindingResolutionKind.Waiver), (waived.State, waived.ResolvedByKind));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Findings.WaiveAsync(s.Job.JobId, finding.FindingId, "again", s.Token, Ctx(Editor)));

        var reopened = await Findings.ReopenAsync(s.Job.JobId, finding.FindingId, s.Token, Ctx(Editor));
        Assert.Equal((FindingState.Open, (FindingResolutionKind?)null, (string?)null), (reopened.State, reopened.ResolvedByKind, reopened.ResolutionNote));
    }

    [Fact]
    public async Task A_finding_of_another_job_is_not_reachable_and_the_database_refuses_a_waiver_with_no_note()
    {
        var s = await JobAsync([Boot("d1", "4x12")], [Question("f1", "d1")]);
        var other = await JobAsync([Boot("d1", "4x12")], [Question("f1", "d1")]);
        var foreign = (await Findings.ListByJobAsync(other.Job.JobId)).Single();
        var own = (await Findings.ListByJobAsync(s.Job.JobId)).Single();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => Findings.WaiveAsync(s.Job.JobId, foreign.FindingId, "x", s.Token, Ctx(Editor)));
        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync(
            "UPDATE ductwork.Finding SET State = 'Waived', ResolvedByKind = 'Waiver', ResolvedBy = 'x', ResolvedUtc = SYSUTCDATETIME() WHERE FindingId = @f;", ("@f", own.FindingId)));
    }
}
