using AIToolbox.Web.Controllers.Ductwork;
using AIToolbox.Web.Ductwork;
using static AIToolbox.Web.Tests.Ductwork.ApiHarness;

namespace AIToolbox.Web.Tests.Ductwork;

public class UserApiTests(DuctworkDatabase db) : IClassFixture<DuctworkDatabase>
{
    private readonly ApiHarness _h = new(db);

    // ---- creating, listing, reading

    [Fact]
    public async Task Creating_a_job_makes_the_signed_in_person_its_owner()
    {
        var reply = await RunAsync(() => _h.JobsApi(Owner).Create(new CreateJobRequest("  Greenfield ", "Acme", null, "1 Main St", "Normal"), default));

        Assert.Equal(201, reply.Status);
        Assert.Equal(("Greenfield", Owner, "Draft", "Normal"), (reply["name"].GetString(), reply["ownerEmail"].GetString(), reply["state"].GetString(), reply["reviewLevel"].GetString()));
    }

    [Fact]
    public async Task A_bad_value_is_a_400_that_lists_the_valid_ones()
    {
        var reply = await RunAsync(() => _h.JobsApi(Owner).Create(new CreateJobRequest("x", null, null, null, "Reckless"), default));

        Assert.Equal((400, "bad-request"), (reply.Status, reply.Code));
        Assert.Contains("Sensitive, Normal, Insensitive", reply["error"].GetString());
    }

    [Fact]
    public async Task A_request_without_a_tab_id_is_refused_because_the_session_is_derived_from_it()
    {
        var jobs = _h.JobsApi(Owner);
        jobs.HttpContext.Request.Headers.Remove("X-Ductwork-Tab");

        var reply = await RunAsync(() => jobs.Create(new CreateJobRequest("x", null, null, null, null), default));

        Assert.Equal(400, reply.Status);
        Assert.Contains("X-Ductwork-Tab", reply["error"].GetString());
    }

    [Fact]
    public async Task The_list_only_ever_contains_jobs_the_caller_belongs_to()
    {
        var mine = await _h.NewJobAsync("mine " + Guid.NewGuid().ToString("N")[..6]);
        var theirs = (await RunAsync(() => _h.JobsApi("someone.else@example.com").Create(new CreateJobRequest("theirs", null, null, null, null), default)))["jobId"].GetInt64();

        var asEditor = await RunAsync(() => _h.JobsApi(Editor).List(null, null, null, null, default));
        var ids = asEditor.Body.EnumerateArray().Select(j => j.GetProperty("jobId").GetInt64()).ToList();

        Assert.Contains(mine, ids);
        Assert.DoesNotContain(theirs, ids);
    }

    [Fact]
    public async Task The_list_filters_by_state_and_tag()
    {
        var id = await _h.NewJobAsync();
        await RunAsync(() => _h.JobsApi(Editor).AddTag(id, new TagRequest("Orion-" + id), default));

        var tagged = await RunAsync(() => _h.JobsApi(Editor).List(["Draft"], ["Orion-" + id], null, null, default));
        Assert.Equal([id], tagged.Body.EnumerateArray().Select(j => j.GetProperty("jobId").GetInt64()));

        var closed = await RunAsync(() => _h.JobsApi(Editor).List(["Closed"], ["Orion-" + id], null, null, default));
        Assert.Empty(closed.Body.EnumerateArray());
        Assert.Equal(400, (await RunAsync(() => _h.JobsApi(Editor).List(["Nonsense"], null, null, null, default))).Status);
    }

    [Fact]
    public async Task A_member_sees_the_job_with_their_role_and_a_stranger_is_told_it_does_not_exist()
    {
        var id = await _h.NewJobAsync();

        var editor = await RunAsync(() => _h.JobsApi(Editor).Get(id, default));
        Assert.Equal((200, "Editor", true), (editor.Status, editor["myRole"].GetString(), editor["canEdit"].GetBoolean()));
        Assert.Equal(3, editor["members"].GetArrayLength());
        Assert.Equal(4, editor["steps"].EnumerateObject().Count());

        var viewer = await RunAsync(() => _h.JobsApi(Viewer).Get(id, default));
        Assert.False(viewer["canEdit"].GetBoolean());

        var stranger = await RunAsync(() => _h.JobsApi(Stranger).Get(id, default));
        Assert.Equal((404, "not-found"), (stranger.Status, stranger.Code));
        var missing = await RunAsync(() => _h.JobsApi(Stranger).Get(long.MaxValue, default));
        Assert.Equal(404, missing.Status);                      // the same answer, so ids reveal nothing
        Assert.Equal(404, (await RunAsync(() => _h.JobsApi(Admin, admin: true).Get(id, default))).Status);   // an administrator sees nothing they are not a member of
    }

    // ---- editing needs the lock and its fencing token

    [Fact]
    public async Task An_edit_with_the_token_from_taking_the_lock_succeeds_and_one_without_it_is_a_400()
    {
        var id = await _h.NewJobAsync();
        var (tab, token) = await _h.EditorLocksAsync(id);
        var version = (await RunAsync(() => _h.JobsApi(Editor).Get(id, default)))["job"].GetProperty("stateVersion").GetInt64();

        var ok = await RunAsync(() => _h.JobsApi(Editor, tab, token).UpdateDetails(id, new UpdateDetailsRequest("Renamed", null, "Phase 2", null, null, version), default));
        Assert.Equal((200, "Renamed", "Phase 2"), (ok.Status, ok["name"].GetString(), ok["projectName"].GetString()));

        var missingHeader = await RunAsync(() => _h.JobsApi(Editor, tab).UpdateDetails(id, new UpdateDetailsRequest("x", null, null, null, null, ok["stateVersion"].GetInt64()), default));
        Assert.Equal(400, missingHeader.Status);
        Assert.Contains("fencing token", missingHeader["error"].GetString());
    }

    [Fact]
    public async Task A_wrong_or_old_token_or_a_different_tab_is_a_409_lock_lost_and_changes_nothing()
    {
        var id = await _h.NewJobAsync();
        var (tab, token) = await _h.EditorLocksAsync(id);
        var job = await _h.JobRepo.GetAsync(id);
        var body = new UpdateDetailsRequest("hijack", null, null, null, null, job!.StateVersion);

        Assert.Equal("lock-lost", (await RunAsync(() => _h.JobsApi(Editor, tab, token + 1000).UpdateDetails(id, body, default))).Code);
        Assert.Equal("lock-lost", (await RunAsync(() => _h.JobsApi(Editor, ApiHarness.NewTab(), token).UpdateDetails(id, body, default))).Code);   // another tab of the same person
        Assert.Equal("lock-lost", (await RunAsync(() => _h.JobsApi(Owner, ApiHarness.NewTab(), token).UpdateDetails(id, body, default))).Code);       // another person, same token
        Assert.Equal(job.Name, (await _h.JobRepo.GetAsync(id))!.Name);
    }

    [Fact]
    public async Task Editing_from_a_stale_version_is_a_409_version_conflict()
    {
        var id = await _h.NewJobAsync();
        var (tab, token) = await _h.EditorLocksAsync(id);
        var version = (await _h.JobRepo.GetAsync(id))!.StateVersion;
        await RunAsync(() => _h.JobsApi(Editor, tab, token).UpdateDetails(id, new UpdateDetailsRequest(null, null, "first", null, null, version), default));

        var stale = await RunAsync(() => _h.JobsApi(Editor, tab, token).UpdateDetails(id, new UpdateDetailsRequest(null, null, "second", null, null, version), default));

        Assert.Equal((409, "version-conflict"), (stale.Status, stale.Code));
    }

    [Fact]
    public async Task A_viewer_is_forbidden_and_a_blank_name_is_a_422()
    {
        var id = await _h.NewJobAsync();
        var (tab, token) = await _h.EditorLocksAsync(id);
        var version = (await _h.JobRepo.GetAsync(id))!.StateVersion;

        var viewer = await RunAsync(() => _h.JobsApi(Viewer, tab, token).UpdateDetails(id, new UpdateDetailsRequest("x", null, null, null, null, version), default));
        Assert.Equal((403, "forbidden"), (viewer.Status, viewer.Code));

        var blank = await RunAsync(() => _h.JobsApi(Editor, tab, token).UpdateDetails(id, new UpdateDetailsRequest(" ", null, null, null, null, version), default));
        Assert.Equal((422, "rule"), (blank.Status, blank.Code));
    }

    [Fact]
    public async Task The_review_level_and_workflow_steps_are_edits_and_the_baseline_is_metadata()
    {
        var id = await _h.NewJobAsync();
        var (tab, token) = await _h.EditorLocksAsync(id);

        var level = await RunAsync(() => _h.JobsApi(Editor, tab, token).SetReviewLevel(id, new SetReviewLevelRequest("Insensitive"), default));
        Assert.Equal("Insensitive", level["reviewLevel"].GetString());

        var steps = await RunAsync(() => _h.JobsApi(Editor, tab, token).SetStep(id, "Setup", new SetStepRequest("Done"), default));
        Assert.Equal("Done", steps["Setup"].GetString());
        Assert.Equal(400, (await RunAsync(() => _h.JobsApi(Editor, tab, token).SetStep(id, "Nonsense", new SetStepRequest("Done"), default))).Status);

        var baseline = await RunAsync(() => _h.JobsApi(Editor).SetBaseline(id, new SetBaselineRequest(6.5m), default));   // no token needed
        Assert.Equal(6.5m, baseline["baselineManualHours"].GetDecimal());
    }

    // ---- lifecycle

    [Fact]
    public async Task A_job_runs_through_completing_closing_and_reopening_with_reasons()
    {
        var id = await _h.NewJobAsync();
        var (tab, token) = await _h.EditorLocksAsync(id);
        foreach (var step in new[] { "Setup", "Process", "Review", "Report" })
            await RunAsync(() => _h.JobsApi(Editor, tab, token).SetStep(id, step, new SetStepRequest("Done"), default));

        var begin = await RunAsync(() => _h.JobsApi(Editor, tab).BeginComplete(id, default));
        Assert.Equal("Completing", begin["state"].GetString());
        var done = await RunAsync(() => _h.JobsApi(Editor, tab).FinishComplete(id, default));
        Assert.Equal(("Complete", 1), (done["state"].GetString(), done["completionVersion"].GetInt32()));

        var noReason = await RunAsync(() => _h.JobsApi(Editor, tab).Reopen(id, new ReasonRequest(" "), default));
        Assert.Equal((422, "rule"), (noReason.Status, noReason.Code));
        var reopened = await RunAsync(() => _h.JobsApi(Editor, tab).Reopen(id, new ReasonRequest("Addendum received"), default));
        Assert.Equal("Draft", reopened["state"].GetString());

        var closed = await RunAsync(() => _h.JobsApi(Editor, tab).Close(id, new ReasonRequest("Lost the bid"), default));
        Assert.Equal(("Closed", "Lost the bid"), (closed["state"].GetString(), closed["closeReason"].GetString()));
    }

    [Fact]
    public async Task Completing_with_unfinished_steps_is_a_422_that_says_what_to_do()
    {
        var id = await _h.NewJobAsync();
        var reply = await RunAsync(() => _h.JobsApi(Editor).BeginComplete(id, default));

        Assert.Equal((422, "rule"), (reply.Status, reply.Code));
        Assert.Contains("close it instead", reply["error"].GetString());
    }

    [Fact]
    public async Task Completing_while_someone_else_is_editing_is_a_409_locked()
    {
        var id = await _h.NewJobAsync();
        await _h.EditorLocksAsync(id, Owner);

        var reply = await RunAsync(() => _h.JobsApi(Editor).Close(id, new ReasonRequest("x"), default));

        Assert.Equal((409, "locked"), (reply.Status, reply.Code));
    }

    // ---- members and tags

    [Fact]
    public async Task Owners_manage_members_and_nobody_else_can_and_a_job_keeps_an_owner()
    {
        var id = await _h.NewJobAsync();

        var added = await RunAsync(() => _h.JobsApi(Owner).AddMember(id, new AddMemberRequest("New.Person@Example.com", "Editor"), default));
        Assert.Equal(201, added.Status);
        Assert.Contains(added.Body.EnumerateArray(), m => m.GetProperty("userEmail").GetString() == "new.person@example.com");

        Assert.Equal(403, (await RunAsync(() => _h.JobsApi(Editor).AddMember(id, new AddMemberRequest("x@example.com", "Viewer"), default))).Status);
        Assert.Equal(400, (await RunAsync(() => _h.JobsApi(Owner).AddMember(id, new AddMemberRequest("x@example.com", "Admin"), default))).Status);
        Assert.Equal(422, (await RunAsync(() => _h.JobsApi(Owner).RemoveMember(id, Owner, default))).Status);

        var promoted = await RunAsync(() => _h.JobsApi(Owner).ChangeMemberRole(id, Editor, new ChangeRoleRequest("Owner"), default));
        Assert.Equal(200, promoted.Status);
        Assert.Equal(200, (await RunAsync(() => _h.JobsApi(Owner).RemoveMember(id, Owner, default))).Status);
    }

    [Fact]
    public async Task Tags_are_added_and_removed_by_editors_even_on_a_finished_job()
    {
        var id = await _h.NewJobAsync();
        await RunAsync(() => _h.JobsApi(Editor).Close(id, new ReasonRequest("done"), default));

        var added = await RunAsync(() => _h.JobsApi(Editor).AddTag(id, new TagRequest("  Won  "), default));
        Assert.Equal(["Won"], added.Body.EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(403, (await RunAsync(() => _h.JobsApi(Viewer).AddTag(id, new TagRequest("x"), default))).Status);

        var removed = await RunAsync(() => _h.JobsApi(Editor).RemoveTag(id, "won", default));
        Assert.Empty(removed.Body.EnumerateArray());
    }

    // ---- audit and time on job

    [Fact]
    public async Task The_audit_log_is_readable_by_members_filterable_and_verifiable_and_not_by_strangers()
    {
        var id = await _h.NewJobAsync();
        await RunAsync(() => _h.JobsApi(Editor).AddTag(id, new TagRequest("Searchable"), default));

        var all = await RunAsync(() => _h.JobsApi(Viewer).ListAudit(id, null, null, null, null, null, default));
        Assert.True(all.Body.GetArrayLength() >= 3);

        var tags = await RunAsync(() => _h.JobsApi(Viewer).ListAudit(id, "tags", null, null, null, null, default));
        Assert.All(tags.Body.EnumerateArray(), e => Assert.Equal("tags", e.GetProperty("category").GetString()));

        var verified = await RunAsync(() => _h.JobsApi(Viewer).VerifyAudit(id, default));
        Assert.True(verified["isValid"].GetBoolean());

        Assert.Equal(404, (await RunAsync(() => _h.JobsApi(Stranger).ListAudit(id, null, null, null, null, null, default))).Status);
        Assert.Equal(404, (await RunAsync(() => _h.JobsApi(Stranger).VerifyAudit(id, default))).Status);
    }

    [Fact]
    public async Task The_audit_records_the_real_person_session_address_and_browser_not_anything_from_the_body()
    {
        var id = await _h.NewJobAsync();
        var tab = ApiHarness.NewTab();
        await RunAsync(() => _h.JobsApi(Editor, tab).AddTag(id, new TagRequest("who"), default));

        var entry = (await _h.JobRepo.ListAuditAsync(id)).Last(e => e.Action == "tag.added");

        Assert.Equal((Editor, "10.1.2.3", "tests"), (entry.ActorEmail, entry.ClientIp, entry.UserAgent));
        Assert.Equal(DuctworkAuditContexts.SessionFor(Editor, tab), entry.ActorSessionId);
    }

    [Fact]
    public async Task Time_on_job_is_measured_by_the_server_and_the_per_person_breakdown_is_for_owners()
    {
        var id = await _h.NewJobAsync();
        var tab = ApiHarness.NewTab();
        await RunAsync(() => _h.JobsApi(Editor, tab).ActivityHeartbeat(id, new ActivityHeartbeatRequest("Active", "Review", "viewer"), default));
        await Task.Delay(50);
        var beat = await RunAsync(() => _h.JobsApi(Editor, tab).ActivityHeartbeat(id, new ActivityHeartbeatRequest("Active", null, null), default));
        Assert.Equal(2, beat["heartbeats"].GetInt32());

        var asOwner = await RunAsync(() => _h.JobsApi(Owner).ActivitySummary(id, default));
        var asEditor = await RunAsync(() => _h.JobsApi(Editor).ActivitySummary(id, default));
        Assert.Equal(1, asOwner["byUser"].GetArrayLength());
        Assert.Equal(0, asEditor["byUser"].GetArrayLength());
        Assert.Equal(asOwner["sessions"].GetInt32(), asEditor["sessions"].GetInt32());

        Assert.Equal(400, (await RunAsync(() => _h.JobsApi(Editor, tab).ActivityHeartbeat(id, new ActivityHeartbeatRequest("Napping", null, null), default))).Status);
        Assert.Equal(403, (await RunAsync(() => _h.JobsApi(Stranger, tab).ActivityHeartbeat(id, new ActivityHeartbeatRequest("Active", null, null), default))).Status);
        Assert.True((await RunAsync(() => _h.JobsApi(Editor, tab).EndActivity(id, default)))["ended"].GetBoolean());
    }

    // ---- building types

    [Fact]
    public async Task Building_types_are_created_by_the_lock_holder_and_a_multiplier_entered_by_a_person_is_confirmed()
    {
        var id = await _h.NewJobAsync();
        var (tab, token) = await _h.EditorLocksAsync(id);

        var created = await RunAsync(() => _h.TypesApi(Editor, tab, token).Create(id, new CreateBuildingTypeRequest("Type 4", "UnitType", "Two-bedroom"), default));
        Assert.Equal((201, "Type 4", "UnitType"), (created.Status, created["name"].GetString(), created["kind"].GetString()));
        Assert.Equal(JsonValueKindNull, created["multiplier"].ValueKind);
        var typeId = created["buildingTypeId"].GetInt64();

        var set = await RunAsync(() => _h.TypesApi(Editor, tab, token).SetMultiplier(id, typeId, new SetMultiplierRequest(26, "ArchitecturalList", "Sheet T1.1 rows A to Q"), default));
        Assert.Equal((26, true, "ArchitecturalList"), (set["multiplier"].GetInt32(), set["confirmed"].GetBoolean(), set["multiplierSource"].GetString()));

        Assert.Equal(1, (await RunAsync(() => _h.TypesApi(Viewer, ApiHarness.NewTab()).List(id, default))).Body.GetArrayLength());   // a viewer may read
        Assert.Equal(403, (await RunAsync(() => _h.TypesApi(Viewer, tab, token).Create(id, new CreateBuildingTypeRequest("x", null, null), default))).Status);
    }

    [Fact]
    public async Task A_multiplier_needs_a_number_a_real_source_and_evidence_where_required()
    {
        var id = await _h.NewJobAsync();
        var (tab, token) = await _h.EditorLocksAsync(id);
        var typeId = (await RunAsync(() => _h.TypesApi(Editor, tab, token).Create(id, new CreateBuildingTypeRequest("T", null, null), default)))["buildingTypeId"].GetInt64();

        Assert.Equal(400, (await RunAsync(() => _h.TypesApi(Editor, tab, token).SetMultiplier(id, typeId, new SetMultiplierRequest(null, "Manual", null), default))).Status);
        Assert.Equal(400, (await RunAsync(() => _h.TypesApi(Editor, tab, token).SetMultiplier(id, typeId, new SetMultiplierRequest(3, "Telepathy", null), default))).Status);
        Assert.Equal(422, (await RunAsync(() => _h.TypesApi(Editor, tab, token).SetMultiplier(id, typeId, new SetMultiplierRequest(-1, "Manual", null), default))).Status);
        Assert.Equal(422, (await RunAsync(() => _h.TypesApi(Editor, tab, token).SetMultiplier(id, typeId, new SetMultiplierRequest(3, "Suggested", "x"), default))).Status);
        Assert.Equal(422, (await RunAsync(() => _h.TypesApi(Editor, tab, token).SetMultiplier(id, typeId, new SetMultiplierRequest(3, "ArchitecturalList", " "), default))).Status);
        var zero = await RunAsync(() => _h.TypesApi(Editor, tab, token).SetMultiplier(id, typeId, new SetMultiplierRequest(0, null, null), default));
        Assert.Equal((0, true), (zero["multiplier"].GetInt32(), zero["confirmed"].GetBoolean()));    // source defaults to the person's own count

        var cleared = await RunAsync(() => _h.TypesApi(Editor, tab, token).ClearMultiplier(id, typeId, default));
        Assert.False(cleared["confirmed"].GetBoolean());
    }

    [Fact]
    public async Task A_building_type_of_another_job_is_not_reachable_through_this_one()
    {
        var a = await _h.NewJobAsync();
        var b = await _h.NewJobAsync();
        var (tabA, tokenA) = await _h.EditorLocksAsync(a);
        var (tabB, tokenB) = await _h.EditorLocksAsync(b);
        var typeInB = (await RunAsync(() => _h.TypesApi(Editor, tabB, tokenB).Create(b, new CreateBuildingTypeRequest("B only", null, null), default)))["buildingTypeId"].GetInt64();

        var reply = await RunAsync(() => _h.TypesApi(Editor, tabA, tokenA).SetMultiplier(a, typeInB, new SetMultiplierRequest(5, "Manual", null), default));

        Assert.Equal((404, "not-found"), (reply.Status, reply.Code));
    }

    [Fact]
    public async Task An_empty_building_type_is_deleted_and_the_count_sheet_and_entities_are_readable()
    {
        var id = await _h.NewJobAsync();
        var (tab, token) = await _h.EditorLocksAsync(id);
        var typeId = (await RunAsync(() => _h.TypesApi(Editor, tab, token).Create(id, new CreateBuildingTypeRequest("Gone", null, null), default)))["buildingTypeId"].GetInt64();

        Assert.True((await RunAsync(() => _h.TypesApi(Editor, tab, token).Delete(id, typeId, default)))["deleted"].GetBoolean());

        var sheet = await RunAsync(() => _h.JobsApi(Viewer).CountSheet(id, default));
        Assert.Equal(0, sheet["blocks"].GetArrayLength());
        Assert.True(sheet["isFinal"].GetBoolean());
        Assert.Equal(0, (await RunAsync(() => _h.JobsApi(Viewer).ListEntities(id, null, null, null, null, null, default))).Body.GetArrayLength());
        Assert.Equal(400, (await RunAsync(() => _h.JobsApi(Viewer).ListEntities(id, null, "Maybe", null, null, null, default))).Status);
        Assert.Equal(404, (await RunAsync(() => _h.JobsApi(Stranger).CountSheet(id, default))).Status);
    }

    // ---- tasks

    [Fact]
    public async Task Queuing_a_type_with_no_evaluated_binding_is_a_422_and_cancelling_a_task_of_another_job_is_a_404()
    {
        var id = await _h.NewJobAsync();
        var other = await _h.NewJobAsync();
        var (tab, token) = await _h.EditorLocksAsync(id);

        var refused = await RunAsync(() => _h.JobsApi(Editor, tab, token).EnqueueTask(id, new EnqueueTaskRequest("not-bound", null, null), default));
        Assert.Equal((422, "rule"), (refused.Status, refused.Code));
        Assert.Contains("no active, evaluated binding", refused["error"].GetString());

        var cancel = await RunAsync(() => _h.JobsApi(Editor, tab).CancelTask(other, 999_999, default));
        Assert.Equal(404, cancel.Status);
    }

    private const System.Text.Json.JsonValueKind JsonValueKindNull = System.Text.Json.JsonValueKind.Null;
}
