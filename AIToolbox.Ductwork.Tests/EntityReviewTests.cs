using System.Text;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Counting;
using AIToolbox.Ductwork.Core.Data;
using AIToolbox.Ductwork.Core.Drawings;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Tasks;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Tests;

public sealed class EntityReviewTests(SqlFixture db) : IClassFixture<SqlFixture>, IDisposable
{
    private const string Owner = "owner@example.com";
    private const string Editor = "editor@example.com";
    private const string Second = "second@example.com";
    private const string Viewer = "viewer@example.com";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ductwork-review-" + Guid.NewGuid().ToString("N"));

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

    private EntityReviewRepository Review => new(Open);
    private EntityRepository Entities => new(db.ConnectionString);

    private static AuditContext Ctx(string email, string? session = null) =>
        AuditContext.ForUser(email, displayName: email.Split('@')[0], sessionId: session ?? "s-" + email, userAgent: "tests");

    private sealed record Setup(JobRecord Job, EditToken Token, IReadOnlyList<EntityRecord> Items);

    private static readonly ProducerRef Algorithm = ProducerRef.Algorithm("review-counter", "1.0", "h");

    private static DetectionProposal Detection(string id, string size = "4x10x7", string building = "Type 4", string type = "Boot") => new()
    {
        DetectionId = id, Type = type, Size = size, BuildingType = building, Quantity = 1, Confidence = 0.95,
        Method = DetectionMethod.Text, Family = DetectorFamily.LabelFlagCount, Box = new BoundingBox(1, 2, 3, 4), SheetViewRef = "M1.2", Evidence = "label"
    };

    /// <summary>A job with the detector's items already in it, ready to be reviewed by the editor holding the lock.</summary>
    private async Task<Setup> JobWithItemsAsync(int count = 3)
    {
        var jobs = new JobRepository(db.ConnectionString);
        var job = await jobs.CreateAsync(new NewJob { Name = "Review " + Guid.NewGuid().ToString("N")[..8] }, Ctx(Owner));
        await jobs.AddMemberAsync(job.JobId, Editor, JobRole.Editor, Ctx(Owner));
        await jobs.AddMemberAsync(job.JobId, Second, JobRole.Editor, Ctx(Owner));
        await jobs.AddMemberAsync(job.JobId, Viewer, JobRole.Viewer, Ctx(Owner));
        var token = (await new EditLockService(db.ConnectionString).AcquireAsync(job.JobId, Ctx(Editor))).Token!;

        await using var connection = await Open(default);
        var producer = await ProducerStore.EnsureProducerAsync(connection, Algorithm);
        var type = "detect-" + Guid.NewGuid().ToString("N")[..8];
        var tasks = new ProcessingTaskRepository(db.ConnectionString);
        var queued = await tasks.EnqueueAsync(new NewProcessingTask { JobId = job.JobId, TaskType = type, ProducerId = producer }, token, Ctx(Editor));
        var worker = AuditContext.ForWorker("w1");
        var claimed = await tasks.ClaimNextAsync(worker, [type]);
        Assert.Equal(queued.ProcessingTaskId, claimed!.ProcessingTaskId);

        var manifest = new TaskResultManifest
        {
            ContractVersion = ContractInfo.Version, TaskType = type, TaskPublicId = claimed.PublicId.ToString("D"), Producer = Algorithm,
            Detections = Enumerable.Range(1, count).Select(i => Detection("d" + i, size: $"4x1{i}x7")).ToList(), Findings = []
        };
        await new TaskResultIngestor(db.ConnectionString).SubmitAsync(claimed.ProcessingTaskId, worker, manifest);
        return new Setup(job, token, await Entities.ListByJobAsync(job.JobId));
    }

    private Task<int> EventsAsync(long jobId, string action) =>
        db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.JobAuditEvent WHERE JobId = @j AND Action = @a;", ("@j", jobId), ("@a", action));

    private Task<int> RevisionsAsync(long entityId) =>
        db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.EntityRevision WHERE EntityId = @e;", ("@e", entityId));

    // ---- review status

    [Fact]
    public async Task A_person_verifies_and_rejects_and_each_change_is_a_revision_and_an_audit_event()
    {
        var s = await JobWithItemsAsync();
        var item = s.Items[0];

        var verified = await Review.SetReviewStatusAsync(item.EntityId, ReviewStatus.Verified, item.Revision, null, s.Token, Ctx(Editor));
        Assert.Equal((ReviewStatus.Verified, 2, ResolutionSource.Detector), (verified.ReviewStatus, verified.Revision, verified.ResolutionSource));

        var rejected = await Review.SetReviewStatusAsync(item.EntityId, ReviewStatus.Rejected, verified.Revision, "Not a boot", s.Token, Ctx(Editor));
        Assert.Equal((ReviewStatus.Rejected, 3), (rejected.ReviewStatus, rejected.Revision));

        var history = await Review.ListRevisionsAsync(item.EntityId);
        Assert.Equal([2, 3], history.Select(r => r.RevisionNo));
        Assert.Equal(("Reviewed", Editor, "Not a boot"), (history[1].Action, history[1].ChangedBy, history[1].Reason));
        Assert.Equal(2, await EventsAsync(s.Job.JobId, "review.status"));
        Assert.True((await new JobRepository(db.ConnectionString).VerifyAuditChainAsync(s.Job.JobId)).IsValid);
    }

    [Fact]
    public async Task Setting_the_status_it_already_has_changes_nothing()
    {
        var s = await JobWithItemsAsync();
        var item = s.Items[0];

        var same = await Review.SetReviewStatusAsync(item.EntityId, ReviewStatus.Proposed, item.Revision, null, s.Token, Ctx(Editor));

        Assert.Equal(item.Revision, same.Revision);
        Assert.Equal(0, await RevisionsAsync(item.EntityId));
    }

    [Fact]
    public async Task A_worker_or_system_cannot_review_and_an_item_cannot_be_set_to_manual()
    {
        var s = await JobWithItemsAsync();
        var item = s.Items[0];

        await Assert.ThrowsAsync<StaleEditTokenException>(() => Review.SetReviewStatusAsync(item.EntityId, ReviewStatus.Verified, item.Revision, null, s.Token, AuditContext.ForWorker("w1")));   // a worker cannot hold an edit token
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Review.SetReviewStatusAsync(item.EntityId, ReviewStatus.Manual, item.Revision, null, s.Token, Ctx(Editor)));
        Assert.Equal(ReviewStatus.Proposed, (await Review.GetAsync(item.EntityId))!.ReviewStatus);
    }

    [Fact]
    public async Task Review_needs_the_lock_edit_permission_and_the_current_revision()
    {
        var s = await JobWithItemsAsync();
        var item = s.Items[0];

        await Assert.ThrowsAsync<StaleEditTokenException>(() => Review.SetReviewStatusAsync(item.EntityId, ReviewStatus.Verified, item.Revision, null, s.Token, Ctx(Editor, "other-tab")));
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => Review.SetReviewStatusAsync(item.EntityId, ReviewStatus.Verified, item.Revision, null, s.Token, Ctx(Viewer)));
        var stale = await Assert.ThrowsAsync<EntityConflictException>(() => Review.SetReviewStatusAsync(item.EntityId, ReviewStatus.Verified, item.Revision + 5, null, s.Token, Ctx(Editor)));
        Assert.Equal(item.Revision, stale.Actual);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Review.SetReviewStatusAsync(long.MaxValue, ReviewStatus.Verified, 1, null, s.Token, Ctx(Editor)));
    }

    [Fact]
    public async Task A_batch_is_verified_in_one_step_skipping_what_is_already_done_and_refusing_other_jobs_items()
    {
        var s = await JobWithItemsAsync(4);
        var other = await JobWithItemsAsync(1);
        await Review.SetReviewStatusAsync(s.Items[0].EntityId, ReviewStatus.Verified, 1, null, s.Token, Ctx(Editor));

        var changed = await Review.SetReviewStatusManyAsync(s.Job.JobId, s.Items.Select(i => i.EntityId).ToList(), ReviewStatus.Verified, null, s.Token, Ctx(Editor));

        Assert.Equal(3, changed);
        Assert.All(await Entities.ListByJobAsync(s.Job.JobId), e => Assert.Equal(ReviewStatus.Verified, e.ReviewStatus));
        Assert.Equal(1, await EventsAsync(s.Job.JobId, "review.status-many"));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => Review.SetReviewStatusManyAsync(s.Job.JobId, [other.Items[0].EntityId], ReviewStatus.Rejected, null, s.Token, Ctx(Editor)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Review.SetReviewStatusManyAsync(s.Job.JobId, [], ReviewStatus.Verified, null, s.Token, Ctx(Editor)));
        Assert.Equal(ReviewStatus.Proposed, (await Review.GetAsync(other.Items[0].EntityId))!.ReviewStatus);
    }

    [Fact]
    public async Task A_batch_that_hits_a_bad_item_changes_nothing()
    {
        var s = await JobWithItemsAsync(3);
        var ids = s.Items.Select(i => i.EntityId).Append(long.MaxValue).ToList();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => Review.SetReviewStatusManyAsync(s.Job.JobId, ids, ReviewStatus.Verified, null, s.Token, Ctx(Editor)));

        Assert.All(await Entities.ListByJobAsync(s.Job.JobId), e => Assert.Equal(ReviewStatus.Proposed, e.ReviewStatus));
    }

    // ---- disposition

    [Fact]
    public async Task Disposition_is_decided_apart_from_review_and_leaving_an_item_out_needs_a_reason()
    {
        var s = await JobWithItemsAsync();
        var item = s.Items[0];

        await Assert.ThrowsAsync<JobRuleViolationException>(() => Review.SetDispositionAsync(item.EntityId, Disposition.Excluded, " ", item.Revision, s.Token, Ctx(Editor)));
        var excluded = await Review.SetDispositionAsync(item.EntityId, Disposition.Excluded, "Not in scope per spec 23 31 13", item.Revision, s.Token, Ctx(Editor));
        Assert.Equal((Disposition.Excluded, "Not in scope per spec 23 31 13", ReviewStatus.Proposed), (excluded.Disposition, excluded.DispositionReason, excluded.ReviewStatus));

        var back = await Review.SetDispositionAsync(item.EntityId, Disposition.Included, null, excluded.Revision, s.Token, Ctx(Editor));
        Assert.Equal((Disposition.Included, (string?)null), (back.Disposition, back.DispositionReason));
        Assert.Equal(2, await EventsAsync(s.Job.JobId, "review.disposition"));
    }

    // ---- edits

    [Fact]
    public async Task Changing_what_an_item_is_makes_it_an_override_and_placing_it_does_not()
    {
        var s = await JobWithItemsAsync();
        var item = s.Items[0];
        var type = await new BuildingTypeRepository(db.ConnectionString).CreateAsync(s.Job.JobId, new NewBuildingType { Name = "Type 9" }, s.Token, Ctx(Editor));

        var placed = await Review.EditAsync(item.EntityId, new EntityEdit { BuildingTypeId = type.BuildingTypeId }, item.Revision, null, s.Token, Ctx(Editor));
        Assert.Equal((type.BuildingTypeId, ResolutionSource.Detector), (placed.BuildingTypeId, placed.ResolutionSource));

        var changed = await Review.EditAsync(item.EntityId, new EntityEdit { Size = " 6x12x7 ", Quantity = 2, Tag = "B-1" }, placed.Revision, "Label misread", s.Token, Ctx(Editor));
        Assert.Equal(("6x12x7", 2m, "B-1", ResolutionSource.Override), (changed.Size, changed.Quantity, changed.Tag, changed.ResolutionSource));
        Assert.Equal(("Boot", ReviewStatus.Proposed), (changed.Type, changed.ReviewStatus));        // edits do not approve anything
    }

    [Fact]
    public async Task An_edit_that_changes_nothing_writes_nothing_and_bad_values_are_refused()
    {
        var s = await JobWithItemsAsync();
        var item = s.Items[0];

        var same = await Review.EditAsync(item.EntityId, new EntityEdit { Type = item.Type, Size = item.Size, Quantity = item.Quantity }, item.Revision, null, s.Token, Ctx(Editor));
        Assert.Equal(item.Revision, same.Revision);

        await Assert.ThrowsAsync<JobRuleViolationException>(() => Review.EditAsync(item.EntityId, new EntityEdit { Type = "  " }, item.Revision, null, s.Token, Ctx(Editor)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Review.EditAsync(item.EntityId, new EntityEdit { Quantity = -1 }, item.Revision, null, s.Token, Ctx(Editor)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Review.EditAsync(item.EntityId, new EntityEdit { BuildingTypeId = 999_999 }, item.Revision, null, s.Token, Ctx(Editor)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Review.EditAsync(item.EntityId, new EntityEdit { SheetViewId = 999_999 }, item.Revision, null, s.Token, Ctx(Editor)));
        Assert.Equal(0, await RevisionsAsync(item.EntityId));
    }

    [Fact]
    public async Task An_item_can_be_placed_on_a_view_of_its_job_only_and_a_view_with_items_cannot_be_deleted()
    {
        var s = await JobWithItemsAsync();
        var other = await JobWithItemsAsync(1);
        var item = s.Items[0];
        var (sheetsA, viewA) = await ViewAsync(s);
        var (_, viewB) = await ViewAsync(other);

        await Assert.ThrowsAsync<JobRuleViolationException>(() => Review.EditAsync(item.EntityId, new EntityEdit { SheetViewId = viewB }, item.Revision, null, s.Token, Ctx(Editor)));
        var placed = await Review.EditAsync(item.EntityId, new EntityEdit { SheetViewId = viewA }, item.Revision, null, s.Token, Ctx(Editor));
        Assert.Equal(viewA, placed.SheetViewId);

        var refused = await Assert.ThrowsAsync<JobRuleViolationException>(() => sheetsA.DeleteViewAsync(viewA, s.Token, Ctx(Editor)));
        Assert.Contains("placed on it", refused.Message);

        var cleared = await Review.EditAsync(item.EntityId, new EntityEdit { ClearSheetView = true }, placed.Revision, null, s.Token, Ctx(Editor));
        Assert.Null(cleared.SheetViewId);
        Assert.True(await sheetsA.DeleteViewAsync(viewA, s.Token, Ctx(Editor)));
    }

    private async Task<(SheetRepository Sheets, long ViewId)> ViewAsync(Setup s)
    {
        var data = new byte[300];
        Array.Fill(data, (byte)Random.Shared.Next(1, 250));
        Encoding.ASCII.GetBytes("%PDF-1.7\n").CopyTo(data, 0);
        var uploads = new UploadService(Open, new FileSystemArtifactStore(_root), new UploadSettings { ChunkBytes = 1000 });
        var session = await uploads.BeginAsync(s.Job.JobId, new BeginUpload("v.pdf", data.Length, null), s.Token, Ctx(Editor));
        await uploads.AppendAsync(session.PublicId, 0, new MemoryStream(data), null, Ctx(Editor));
        var file = await uploads.CompleteAsync(session.PublicId, s.Token, Ctx(Editor));

        var sheets = new SheetRepository(Open);
        var sheet = (await sheets.RegisterAsync(s.Job.JobId, file.PublicId, [new NewSheet { PageNo = 1, WidthPts = 2592, HeightPts = 1728 }], s.Token, Ctx(Editor)))[0];
        var view = await sheets.CreateViewAsync(sheet.SheetId, new NewView { Title = "Plan" }, s.Token, Ctx(Editor));
        return (sheets, view.SheetViewId);
    }

    // ---- adding by hand

    [Fact]
    public async Task A_person_adds_an_item_the_detector_missed_with_provenance_and_nothing_to_verify()
    {
        var s = await JobWithItemsAsync(1);

        var added = await Review.AddAsync(s.Job.JobId, new NewManualEntity { Type = "Damper", Size = "12x12", Tag = "VD-1", Quantity = 3, Evidence = "Seen on M2.1" }, s.Token, Ctx(Editor));

        Assert.Equal((ReviewStatus.Manual, ResolutionSource.UserAdded, DetectionMethod.Manual, (DetectorFamily?)null, 1.0), (added.ReviewStatus, added.ResolutionSource, added.Method, added.Family, added.Confidence));
        Assert.True(added.ProducerId > 0 && added.ProcessingTaskId > 0);
        Assert.Equal((ProducerKind.Manual, Editor), (await ProducerKindAsync(added.ProducerId), await db.ScalarAsync<string>("SELECT UserEmail FROM ductwork.Producer WHERE ProducerId = @p;", ("@p", added.ProducerId))));
        Assert.Equal(1, await RevisionsAsync(added.EntityId));
        Assert.Equal(1, await EventsAsync(s.Job.JobId, "entity.added"));

        var second = await Review.AddAsync(s.Job.JobId, new NewManualEntity { Type = "Damper", Size = "10x10" }, s.Token, Ctx(Editor));
        Assert.Equal(added.ProcessingTaskId, second.ProcessingTaskId);                      // one manual task per person per job
        var verify = await Assert.ThrowsAsync<JobRuleViolationException>(() => Review.SetReviewStatusAsync(added.EntityId, ReviewStatus.Verified, added.Revision, null, s.Token, Ctx(Editor)));
        Assert.Contains("nothing to verify", verify.Message);
    }

    private async Task<ProducerKind> ProducerKindAsync(long producerId) =>
        Enum.Parse<ProducerKind>(await db.ScalarAsync<string>("SELECT Kind FROM ductwork.Producer WHERE ProducerId = @p;", ("@p", producerId)) ?? "");

    [Fact]
    public async Task A_hand_added_item_can_be_rejected_and_brought_back_as_manual()
    {
        var s = await JobWithItemsAsync(1);
        var added = await Review.AddAsync(s.Job.JobId, new NewManualEntity { Type = "Damper" }, s.Token, Ctx(Editor));

        var rejected = await Review.SetReviewStatusAsync(added.EntityId, ReviewStatus.Rejected, added.Revision, "Duplicate", s.Token, Ctx(Editor));
        var back = await Review.SetReviewStatusAsync(added.EntityId, ReviewStatus.Proposed, rejected.Revision, null, s.Token, Ctx(Editor));

        Assert.Equal((ReviewStatus.Rejected, ReviewStatus.Manual), (rejected.ReviewStatus, back.ReviewStatus));
    }

    [Fact]
    public async Task Adding_needs_a_person_a_type_and_a_reason_for_leaving_it_out()
    {
        var s = await JobWithItemsAsync(1);

        await Assert.ThrowsAsync<JobRuleViolationException>(() => Review.AddAsync(s.Job.JobId, new NewManualEntity { Type = "x" }, s.Token, AuditContext.ForWorker("w1")));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Review.AddAsync(s.Job.JobId, new NewManualEntity { Type = " " }, s.Token, Ctx(Editor)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Review.AddAsync(s.Job.JobId, new NewManualEntity { Type = "x", Quantity = -2 }, s.Token, Ctx(Editor)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Review.AddAsync(s.Job.JobId, new NewManualEntity { Type = "x", Disposition = Disposition.Excluded }, s.Token, Ctx(Editor)));
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => Review.AddAsync(s.Job.JobId, new NewManualEntity { Type = "x" }, s.Token, Ctx(Viewer)));
        Assert.Single(await Entities.ListByJobAsync(s.Job.JobId));
    }

    // ---- undo

    [Fact]
    public async Task Undo_restores_what_the_item_was_and_is_itself_a_revision_that_can_be_undone()
    {
        var s = await JobWithItemsAsync();
        var item = s.Items[0];
        var edited = await Review.EditAsync(item.EntityId, new EntityEdit { Size = "8x8x8", Quantity = 5 }, item.Revision, null, s.Token, Ctx(Editor));
        Assert.Equal(ResolutionSource.Override, edited.ResolutionSource);

        var undone = await Review.UndoAsync(item.EntityId, s.Token, Ctx(Editor));
        Assert.Equal((item.Size, item.Quantity, ResolutionSource.Detector, 3), (undone.Size, undone.Quantity, undone.ResolutionSource, undone.Revision));

        var redone = await Review.UndoAsync(item.EntityId, s.Token, Ctx(Editor));              // undoing the undo puts the edit back
        Assert.Equal(("8x8x8", 5m, ResolutionSource.Override, 4), (redone.Size, redone.Quantity, redone.ResolutionSource, redone.Revision));

        var history = await Review.ListRevisionsAsync(item.EntityId);
        Assert.Equal(["Edited", "Undone", "Undone"], history.Select(r => r.Action));
        Assert.Equal([null, 2, 3], history.Select(r => r.UndoOfRevisionNo));
    }

    [Fact]
    public async Task There_is_nothing_to_undo_on_an_untouched_or_just_added_item()
    {
        var s = await JobWithItemsAsync(1);
        var added = await Review.AddAsync(s.Job.JobId, new NewManualEntity { Type = "Damper" }, s.Token, Ctx(Editor));

        await Assert.ThrowsAsync<JobRuleViolationException>(() => Review.UndoAsync(s.Items[0].EntityId, s.Token, Ctx(Editor)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Review.UndoAsync(added.EntityId, s.Token, Ctx(Editor)));
    }

    [Fact]
    public async Task Undo_last_takes_back_the_callers_own_latest_change_that_is_still_the_latest_on_its_item()
    {
        var s = await JobWithItemsAsync();
        var a = s.Items[0];
        var b = s.Items[1];
        await Review.SetReviewStatusAsync(a.EntityId, ReviewStatus.Verified, a.Revision, null, s.Token, Ctx(Editor));
        await Review.SetReviewStatusAsync(b.EntityId, ReviewStatus.Rejected, b.Revision, null, s.Token, Ctx(Editor));

        var first = await Review.UndoLastAsync(s.Job.JobId, s.Token, Ctx(Editor));
        Assert.Equal((b.EntityId, ReviewStatus.Proposed), (first.EntityId, first.ReviewStatus));

        var second = await Review.UndoLastAsync(s.Job.JobId, s.Token, Ctx(Editor));         // the undo of b is now the latest, so it is undone again
        Assert.Equal((b.EntityId, ReviewStatus.Rejected), (second.EntityId, second.ReviewStatus));
        Assert.Equal(ReviewStatus.Verified, (await Review.GetAsync(a.EntityId))!.ReviewStatus);
    }

    [Fact]
    public async Task Undo_last_never_touches_another_persons_work_and_says_when_there_is_nothing()
    {
        var s = await JobWithItemsAsync();
        await Review.SetReviewStatusAsync(s.Items[0].EntityId, ReviewStatus.Verified, 1, null, s.Token, Ctx(Editor));

        // A different person holding the lock later has nothing of their own to undo.
        await new EditLockService(db.ConnectionString).ReleaseAsync(s.Token, Ctx(Editor));
        var token2 = (await new EditLockService(db.ConnectionString).AcquireAsync(s.Job.JobId, Ctx(Second))).Token!;

        await Assert.ThrowsAsync<JobRuleViolationException>(() => Review.UndoLastAsync(s.Job.JobId, token2, Ctx(Second)));
        Assert.Equal(ReviewStatus.Verified, (await Review.GetAsync(s.Items[0].EntityId))!.ReviewStatus);
    }

    [Fact]
    public async Task Undo_is_refused_when_what_it_would_restore_no_longer_exists()
    {
        var s = await JobWithItemsAsync();
        var item = s.Items[0];
        var types = new BuildingTypeRepository(db.ConnectionString);
        var temporary = await types.CreateAsync(s.Job.JobId, new NewBuildingType { Name = "Temporary" }, s.Token, Ctx(Editor));
        var placed = await Review.EditAsync(item.EntityId, new EntityEdit { BuildingTypeId = temporary.BuildingTypeId }, item.Revision, null, s.Token, Ctx(Editor));
        var moved = await Review.EditAsync(item.EntityId, new EntityEdit { ClearBuildingType = true }, placed.Revision, null, s.Token, Ctx(Editor));
        Assert.Null(moved.BuildingTypeId);
        await types.DeleteAsync(temporary.BuildingTypeId, s.Token, Ctx(Editor));

        var refused = await Assert.ThrowsAsync<JobRuleViolationException>(() => Review.UndoAsync(item.EntityId, s.Token, Ctx(Editor)));
        Assert.Contains("no longer exists", refused.Message);
    }

    // ---- the record itself

    [Fact]
    public async Task The_history_cannot_be_changed_or_deleted_even_with_direct_database_access()
    {
        var s = await JobWithItemsAsync();
        await Review.SetReviewStatusAsync(s.Items[0].EntityId, ReviewStatus.Verified, 1, null, s.Token, Ctx(Editor));

        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync("UPDATE ductwork.EntityRevision SET ChangedBy = 'someone else' WHERE EntityId = @e;", ("@e", s.Items[0].EntityId)));
        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync("DELETE FROM ductwork.EntityRevision WHERE EntityId = @e;", ("@e", s.Items[0].EntityId)));
    }

    [Fact]
    public async Task A_rejected_item_leaves_the_count_sheet_and_an_excluded_one_is_listed_as_not_counted()
    {
        var s = await JobWithItemsAsync(2);
        await Review.SetReviewStatusAsync(s.Items[0].EntityId, ReviewStatus.Rejected, 1, null, s.Token, Ctx(Editor));
        await Review.SetDispositionAsync(s.Items[1].EntityId, Disposition.Excluded, "Out of scope", 1, s.Token, Ctx(Editor));

        var sheet = await new CountSheetRepository(Open).BuildAsync(s.Job.JobId);

        Assert.Equal(1, sheet.Blocks.SelectMany(b => b.Lines).Count());
        Assert.All(sheet.Blocks.SelectMany(b => b.Lines), l => Assert.Equal(Disposition.Excluded, l.Disposition));
    }
}
