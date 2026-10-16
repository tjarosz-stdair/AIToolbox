using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Counting;
using AIToolbox.Ductwork.Core.Data;
using AIToolbox.Ductwork.Core.Jobs;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Tests;

public class BuildingTypeTests(SqlFixture db) : IClassFixture<SqlFixture>
{
    private const string Owner = "owner@example.com";
    private const string Editor = "editor@example.com";
    private const string Viewer = "viewer@example.com";

    private JobRepository Jobs() => new(db.ConnectionString);
    private BuildingTypeRepository Types() => new(db.ConnectionString);
    private EditLockService Locks() => new(db.ConnectionString);

    private static AuditContext Ctx(string email) =>
        AuditContext.ForUser(email, displayName: email.Split('@')[0], sessionId: "s-" + email, clientIp: "10.0.0.4", userAgent: "tests");

    private async Task<(JobRecord Job, EditToken Token)> NewJobAsync()
    {
        var job = await Jobs().CreateAsync(new NewJob { Name = "Types job " + Guid.NewGuid().ToString("N")[..8] }, Ctx(Owner));
        await Jobs().AddMemberAsync(job.JobId, Editor, JobRole.Editor, Ctx(Owner));
        await Jobs().AddMemberAsync(job.JobId, Viewer, JobRole.Viewer, Ctx(Owner));
        return (job, (await Locks().AcquireAsync(job.JobId, Ctx(Editor))).Token!);
    }

    private async Task<IReadOnlyList<string>> ActionsAsync(long jobId) => (await Jobs().ListAuditAsync(jobId)).Select(e => e.Action).ToList();

    // ---- creating and editing

    [Fact]
    public async Task A_building_type_is_created_unconfirmed_with_no_multiplier_and_logged()
    {
        var (job, token) = await NewJobAsync();

        var type = await Types().CreateAsync(job.JobId, new NewBuildingType { Name = "  Type   4 ", Kind = BuildingKind.UnitType, Notes = "Two-bedroom" }, token, Ctx(Editor));

        Assert.Equal(("Type 4", BuildingKind.UnitType, "Two-bedroom"), (type.Name, type.Kind, type.Notes));
        Assert.Equal((null, null, false), (type.Multiplier, type.MultiplierSource, type.Confirmed));
        Assert.Contains("building-type.created", await ActionsAsync(job.JobId));
    }

    [Fact]
    public async Task Names_are_unique_within_a_job_ignoring_case_and_spacing_but_not_across_jobs()
    {
        var (job, token) = await NewJobAsync();
        var (other, otherToken) = await NewJobAsync();
        await Types().CreateAsync(job.JobId, new NewBuildingType { Name = "Office" }, token, Ctx(Editor));

        await Assert.ThrowsAsync<JobRuleViolationException>(() =>
            Types().CreateAsync(job.JobId, new NewBuildingType { Name = " OFFICE " }, token, Ctx(Editor)));
        await Types().CreateAsync(other.JobId, new NewBuildingType { Name = "Office" }, otherToken, Ctx(Editor));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Types().CreateAsync(job.JobId, new NewBuildingType { Name = "  " }, token, Ctx(Editor)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Types().CreateAsync(job.JobId, new NewBuildingType { Name = new string('x', 101) }, token, Ctx(Editor)));
    }

    [Fact]
    public async Task Setup_needs_the_edit_lock_and_edit_permission()
    {
        var (job, token) = await NewJobAsync();

        await Assert.ThrowsAsync<JobAccessDeniedException>(() => Types().CreateAsync(job.JobId, new NewBuildingType { Name = "x" }, token, Ctx(Viewer)));
        await Locks().ForceTakeoverAsync(job.JobId, Ctx(Owner));
        await Assert.ThrowsAsync<StaleEditTokenException>(() => Types().CreateAsync(job.JobId, new NewBuildingType { Name = "x" }, token, Ctx(Editor)));
        Assert.Empty(await Types().ListByJobAsync(job.JobId));
    }

    [Fact]
    public async Task A_closed_job_takes_no_building_type_changes()
    {
        var (job, token) = await NewJobAsync();
        var type = await Types().CreateAsync(job.JobId, new NewBuildingType { Name = "A" }, token, Ctx(Editor));
        await Jobs().CloseAsync(job.JobId, "done", Ctx(Editor));

        await Assert.ThrowsAsync<JobRuleViolationException>(() => Types().CreateAsync(job.JobId, new NewBuildingType { Name = "B" }, token, Ctx(Editor)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Types().UpdateAsync(type.BuildingTypeId, "Renamed", null, null, token, Ctx(Editor)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Types().SetMultiplierAsync(type.BuildingTypeId, 3, MultiplierSource.Manual, null, token, Ctx(Editor)));
    }

    [Fact]
    public async Task Editing_changes_only_what_changed_and_a_change_that_changes_nothing_writes_nothing()
    {
        var (job, token) = await NewJobAsync();
        var a = await Types().CreateAsync(job.JobId, new NewBuildingType { Name = "A" }, token, Ctx(Editor));
        await Types().CreateAsync(job.JobId, new NewBuildingType { Name = "B" }, token, Ctx(Editor));
        var events = (await Jobs().ListAuditAsync(job.JobId)).Count;

        var same = await Types().UpdateAsync(a.BuildingTypeId, "a", BuildingKind.Other, "", token, Ctx(Editor));
        Assert.Equal(events, (await Jobs().ListAuditAsync(job.JobId)).Count);
        Assert.Equal("A", same.Name);

        var renamed = await Types().UpdateAsync(a.BuildingTypeId, "Alpha", BuildingKind.Building, "Note", token, Ctx(Editor));
        Assert.Equal(("Alpha", BuildingKind.Building, "Note"), (renamed.Name, renamed.Kind, renamed.Notes));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Types().UpdateAsync(a.BuildingTypeId, " b ", null, null, token, Ctx(Editor)));   // taken
    }

    // ---- multipliers: a person confirms, a detector only suggests

    [Fact]
    public async Task A_person_entering_a_multiplier_confirms_it_and_the_change_is_logged_with_before_and_after()
    {
        var (job, token) = await NewJobAsync();
        var type = await Types().CreateAsync(job.JobId, new NewBuildingType { Name = "Type 4" }, token, Ctx(Editor));

        var set = await Types().SetMultiplierAsync(type.BuildingTypeId, 26, MultiplierSource.Manual, " counted from the site plan ", token, Ctx(Editor));

        Assert.Equal((26, MultiplierSource.Manual, true, Editor), (set.Multiplier, set.MultiplierSource, set.Confirmed, set.ConfirmedBy));
        Assert.Equal("counted from the site plan", set.MultiplierEvidence);
        Assert.NotNull(set.ConfirmedUtc);
        var logged = (await Jobs().ListAuditAsync(job.JobId)).Single(e => e.Action == "building-type.multiplier-confirmed");
        Assert.Contains("26", logged.AfterJson);
        Assert.Contains("null", logged.BeforeJson);     // there was no multiplier before
    }

    [Fact]
    public async Task Zero_is_a_valid_confirmed_multiplier_and_negative_is_not()
    {
        var (job, token) = await NewJobAsync();
        var type = await Types().CreateAsync(job.JobId, new NewBuildingType { Name = "Type 7" }, token, Ctx(Editor));

        var zero = await Types().SetMultiplierAsync(type.BuildingTypeId, 0, MultiplierSource.Manual, null, token, Ctx(Editor));
        Assert.Equal((0, true), (zero.Multiplier, zero.Confirmed));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Types().SetMultiplierAsync(type.BuildingTypeId, -1, MultiplierSource.Manual, null, token, Ctx(Editor)));
    }

    [Fact]
    public async Task A_multiplier_from_the_architectural_list_needs_its_evidence_and_a_suggestion_cannot_be_entered_as_confirmed()
    {
        var (job, token) = await NewJobAsync();
        var type = await Types().CreateAsync(job.JobId, new NewBuildingType { Name = "Type 1" }, token, Ctx(Editor));

        await Assert.ThrowsAsync<JobRuleViolationException>(() =>
            Types().SetMultiplierAsync(type.BuildingTypeId, 30, MultiplierSource.ArchitecturalList, " ", token, Ctx(Editor)));
        var listed = await Types().SetMultiplierAsync(type.BuildingTypeId, 30, MultiplierSource.ArchitecturalList, "Sheet T1.1, building list rows A to Q", token, Ctx(Editor));
        Assert.Equal(MultiplierSource.ArchitecturalList, listed.MultiplierSource);

        await Assert.ThrowsAsync<JobRuleViolationException>(() =>
            Types().SetMultiplierAsync(type.BuildingTypeId, 30, MultiplierSource.Suggested, "x", token, Ctx(Editor)));
    }

    [Fact]
    public async Task Entering_the_same_multiplier_again_changes_nothing()
    {
        var (job, token) = await NewJobAsync();
        var type = await Types().CreateAsync(job.JobId, new NewBuildingType { Name = "Type 1" }, token, Ctx(Editor));
        await Types().SetMultiplierAsync(type.BuildingTypeId, 5, MultiplierSource.Manual, "e", token, Ctx(Editor));
        var events = (await Jobs().ListAuditAsync(job.JobId)).Count;

        await Types().SetMultiplierAsync(type.BuildingTypeId, 5, MultiplierSource.Manual, "e", token, Ctx(Editor));

        Assert.Equal(events, (await Jobs().ListAuditAsync(job.JobId)).Count);
    }

    [Fact]
    public async Task A_detector_can_suggest_a_multiplier_but_it_is_never_confirmed_by_the_suggestion()
    {
        var (job, token) = await NewJobAsync();
        var type = await Types().CreateAsync(job.JobId, new NewBuildingType { Name = "Type 1" }, token, Ctx(Editor));
        var worker = AuditContext.ForWorker("worker-1");

        Assert.True(await Types().SuggestMultiplierAsync(type.BuildingTypeId, 26, "26 building names on the architectural index", worker));

        var after = (await Types().GetAsync(type.BuildingTypeId))!;
        Assert.Equal((26, MultiplierSource.Suggested, false, null), (after.Multiplier, after.MultiplierSource, after.Confirmed, after.ConfirmedBy));
        Assert.Contains("building-type.multiplier-suggested", await ActionsAsync(job.JobId));
        Assert.False(await Types().SuggestMultiplierAsync(type.BuildingTypeId, 26, "same again", worker));          // nothing new
    }

    [Fact]
    public async Task A_suggestion_never_overwrites_a_confirmed_multiplier_and_a_person_can_confirm_a_suggested_one()
    {
        var (job, token) = await NewJobAsync();
        var type = await Types().CreateAsync(job.JobId, new NewBuildingType { Name = "Type 1" }, token, Ctx(Editor));
        var worker = AuditContext.ForWorker("worker-1");

        await Types().SuggestMultiplierAsync(type.BuildingTypeId, 26, "guess", worker);
        var confirmed = await Types().SetMultiplierAsync(type.BuildingTypeId, 30, MultiplierSource.Manual, "site plan", token, Ctx(Editor));
        Assert.Equal((30, true), (confirmed.Multiplier, confirmed.Confirmed));

        Assert.False(await Types().SuggestMultiplierAsync(type.BuildingTypeId, 99, "another guess", worker));
        var after = (await Types().GetAsync(type.BuildingTypeId))!;
        Assert.Equal((30, MultiplierSource.Manual, true), (after.Multiplier, after.MultiplierSource, after.Confirmed));
    }

    [Fact]
    public async Task Only_a_person_can_confirm_and_a_person_cannot_make_a_suggestion()
    {
        var (job, token) = await NewJobAsync();
        var type = await Types().CreateAsync(job.JobId, new NewBuildingType { Name = "Type 1" }, token, Ctx(Editor));

        // A token belongs to a person's session, so the system cannot even present one. The rule inside is a second guard.
        await Assert.ThrowsAsync<StaleEditTokenException>(() => Types().SetMultiplierAsync(type.BuildingTypeId, 5, MultiplierSource.Manual, "x", token, AuditContext.ForSystem("importer")));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Types().SuggestMultiplierAsync(type.BuildingTypeId, 5, "x", Ctx(Editor)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Types().SuggestMultiplierAsync(type.BuildingTypeId, 5, " ", AuditContext.ForWorker("w")));
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => Types().SetMultiplierAsync(type.BuildingTypeId, 5, MultiplierSource.Manual, "x", token, Ctx(Viewer)));
    }

    [Fact]
    public async Task Clearing_a_multiplier_makes_it_unknown_again()
    {
        var (job, token) = await NewJobAsync();
        var type = await Types().CreateAsync(job.JobId, new NewBuildingType { Name = "Type 1" }, token, Ctx(Editor));
        await Types().SetMultiplierAsync(type.BuildingTypeId, 5, MultiplierSource.Manual, "e", token, Ctx(Editor));

        var cleared = await Types().ClearMultiplierAsync(type.BuildingTypeId, token, Ctx(Editor));

        Assert.Equal((null, null, false, null), (cleared.Multiplier, cleared.MultiplierSource, cleared.Confirmed, cleared.ConfirmedBy));
        Assert.Contains("building-type.multiplier-cleared", await ActionsAsync(job.JobId));
    }

    [Fact]
    public async Task The_database_refuses_a_confirmed_suggestion_or_a_value_without_a_source()
    {
        var (job, token) = await NewJobAsync();
        var type = await Types().CreateAsync(job.JobId, new NewBuildingType { Name = "Type 1" }, token, Ctx(Editor));
        var id = ("@id", (object?)type.BuildingTypeId);

        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync(
            "UPDATE ductwork.BuildingType SET Multiplier = 5, MultiplierSource = 'Suggested', Confirmed = 1, ConfirmedBy = 'x', ConfirmedUtc = SYSUTCDATETIME() WHERE BuildingTypeId = @id;", id));
        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync("UPDATE ductwork.BuildingType SET Multiplier = 5 WHERE BuildingTypeId = @id;", id));
        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync("UPDATE ductwork.BuildingType SET Confirmed = 1 WHERE BuildingTypeId = @id;", id));
        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync(
            "UPDATE ductwork.BuildingType SET Multiplier = -2, MultiplierSource = 'Manual' WHERE BuildingTypeId = @id;", id));
    }

    // ---- deleting

    [Fact]
    public async Task A_building_type_with_counted_items_cannot_be_deleted_but_an_empty_one_can()
    {
        var (job, token) = await NewJobAsync();
        var used = await Types().CreateAsync(job.JobId, new NewBuildingType { Name = "Used" }, token, Ctx(Editor));
        var empty = await Types().CreateAsync(job.JobId, new NewBuildingType { Name = "Empty" }, token, Ctx(Editor));
        await new IngestFixture(db).StoreOneEntityAsync(job.JobId, used.BuildingTypeId);

        var refused = await Assert.ThrowsAsync<JobRuleViolationException>(() => Types().DeleteAsync(used.BuildingTypeId, token, Ctx(Editor)));
        Assert.Contains("1 counted item", refused.Message);
        Assert.True(await Types().DeleteAsync(empty.BuildingTypeId, token, Ctx(Editor)));
        Assert.Null(await Types().GetAsync(empty.BuildingTypeId));
        Assert.Contains("building-type.deleted", await ActionsAsync(job.JobId));
    }

    // ---- completing a job

    [Fact]
    public async Task A_job_cannot_be_completed_while_a_type_with_counted_items_has_no_confirmed_multiplier()
    {
        var (job, token) = await NewJobAsync();
        var type = await Types().CreateAsync(job.JobId, new NewBuildingType { Name = "Type 4" }, token, Ctx(Editor));
        await new IngestFixture(db).StoreOneEntityAsync(job.JobId, type.BuildingTypeId);
        foreach (var step in Enum.GetValues<WorkflowStep>())
            await Jobs().SetWorkflowStepAsync(job.JobId, step, StepStatus.Done, token, Ctx(Editor));

        var blocked = await Assert.ThrowsAsync<JobRuleViolationException>(() => Jobs().BeginCompleteAsync(job.JobId, Ctx(Editor)));
        Assert.Contains("1 building type", blocked.Message);
        Assert.Contains("multiplier", blocked.Message);

        await Types().SuggestMultiplierAsync(type.BuildingTypeId, 26, "guess", AuditContext.ForWorker("w"));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Jobs().BeginCompleteAsync(job.JobId, Ctx(Editor)));   // a suggestion is not enough

        await Types().SetMultiplierAsync(type.BuildingTypeId, 26, MultiplierSource.Manual, "site plan", token, Ctx(Editor));
        Assert.Equal(JobState.Completing, (await Jobs().BeginCompleteAsync(job.JobId, Ctx(Editor))).State);
    }

    [Fact]
    public async Task An_unconfirmed_type_with_nothing_counted_does_not_block_completion()
    {
        var (job, token) = await NewJobAsync();
        await Types().CreateAsync(job.JobId, new NewBuildingType { Name = "Empty type" }, token, Ctx(Editor));
        foreach (var step in Enum.GetValues<WorkflowStep>())
            await Jobs().SetWorkflowStepAsync(job.JobId, step, StepStatus.Done, token, Ctx(Editor));

        Assert.Equal(JobState.Completing, (await Jobs().BeginCompleteAsync(job.JobId, Ctx(Editor))).State);
    }

    [Fact]
    public async Task The_count_sheet_for_a_job_extends_when_the_multiplier_is_confirmed()
    {
        var (job, token) = await NewJobAsync();
        var type = await Types().CreateAsync(job.JobId, new NewBuildingType { Name = "Type 4" }, token, Ctx(Editor));
        var fixture = new IngestFixture(db);
        for (var i = 0; i < 3; i++) await fixture.StoreOneEntityAsync(job.JobId, type.BuildingTypeId, detectionId: "d" + i);

        var sheets = new CountSheetRepository(db.ConnectionString);
        var before = await sheets.BuildAsync(job.JobId);
        Assert.Null(before.Totals.Single().Total);
        Assert.Equal(["Type 4"], before.UnconfirmedBuildingTypes);

        await Types().SetMultiplierAsync(type.BuildingTypeId, 26, MultiplierSource.Manual, "site plan", token, Ctx(Editor));
        var after = await sheets.BuildAsync(job.JobId);

        Assert.Equal(78m, after.Totals.Single().Total);
        Assert.Equal(3m, after.Blocks.Single().Lines.Single().QuantityPerBuilding);
        Assert.Empty(after.UnconfirmedBuildingTypes);
    }
}

/// <summary>Stores entities the one legitimate way, through a task and its result, so these tests exercise the real path.</summary>
internal sealed class IngestFixture(SqlFixture db)
{
    public async Task StoreOneEntityAsync(long jobId, long buildingTypeId, string detectionId = "d1")
    {
        // Straight SQL is used here only to set up rows for tests of other behavior. Producer and task are real rows.
        await using var connection = await db.OpenAsync();
        var producer = await ProducerStore.EnsureProducerAsync(connection, ProducerRef.Algorithm("fixture-producer", "1.0", "fixture"));
        var taskId = await db.ScalarAsync<long>(
            "INSERT INTO ductwork.ProcessingTask (JobId, TaskType, Status, ProducerId, ContractVersion, CreatedBy, FinishedUtc) " +
            "OUTPUT INSERTED.ProcessingTaskId VALUES (@j, 'fixture', 'Succeeded', @p, '0.1.0', 'fixture', SYSUTCDATETIME());",
            ("@j", jobId), ("@p", producer));
        await db.ExecAsync(
            """
            INSERT INTO ductwork.Entity (JobId, BuildingTypeId, ProducerId, ProcessingTaskId, DetectionId, Type, Size, Quantity, Method, Family, Confidence, SurfacedAs, ReviewLevelAtCreation)
            VALUES (@j, @b, @p, @t, @d, 'Boot', '4x10x7', 1, 'Text', 'LabelFlagCount', 0.9, 'Finding', 'Sensitive');
            """, ("@j", jobId), ("@b", buildingTypeId), ("@p", producer), ("@t", taskId), ("@d", detectionId));
    }
}
