using System.Text.Json;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Web.Controllers.Ductwork;
using static AIToolbox.Web.Tests.Ductwork.ApiHarness;

namespace AIToolbox.Web.Tests.Ductwork;

public class ReviewApiTests(DuctworkDatabase db) : IClassFixture<DuctworkDatabase>
{
    private readonly ApiHarness _h = new(db);

    private static DetectionProposal Detection(string id, string size) => new()
    {
        DetectionId = id, Type = "Boot", Size = size, BuildingType = "Type 4", Quantity = 1, Confidence = 0.95,
        Method = DetectionMethod.Text, Family = DetectorFamily.LabelFlagCount, Box = new BoundingBox(1, 2, 3, 4), SheetViewRef = "M1.2", Evidence = "label"
    };

    /// <summary>A job where a worker has reported items through the real API, with the editor holding the lock.</summary>
    private async Task<(long Job, string Tab, long Token, List<Guid> Items)> JobWithItemsAsync(int count = 3)
    {
        var producerRef = ProducerRef.Algorithm("review-" + Guid.NewGuid().ToString("N")[..6], "1.0", "h");
        var producer = await _h.ProducerAsync(producerRef);
        var type = "detect-" + Guid.NewGuid().ToString("N")[..8];
        await _h.BindingRepo.BindAsync(type, producer, await _h.EvalRunAsync(producer), false, 3, AIToolbox.Ductwork.Core.Jobs.AuditContext.ForUser(Admin, sessionId: "setup", isAdmin: true));

        var job = await _h.NewJobAsync();
        var (tab, token) = await _h.EditorLocksAsync(job);
        await RunAsync(() => _h.JobsApi(Editor, tab, token).EnqueueTask(job, new EnqueueTaskRequest(type, null, null), default));
        var claimed = await RunAsync(() => _h.WorkerApi().Claim(new WorkerClaimRequest("w1", [type]), default));

        var manifest = JsonSerializer.Serialize(new TaskResultManifest
        {
            ContractVersion = ContractInfo.Version, TaskType = type, TaskPublicId = claimed["taskPublicId"].GetString()!, Producer = producerRef,
            Detections = Enumerable.Range(1, count).Select(i => Detection("d" + i, $"4x1{i}x7")).ToList(), Findings = []
        }, ContractJson.Options);
        var posted = await RunAsync(() => _h.WorkerApi(body: new MemoryStream(System.Text.Encoding.UTF8.GetBytes(manifest))).Result(claimed["taskId"].GetInt64(), "w1", null, default));
        Assert.True(posted.Ok);

        var list = await RunAsync(() => _h.JobsApi(Viewer).ListEntities(job, null, null, null, null, null, default));
        return (job, tab, token, list.Body.EnumerateArray().Select(e => e.GetProperty("publicId").GetGuid()).ToList());
    }

    private static int Revision(Reply reply) => reply["revision"].GetInt32();

    [Fact]
    public async Task An_item_is_verified_rejected_and_its_history_is_readable_by_members()
    {
        var (job, tab, token, items) = await JobWithItemsAsync();
        var api = () => _h.ReviewApi(Editor, tab, token);

        var before = await RunAsync(() => api().Get(job, items[0], default));
        Assert.Equal((1, "Proposed"), (Revision(before), before["reviewStatus"].GetString()));

        var verified = await RunAsync(() => api().SetReview(job, items[0], new SetReviewRequest("Verified", 1, null), default));
        Assert.Equal((2, "Verified"), (Revision(verified), verified["reviewStatus"].GetString()));
        var rejected = await RunAsync(() => api().SetReview(job, items[0], new SetReviewRequest("rejected", 2, "Not a boot"), default));
        Assert.Equal("Rejected", rejected["reviewStatus"].GetString());

        var history = await RunAsync(() => _h.ReviewApi(Viewer, NewTab()).History(job, items[0], default));
        Assert.Equal(["Reviewed", "Reviewed"], history.Body.EnumerateArray().Select(r => r.GetProperty("action").GetString()));
        Assert.Equal(404, (await RunAsync(() => _h.ReviewApi(Stranger, NewTab()).History(job, items[0], default))).Status);
    }

    [Fact]
    public async Task A_change_made_against_an_old_revision_is_a_409_that_says_what_it_is_now()
    {
        var (job, tab, token, items) = await JobWithItemsAsync();
        var api = () => _h.ReviewApi(Editor, tab, token);
        await RunAsync(() => api().SetReview(job, items[0], new SetReviewRequest("Verified", 1, null), default));

        var stale = await RunAsync(() => api().SetReview(job, items[0], new SetReviewRequest("Rejected", 1, null), default));

        Assert.Equal((409, "version-conflict", 2), (stale.Status, stale.Code, stale["actualRevision"].GetInt32()));
    }

    [Fact]
    public async Task Changes_need_the_fencing_token_a_viewer_is_refused_and_values_are_validated()
    {
        var (job, tab, token, items) = await JobWithItemsAsync();

        Assert.Equal(400, (await RunAsync(() => _h.ReviewApi(Editor, tab).SetReview(job, items[0], new SetReviewRequest("Verified", 1, null), default))).Status);
        Assert.Equal("lock-lost", (await RunAsync(() => _h.ReviewApi(Editor, tab, token + 77).SetReview(job, items[0], new SetReviewRequest("Verified", 1, null), default))).Code);
        Assert.False((await RunAsync(() => _h.ReviewApi(Viewer, NewTab(), token).SetReview(job, items[0], new SetReviewRequest("Verified", 1, null), default))).Ok);

        var bad = await RunAsync(() => _h.ReviewApi(Editor, tab, token).SetReview(job, items[0], new SetReviewRequest("Approved", 1, null), default));
        Assert.Equal((400, "bad-request"), (bad.Status, bad.Code));
        Assert.Contains("Proposed, Verified", bad["error"].GetString());
        Assert.Equal(404, (await RunAsync(() => _h.ReviewApi(Editor, tab, token).SetReview(job, Guid.NewGuid(), new SetReviewRequest("Verified", 1, null), default))).Status);
    }

    [Fact]
    public async Task A_batch_is_changed_together_and_an_item_of_another_job_fails_the_request()
    {
        var (job, tab, token, items) = await JobWithItemsAsync(4);
        var (_, _, _, foreign) = await JobWithItemsAsync(1);
        var api = () => _h.ReviewApi(Editor, tab, token);

        var done = await RunAsync(() => api().SetReviewMany(job, new ReviewManyRequest(items.ToArray(), "Verified", null), default));
        Assert.Equal((4, 4), (done["changed"].GetInt32(), done["requested"].GetInt32()));

        var mixed = await RunAsync(() => api().SetReviewMany(job, new ReviewManyRequest([items[0], foreign[0]], "Rejected", null), default));
        Assert.Equal(404, mixed.Status);
        var list = await RunAsync(() => _h.JobsApi(Viewer).ListEntities(job, null, "Rejected", null, null, null, default));
        Assert.Equal(0, list.Body.GetArrayLength());
    }

    [Fact]
    public async Task Disposition_needs_a_reason_to_leave_an_item_out_and_the_count_sheet_follows()
    {
        var (job, tab, token, items) = await JobWithItemsAsync(2);
        var api = () => _h.ReviewApi(Editor, tab, token);

        var noReason = await RunAsync(() => api().SetDisposition(job, items[0], new SetDispositionRequest("Excluded", 1, null), default));
        Assert.Equal((422, "rule"), (noReason.Status, noReason.Code));
        var excluded = await RunAsync(() => api().SetDisposition(job, items[0], new SetDispositionRequest("Excluded", 1, "Out of scope"), default));
        Assert.Equal(("Excluded", "Out of scope"), (excluded["disposition"].GetString(), excluded["dispositionReason"].GetString()));
        await RunAsync(() => api().SetReview(job, items[1], new SetReviewRequest("Rejected", 1, null), default));

        var sheet = await RunAsync(() => _h.JobsApi(Viewer).CountSheet(job, default));
        Assert.Equal(1, sheet["blocks"][0].GetProperty("lines").GetArrayLength());
    }

    [Fact]
    public async Task An_edit_changes_details_makes_an_override_and_can_be_undone()
    {
        var (job, tab, token, items) = await JobWithItemsAsync();
        var api = () => _h.ReviewApi(Editor, tab, token);

        var edited = await RunAsync(() => api().Edit(job, items[0], new EditEntityRequest(1, "Misread", null, null, "B-1", "6x12x7", null, null, null, 2, "EA", null, false, null, false), default));
        Assert.Equal(("6x12x7", "B-1", 2m, "Override"), (edited["size"].GetString(), edited["tag"].GetString(), edited["quantity"].GetDecimal(), edited["resolutionSource"].GetString()));

        var undone = await RunAsync(() => api().Undo(job, items[0], default));
        Assert.Equal(("Detector", 3), (undone["resolutionSource"].GetString(), Revision(undone)));
        Assert.NotEqual("6x12x7", undone["size"].GetString());

        var badUnit = await RunAsync(() => api().Edit(job, items[0], new EditEntityRequest(3, null, null, null, null, null, null, null, null, null, "Yards", null, false, null, false), default));
        Assert.Equal(400, badUnit.Status);
        var negative = await RunAsync(() => api().Edit(job, items[0], new EditEntityRequest(3, null, null, null, null, null, null, null, null, -1, null, null, false, null, false), default));
        Assert.Equal(422, negative.Status);
    }

    [Fact]
    public async Task A_person_adds_an_item_and_it_shows_with_its_provenance()
    {
        var (job, tab, token, _) = await JobWithItemsAsync(1);
        var api = () => _h.ReviewApi(Editor, tab, token);

        var added = await RunAsync(() => api().Add(job, new AddEntityRequest("Damper", null, "VD-1", "12x12", null, null, null, 2, "EA", null, null, null, null, [1, 2, 3, 4], "On M2.1"), default));

        Assert.Equal((201, "Manual", "UserAdded", 2m), (added.Status, added["reviewStatus"].GetString(), added["resolutionSource"].GetString(), added["quantity"].GetDecimal()));
        Assert.Equal(JsonValueKind.Null, added["family"].ValueKind);
        Assert.Equal(400, (await RunAsync(() => api().Add(job, new AddEntityRequest("Damper", null, null, null, null, null, null, null, null, null, null, null, null, [1, 2], null), default))).Status);
        Assert.Equal(422, (await RunAsync(() => api().Add(job, new AddEntityRequest(" ", null, null, null, null, null, null, null, null, null, null, null, null, null, null), default))).Status);
    }

    [Fact]
    public async Task Undo_last_takes_back_the_callers_latest_change_and_says_when_there_is_none()
    {
        var (job, tab, token, items) = await JobWithItemsAsync();
        var api = () => _h.ReviewApi(Editor, tab, token);

        var none = await RunAsync(() => api().UndoLast(job, default));
        Assert.Equal((422, "rule"), (none.Status, none.Code));

        await RunAsync(() => api().SetReview(job, items[0], new SetReviewRequest("Verified", 1, null), default));
        var undone = await RunAsync(() => api().UndoLast(job, default));
        Assert.Equal("Proposed", undone["reviewStatus"].GetString());
        Assert.Equal(404, (await RunAsync(() => _h.ReviewApi(Stranger, NewTab(), token).UndoLast(job, default))).Status);
    }

    [Fact]
    public async Task An_item_of_another_job_is_not_reachable_through_this_one()
    {
        var (job, tab, token, _) = await JobWithItemsAsync(1);
        var (_, _, _, foreign) = await JobWithItemsAsync(1);

        Assert.Equal(404, (await RunAsync(() => _h.ReviewApi(Editor, tab, token).Get(job, foreign[0], default))).Status);
        Assert.Equal(404, (await RunAsync(() => _h.ReviewApi(Editor, tab, token).SetReview(job, foreign[0], new SetReviewRequest("Verified", 1, null), default))).Status);
        Assert.Equal(404, (await RunAsync(() => _h.ReviewApi(Editor, tab, token).Undo(job, foreign[0], default))).Status);
    }
}
