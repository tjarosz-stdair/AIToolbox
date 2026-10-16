using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Jobs;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Tests;

public class JobRepositoryTests(SqlFixture db) : IClassFixture<SqlFixture>
{
    private const string Owner = "owner@example.com";
    private const string Editor = "editor@example.com";
    private const string Viewer = "viewer@example.com";
    private const string Stranger = "stranger@example.com";

    private JobRepository Repo(ICompletionProbe? probe = null) => new(db.ConnectionString, probe);

    private static AuditContext Ctx(string email, bool admin = false) =>
        AuditContext.ForUser(email, displayName: email.Split('@')[0], sessionId: "s-" + email, clientIp: "10.0.0.1", userAgent: "tests", isAdmin: admin);

    private async Task<JobRecord> NewJobAsync(JobRepository? repo = null, string? name = null)
    {
        repo ??= Repo();
        var job = await repo.CreateAsync(new NewJob { Name = name ?? "Job " + Guid.NewGuid().ToString("N")[..8], CustomerName = "Acme Homes" }, Ctx(Owner));
        await repo.AddMemberAsync(job.JobId, Editor, JobRole.Editor, Ctx(Owner));
        await repo.AddMemberAsync(job.JobId, Viewer, JobRole.Viewer, Ctx(Owner));
        return (await repo.GetAsync(job.JobId))!;
    }

    private EditLockService Locks(LockSettings? settings = null) => new(db.ConnectionString, settings);

    private async Task<EditToken> TokenAsync(long jobId, string email = Editor)
    {
        var result = await Locks().AcquireAsync(jobId, Ctx(email));
        return result.Token ?? throw new InvalidOperationException($"{email} could not take the lock: held by {result.Holder?.HolderEmail}.");
    }

    private async Task FinishAllStepsAsync(JobRepository repo, long jobId)
    {
        var tok = await TokenAsync(jobId);
        foreach (var step in Enum.GetValues<WorkflowStep>())
            await repo.SetWorkflowStepAsync(jobId, step, StepStatus.Done, tok, Ctx(Editor));
    }

    private async Task<JobRecord> CompleteAsync(JobRepository repo, long jobId)
    {
        await FinishAllStepsAsync(repo, jobId);
        await repo.BeginCompleteAsync(jobId, Ctx(Editor));
        return await repo.FinishCompleteAsync(jobId, Ctx(Editor));
    }

    // ---- create

    [Fact]
    public async Task Creating_a_job_makes_the_creator_owner_sets_up_the_steps_and_logs_it()
    {
        var repo = Repo();
        var job = await repo.CreateAsync(new NewJob { Name = "  Greenfield  ", SiteAddress = " 1 Main St ", ReviewLevel = ReviewLevel.Normal }, Ctx(Owner));

        Assert.Equal("Greenfield", job.Name);
        Assert.Equal("1 Main St", job.SiteAddress);
        Assert.Equal((JobState.Draft, 1L, ReviewLevel.Normal), (job.State, job.StateVersion, job.ReviewLevel));
        Assert.Equal(Owner, job.OwnerEmail);

        var members = await repo.GetMembersAsync(job.JobId);
        Assert.Equal([(Owner, JobRole.Owner)], members.Select(m => (m.UserEmail, m.Role)));
        var steps = await repo.GetWorkflowAsync(job.JobId);
        Assert.Equal(4, steps.Count);
        Assert.All(steps.Values, s => Assert.Equal(StepStatus.NotStarted, s));

        var events = await repo.ListAuditAsync(job.JobId);
        var created = Assert.Single(events);
        Assert.Equal(("job.created", 1L, Owner, "tests"), (created.Action, created.EventId, created.ActorEmail, created.UserAgent));
        Assert.True((await repo.VerifyAuditChainAsync(job.JobId)).IsValid);
    }

    [Fact]
    public async Task A_job_needs_a_name_and_a_user_to_own_it()
    {
        var repo = Repo();
        await Assert.ThrowsAsync<JobRuleViolationException>(() => repo.CreateAsync(new NewJob { Name = "  " }, Ctx(Owner)));
        await Assert.ThrowsAsync<ArgumentException>(() => repo.CreateAsync(new NewJob { Name = "x" }, AuditContext.ForSystem("importer")));
    }

    [Fact]
    public async Task The_audit_time_is_the_database_clock_not_the_callers()
    {
        var before = DateTime.UtcNow.AddMinutes(-2);
        var job = await Repo().CreateAsync(new NewJob { Name = "clock" }, Ctx(Owner));
        var at = (await Repo().ListAuditAsync(job.JobId)).Single().OccurredUtc;
        Assert.InRange(at, before, DateTime.UtcNow.AddMinutes(2));
        Assert.Equal(DateTimeKind.Utc, at.Kind);
    }

    // ---- details

    [Fact]
    public async Task Editing_details_changes_only_what_changed_and_logs_before_and_after()
    {
        var repo = Repo();
        var job = await NewJobAsync(repo);
        var tok = await TokenAsync(job.JobId);

        var updated = await repo.UpdateDetailsAsync(job.JobId,
            new JobDetails { Name = "Renamed", ProjectName = "Phase 2", CustomerName = job.CustomerName }, job.StateVersion, tok, Ctx(Editor));

        Assert.Equal(("Renamed", "Phase 2", "Acme Homes"), (updated.Name, updated.ProjectName, updated.CustomerName));
        Assert.True(updated.StateVersion > job.StateVersion);

        var edited = (await repo.ListAuditAsync(job.JobId)).Last();
        Assert.Equal("job.edited", edited.Action);
        Assert.Contains("Renamed", edited.AfterJson);
        Assert.Contains(job.Name, edited.BeforeJson);
        Assert.DoesNotContain("Acme", edited.AfterJson);       // unchanged fields are not in the diff
        Assert.Equal(updated.StateVersion, edited.StateVersion);
    }

    [Fact]
    public async Task A_change_that_changes_nothing_writes_no_event_and_does_not_bump_the_version()
    {
        var repo = Repo();
        var job = await NewJobAsync(repo);
        var tok = await TokenAsync(job.JobId);
        var eventsBefore = (await repo.ListAuditAsync(job.JobId)).Count;

        var same = await repo.UpdateDetailsAsync(job.JobId, new JobDetails { Name = job.Name }, job.StateVersion, tok, Ctx(Editor));
        await repo.SetReviewLevelAsync(job.JobId, job.ReviewLevel, tok, Ctx(Editor));
        await repo.AddTagAsync(job.JobId, "Orion", Ctx(Editor));
        await repo.AddTagAsync(job.JobId, "  orion ", Ctx(Editor));   // the same tag again
        await repo.SetWorkflowStepAsync(job.JobId, WorkflowStep.Setup, StepStatus.NotStarted, tok, Ctx(Editor));

        Assert.Equal(job.StateVersion + 1, (await repo.GetAsync(job.JobId))!.StateVersion);   // only the tag counted
        Assert.Equal(eventsBefore + 1, (await repo.ListAuditAsync(job.JobId)).Count);
        Assert.Equal(job.Name, same.Name);
    }

    [Fact]
    public async Task An_optional_field_can_be_cleared_and_the_name_cannot_be_blanked()
    {
        var repo = Repo();
        var job = await NewJobAsync(repo);
        var tok = await TokenAsync(job.JobId);

        var cleared = await repo.UpdateDetailsAsync(job.JobId, new JobDetails { Clear = new HashSet<string> { "CustomerName" } }, job.StateVersion, tok, Ctx(Editor));
        Assert.Null(cleared.CustomerName);

        await Assert.ThrowsAsync<JobRuleViolationException>(() =>
            repo.UpdateDetailsAsync(job.JobId, new JobDetails { Name = " " }, cleared.StateVersion, tok, Ctx(Editor)));
    }

    [Fact]
    public async Task A_stale_version_is_refused_so_nobody_silently_overwrites_a_newer_change()
    {
        var repo = Repo();
        var job = await NewJobAsync(repo);
        var tok = await TokenAsync(job.JobId);
        await repo.UpdateDetailsAsync(job.JobId, new JobDetails { ProjectName = "first" }, job.StateVersion, tok, Ctx(Editor));

        var ex = await Assert.ThrowsAsync<JobConcurrencyException>(() =>
            repo.UpdateDetailsAsync(job.JobId, new JobDetails { ProjectName = "second" }, job.StateVersion, tok, Ctx(Editor)));
        Assert.Contains("Reload", ex.Message);
        Assert.Equal("first", (await repo.GetAsync(job.JobId))!.ProjectName);
    }

    [Fact]
    public async Task Only_one_of_several_simultaneous_edits_from_the_same_version_wins()
    {
        var repo = Repo();
        var job = await NewJobAsync(repo);
        var tok = await TokenAsync(job.JobId);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            try
            {
                await Repo().UpdateDetailsAsync(job.JobId, new JobDetails { ProjectName = "p" + i }, job.StateVersion, tok, Ctx(Editor));
                return true;
            }
            catch (JobConcurrencyException)
            {
                return false;
            }
        }));

        Assert.Single(results, r => r);
        Assert.True((await repo.VerifyAuditChainAsync(job.JobId)).IsValid);
    }

    [Fact]
    public async Task Viewers_and_strangers_cannot_edit_and_nothing_is_recorded_for_a_refused_change()
    {
        var repo = Repo();
        var job = await NewJobAsync(repo);
        var tok = await TokenAsync(job.JobId);
        var events = (await repo.ListAuditAsync(job.JobId)).Count;

        await Assert.ThrowsAsync<JobAccessDeniedException>(() =>
            repo.UpdateDetailsAsync(job.JobId, new JobDetails { Name = "x" }, job.StateVersion, tok, Ctx(Viewer)));
        await Assert.ThrowsAsync<JobAccessDeniedException>(() =>
            repo.UpdateDetailsAsync(job.JobId, new JobDetails { Name = "x" }, job.StateVersion, tok, Ctx(Stranger)));
        await Assert.ThrowsAsync<JobAccessDeniedException>(() =>
            repo.UpdateDetailsAsync(job.JobId, new JobDetails { Name = "x" }, job.StateVersion, tok, Ctx(Stranger, admin: true)));

        Assert.Equal(events, (await repo.ListAuditAsync(job.JobId)).Count);
        Assert.Equal(job.StateVersion, (await repo.GetAsync(job.JobId))!.StateVersion);
    }

    [Fact]
    public async Task A_missing_job_is_reported_as_missing()
    {
        var real = await NewJobAsync();
        var tok = await TokenAsync(real.JobId);
        await Assert.ThrowsAsync<JobNotFoundException>(() =>
            Repo().UpdateDetailsAsync(long.MaxValue, new JobDetails { Name = "x" }, 1, tok, Ctx(Editor)));
        Assert.Null(await Repo().GetAsync(long.MaxValue));
    }

    [Fact]
    public async Task The_review_level_and_baseline_can_be_set_and_are_logged()
    {
        var repo = Repo();
        var job = await NewJobAsync(repo);
        var tok = await TokenAsync(job.JobId);

        var level = await repo.SetReviewLevelAsync(job.JobId, ReviewLevel.Insensitive, tok, Ctx(Editor));
        var baseline = await repo.SetBaselineManualHoursAsync(job.JobId, 6.5m, Ctx(Editor));

        Assert.Equal(ReviewLevel.Insensitive, level.ReviewLevel);
        Assert.Equal(6.5m, baseline.BaselineManualHours);
        var actions = (await repo.ListAuditAsync(job.JobId)).Select(e => e.Action).ToList();
        Assert.Contains("job.review-level-changed", actions);
        Assert.Contains("job.baseline-changed", actions);
        await Assert.ThrowsAsync<JobRuleViolationException>(() => repo.SetBaselineManualHoursAsync(job.JobId, -1m, Ctx(Editor)));
    }

    // ---- members

    [Fact]
    public async Task Owners_manage_members_and_editors_and_viewers_cannot()
    {
        var repo = Repo();
        var job = await NewJobAsync(repo);

        await repo.AddMemberAsync(job.JobId, "  New.Person@Example.com ", JobRole.Editor, Ctx(Owner));
        Assert.Contains((await repo.GetMembersAsync(job.JobId)), m => m.UserEmail == "new.person@example.com");

        await Assert.ThrowsAsync<JobAccessDeniedException>(() => repo.AddMemberAsync(job.JobId, "x@example.com", JobRole.Viewer, Ctx(Editor)));
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => repo.RemoveMemberAsync(job.JobId, Viewer, Ctx(Viewer)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => repo.AddMemberAsync(job.JobId, Editor, JobRole.Viewer, Ctx(Owner)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => repo.AddMemberAsync(job.JobId, "not-an-email", JobRole.Viewer, Ctx(Owner)));
    }

    [Fact]
    public async Task A_job_always_keeps_an_owner_and_the_primary_owner_follows_the_remaining_owners()
    {
        var repo = Repo();
        var job = await NewJobAsync(repo);
        var tok = await TokenAsync(job.JobId);

        await Assert.ThrowsAsync<JobRuleViolationException>(() => repo.RemoveMemberAsync(job.JobId, Owner, Ctx(Owner)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => repo.ChangeMemberRoleAsync(job.JobId, Owner, JobRole.Editor, Ctx(Owner)));

        await repo.ChangeMemberRoleAsync(job.JobId, Editor, JobRole.Owner, Ctx(Owner));
        await repo.RemoveMemberAsync(job.JobId, Owner, Ctx(Editor));

        var after = (await repo.GetAsync(job.JobId))!;
        Assert.Equal(Editor, after.OwnerEmail);
        Assert.DoesNotContain(await repo.GetMembersAsync(job.JobId), m => m.UserEmail == Owner);
        // The person who was removed can no longer act on the job.
        await Assert.ThrowsAsync<JobAccessDeniedException>(() =>
            repo.UpdateDetailsAsync(job.JobId, new JobDetails { Name = "x" }, after.StateVersion, tok, Ctx(Owner)));
    }

    // ---- tags

    [Fact]
    public async Task Tags_are_shared_across_jobs_ignoring_case_and_spacing_and_filter_all_of()
    {
        var repo = Repo();
        var tag = "Orion " + Guid.NewGuid().ToString("N")[..6];
        var a = await NewJobAsync(repo);
        var b = await NewJobAsync(repo);

        await repo.AddTagAsync(a.JobId, tag, Ctx(Editor));
        await repo.AddTagAsync(b.JobId, "  " + tag.ToUpperInvariant().Replace(" ", "   ") + " ", Ctx(Editor));
        await repo.AddTagAsync(a.JobId, "Bid", Ctx(Editor));

        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.Tag WHERE NameNormalized = @n;", ("@n", tag.ToLowerInvariant())));

        var both = await repo.ListAsync(new JobListFilter { Tags = [tag] });
        Assert.Equal(new[] { a.JobId, b.JobId }.Order(), both.Select(j => j.JobId).Order());
        var narrowed = await repo.ListAsync(new JobListFilter { Tags = [tag, "bid"] });
        Assert.Equal([a.JobId], narrowed.Select(j => j.JobId));

        await repo.RemoveTagAsync(a.JobId, tag.ToUpperInvariant(), Ctx(Editor));
        Assert.DoesNotContain(await repo.GetTagsAsync(a.JobId), t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Tags_can_be_changed_on_a_complete_job_without_reopening_it_but_not_by_viewers()
    {
        var repo = Repo();
        var job = await NewJobAsync(repo);
        await CompleteAsync(repo, job.JobId);

        await repo.AddTagAsync(job.JobId, "Won", Ctx(Editor));
        Assert.Contains("Won", await repo.GetTagsAsync(job.JobId));
        Assert.Equal(JobState.Complete, (await repo.GetAsync(job.JobId))!.State);
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => repo.AddTagAsync(job.JobId, "x", Ctx(Viewer)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => repo.AddTagAsync(job.JobId, "   ", Ctx(Editor)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => repo.AddTagAsync(job.JobId, new string('x', 101), Ctx(Editor)));
    }

    [Fact]
    public async Task Many_people_adding_the_same_new_tag_at_once_all_succeed_and_share_one_tag()
    {
        var repo = Repo();
        var tag = "Race " + Guid.NewGuid().ToString("N")[..6];
        var jobs = new List<JobRecord>();
        for (var i = 0; i < 6; i++) jobs.Add(await NewJobAsync(repo));

        await Task.WhenAll(jobs.Select(j => Repo().AddTagAsync(j.JobId, tag, Ctx(Editor))));

        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.Tag WHERE NameNormalized = @n;", ("@n", tag.ToLowerInvariant())));
        Assert.Equal(6, (await repo.ListAsync(new JobListFilter { Tags = [tag] })).Count);
    }

    // ---- workflow and lifecycle

    [Fact]
    public async Task Completing_waits_for_every_step_then_makes_the_job_read_only()
    {
        var repo = Repo();
        var job = await NewJobAsync(repo);
        var tok = await TokenAsync(job.JobId);

        var early = await Assert.ThrowsAsync<JobRuleViolationException>(() => repo.BeginCompleteAsync(job.JobId, Ctx(Editor)));
        Assert.Contains("close it instead", early.Message);

        await FinishAllStepsAsync(repo, job.JobId);
        var completing = await repo.BeginCompleteAsync(job.JobId, Ctx(Editor));
        Assert.Equal(JobState.Completing, completing.State);

        // A job that is completing is held still: no edits and no second completion.
        await Assert.ThrowsAsync<JobRuleViolationException>(() =>
            repo.UpdateDetailsAsync(job.JobId, new JobDetails { Name = "x" }, completing.StateVersion, tok, Ctx(Editor)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => repo.BeginCompleteAsync(job.JobId, Ctx(Editor)));

        var done = await repo.FinishCompleteAsync(job.JobId, Ctx(Editor));
        Assert.Equal((JobState.Complete, 1, Editor), (done.State, done.CompletionVersion, done.CompletedBy));
        Assert.NotNull(done.CompletedUtc);

        await Assert.ThrowsAsync<JobRuleViolationException>(() =>
            repo.SetWorkflowStepAsync(job.JobId, WorkflowStep.Review, StepStatus.Active, tok, Ctx(Editor)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() =>
            repo.UpdateDetailsAsync(job.JobId, new JobDetails { Name = "x" }, done.StateVersion, tok, Ctx(Editor)));
        Assert.True((await repo.VerifyAuditChainAsync(job.JobId)).IsValid);
    }

    [Fact]
    public async Task Viewers_cannot_complete_close_or_reopen()
    {
        var repo = Repo();
        var job = await NewJobAsync(repo);
        await FinishAllStepsAsync(repo, job.JobId);

        await Assert.ThrowsAsync<JobAccessDeniedException>(() => repo.BeginCompleteAsync(job.JobId, Ctx(Viewer)));
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => repo.CloseAsync(job.JobId, "reason", Ctx(Viewer)));
        await repo.CloseAsync(job.JobId, "Lost the bid", Ctx(Editor));
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => repo.ReopenAsync(job.JobId, "reason", Ctx(Viewer)));
    }

    [Fact]
    public async Task Closing_needs_a_reason_records_it_and_can_be_reopened_with_one()
    {
        var repo = Repo();
        var job = await NewJobAsync(repo);

        await Assert.ThrowsAsync<JobRuleViolationException>(() => repo.CloseAsync(job.JobId, " ", Ctx(Editor)));
        var closed = await repo.CloseAsync(job.JobId, "Lost the bid", Ctx(Editor));
        Assert.Equal((JobState.Closed, "Lost the bid", Editor), (closed.State, closed.CloseReason, closed.ClosedBy));

        await Assert.ThrowsAsync<JobRuleViolationException>(() => repo.ReopenAsync(job.JobId, "", Ctx(Editor)));
        var reopened = await repo.ReopenAsync(job.JobId, "Bid is live again", Ctx(Editor));
        Assert.Equal((JobState.Draft, "Bid is live again"), (reopened.State, reopened.ReopenReason));

        var events = await repo.ListAuditAsync(job.JobId);
        var closeEvent = events.Single(e => e.Action == "job.closed");
        var reopenEvent = events.Single(e => e.Action == "job.reopened");
        Assert.Equal("Lost the bid", closeEvent.Reason);
        Assert.Equal("Bid is live again", reopenEvent.Reason);
    }

    [Fact]
    public async Task Reopening_and_completing_again_raises_the_completion_version()
    {
        var repo = Repo();
        var job = await NewJobAsync(repo);
        var first = await CompleteAsync(repo, job.JobId);
        Assert.Equal(1, first.CompletionVersion);

        await repo.ReopenAsync(job.JobId, "Addendum received", Ctx(Editor));
        await repo.BeginCompleteAsync(job.JobId, Ctx(Editor));
        var second = await repo.FinishCompleteAsync(job.JobId, Ctx(Editor));

        Assert.Equal(2, second.CompletionVersion);
    }

    [Fact]
    public async Task Running_tasks_and_unconfirmed_multipliers_block_completing_and_closing()
    {
        var job = await NewJobAsync();
        var busy = Repo(new FixedProbe(active: 2, unconfirmed: 0));
        await FinishAllStepsAsync(busy, job.JobId);

        var complete = await Assert.ThrowsAsync<JobRuleViolationException>(() => busy.BeginCompleteAsync(job.JobId, Ctx(Editor)));
        Assert.Contains("2 processing task", complete.Message);
        await Assert.ThrowsAsync<JobRuleViolationException>(() => busy.CloseAsync(job.JobId, "reason", Ctx(Editor)));

        var unconfirmed = Repo(new FixedProbe(active: 0, unconfirmed: 3));
        var multipliers = await Assert.ThrowsAsync<JobRuleViolationException>(() => unconfirmed.BeginCompleteAsync(job.JobId, Ctx(Editor)));
        Assert.Contains("multiplier", multipliers.Message);

        Assert.Equal(JobState.Draft, (await Repo().GetAsync(job.JobId))!.State);
    }

    private sealed class FixedProbe(int active, int unconfirmed) : ICompletionProbe
    {
        public Task<(int ActiveProcessingTasks, int UnconfirmedMultipliers)> GetAsync(SqlConnection connection, SqlTransaction transaction, long jobId, CancellationToken ct) =>
            Task.FromResult((active, unconfirmed));
    }

    // ---- listing

    [Fact]
    public async Task The_job_list_filters_by_state_owner_and_membership()
    {
        var repo = Repo();
        var mine = await repo.CreateAsync(new NewJob { Name = "mine " + Guid.NewGuid().ToString("N")[..6] }, Ctx("lister@example.com"));
        var other = await NewJobAsync(repo);
        await repo.CloseAsync(other.JobId, "done", Ctx(Owner));

        var owned = await repo.ListAsync(new JobListFilter { OwnerEmail = "lister@example.com" });
        Assert.Equal([mine.JobId], owned.Select(j => j.JobId));

        var closed = await repo.ListAsync(new JobListFilter { States = [JobState.Closed], MemberEmail = Viewer });
        Assert.Contains(closed, j => j.JobId == other.JobId);
        Assert.All(closed, j => Assert.Equal(JobState.Closed, j.State));

        var notMember = await repo.ListAsync(new JobListFilter { MemberEmail = "nobody@example.com" });
        Assert.Empty(notMember);
    }

    // ---- the audit log itself

    [Fact]
    public async Task Events_are_numbered_from_one_without_gaps_and_the_chain_verifies()
    {
        var repo = Repo();
        var job = await NewJobAsync(repo);
        var tok = await TokenAsync(job.JobId);
        await repo.AddTagAsync(job.JobId, "A", Ctx(Editor));
        await repo.SetReviewLevelAsync(job.JobId, ReviewLevel.Normal, tok, Ctx(Editor));
        await repo.RemoveTagAsync(job.JobId, "A", Ctx(Editor));

        var events = await repo.ListAuditAsync(job.JobId);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.EventId));
        Assert.Equal(AuditHasher.Genesis, events[0].PrevHash);
        for (var i = 1; i < events.Count; i++) Assert.Equal(events[i - 1].Hash, events[i].PrevHash);
        Assert.True(events.Select(e => e.StateVersion).SequenceEqual(events.Select(e => e.StateVersion).Order()));

        var verification = await repo.VerifyAuditChainAsync(job.JobId);
        Assert.True(verification.IsValid);
        Assert.Equal(events.Count, verification.EventCount);
        Assert.Equal(events[^1].Hash, verification.HeadHash);
    }

    [Fact]
    public async Task Many_simultaneous_changes_to_one_job_still_produce_one_unbroken_chain()
    {
        var repo = Repo();
        var job = await NewJobAsync(repo);
        var before = (await repo.ListAuditAsync(job.JobId)).Count;

        await Task.WhenAll(Enumerable.Range(0, 15).Select(i => Repo().AddTagAsync(job.JobId, "t" + i, Ctx(Editor))));

        var events = await repo.ListAuditAsync(job.JobId);
        Assert.Equal(before + 15, events.Count);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.EventId));
        Assert.True((await repo.VerifyAuditChainAsync(job.JobId)).IsValid);
    }

    [Fact]
    public async Task The_audit_log_can_be_filtered_by_category_actor_and_text()
    {
        var repo = Repo();
        var job = await NewJobAsync(repo);
        await repo.AddTagAsync(job.JobId, "Searchable", Ctx(Editor));

        Assert.All(await repo.ListAuditAsync(job.JobId, new AuditFilter { Category = "tags" }), e => Assert.Equal("tags", e.Category));
        Assert.All(await repo.ListAuditAsync(job.JobId, new AuditFilter { ActorEmail = " EDITOR@example.com " }), e => Assert.Equal(Editor, e.ActorEmail));
        Assert.Single(await repo.ListAuditAsync(job.JobId, new AuditFilter { Text = "Searchable" }));
        Assert.Empty(await repo.ListAuditAsync(job.JobId, new AuditFilter { Text = "%" }));   // wildcards are literal
        var after = await repo.ListAuditAsync(job.JobId, new AuditFilter { AfterEventId = 2 });
        Assert.All(after, e => Assert.True(e.EventId > 2));
    }

    [Fact]
    public async Task The_database_refuses_to_change_or_delete_an_audit_event_or_a_job()
    {
        var job = await NewJobAsync();

        var update = await Assert.ThrowsAsync<SqlException>(() =>
            db.ExecAsync("UPDATE ductwork.JobAuditEvent SET Summary = 'edited' WHERE JobId = @j;", ("@j", job.JobId)));
        Assert.Contains("append-only", update.Message);
        var delete = await Assert.ThrowsAsync<SqlException>(() =>
            db.ExecAsync("DELETE FROM ductwork.JobAuditEvent WHERE JobId = @j;", ("@j", job.JobId)));
        Assert.Contains("append-only", delete.Message);
        var deleteJob = await Assert.ThrowsAsync<SqlException>(() =>
            db.ExecAsync("DELETE FROM ductwork.Job WHERE JobId = @j;", ("@j", job.JobId)));
        Assert.Contains("permanent", deleteJob.Message);

        Assert.NotNull(await Repo().GetAsync(job.JobId));
    }

    [Fact]
    public async Task Verification_names_the_first_event_that_was_tampered_with_even_if_the_trigger_is_bypassed()
    {
        var repo = Repo();
        var job = await NewJobAsync(repo);
        await repo.AddTagAsync(job.JobId, "T1", Ctx(Editor));
        await repo.AddTagAsync(job.JobId, "T2", Ctx(Editor));
        Assert.True((await repo.VerifyAuditChainAsync(job.JobId)).IsValid);

        // Someone with enough rights disables the trigger and rewrites history.
        await db.ExecAsync("DISABLE TRIGGER ductwork.TR_JobAuditEvent_AppendOnly ON ductwork.JobAuditEvent;");
        try
        {
            await db.ExecAsync("UPDATE ductwork.JobAuditEvent SET Summary = 'rewritten' WHERE JobId = @j AND EventId = 4;", ("@j", job.JobId));
        }
        finally
        {
            await db.ExecAsync("ENABLE TRIGGER ductwork.TR_JobAuditEvent_AppendOnly ON ductwork.JobAuditEvent;");
        }

        var result = await repo.VerifyAuditChainAsync(job.JobId);
        Assert.False(result.IsValid);
        Assert.Equal(4, result.FirstBadEventId);
        Assert.Contains("changed after it was written", result.Problem);
    }

    [Fact]
    public async Task Verification_notices_a_removed_event()
    {
        var repo = Repo();
        var job = await NewJobAsync(repo);
        await repo.AddTagAsync(job.JobId, "T1", Ctx(Editor));
        await repo.AddTagAsync(job.JobId, "T2", Ctx(Editor));

        await db.ExecAsync("DISABLE TRIGGER ductwork.TR_JobAuditEvent_AppendOnly ON ductwork.JobAuditEvent;");
        try
        {
            await db.ExecAsync("DELETE FROM ductwork.JobAuditEvent WHERE JobId = @j AND EventId = 3;", ("@j", job.JobId));
        }
        finally
        {
            await db.ExecAsync("ENABLE TRIGGER ductwork.TR_JobAuditEvent_AppendOnly ON ductwork.JobAuditEvent;");
        }

        var result = await repo.VerifyAuditChainAsync(job.JobId);
        Assert.False(result.IsValid);
        Assert.Equal(3, result.FirstBadEventId);
        Assert.Contains("missing", result.Problem);
    }

    [Fact]
    public async Task A_change_and_its_audit_event_succeed_or_fail_together()
    {
        var repo = Repo();
        var job = await NewJobAsync(repo);
        var tok = await TokenAsync(job.JobId);
        var events = (await repo.ListAuditAsync(job.JobId)).Count;

        // A system actor whose display name is too long for the audit column makes the audit insert fail.
        var broken = AuditContext.ForSystem(new string('x', 400));
        await Assert.ThrowsAsync<SqlException>(() => repo.SetBaselineManualHoursAsync(job.JobId, 42m, broken));

        var after = (await repo.GetAsync(job.JobId))!;
        Assert.Null(after.BaselineManualHours);
        Assert.Equal(job.StateVersion, after.StateVersion);
        Assert.Equal(events, (await repo.ListAuditAsync(job.JobId)).Count);
    }

    [Fact]
    public async Task A_closed_job_cannot_exist_without_a_reason_at_the_database_level()
    {
        var job = await NewJobAsync();
        await Assert.ThrowsAsync<SqlException>(() =>
            db.ExecAsync("UPDATE ductwork.Job SET State = 'Closed', CloseReason = NULL WHERE JobId = @j;", ("@j", job.JobId)));
        await Assert.ThrowsAsync<SqlException>(() =>
            db.ExecAsync("UPDATE ductwork.Job SET State = 'Nonsense' WHERE JobId = @j;", ("@j", job.JobId)));
    }

    [Fact]
    public async Task Member_emails_must_be_stored_lower_case_so_one_person_cannot_be_two_members()
    {
        var job = await NewJobAsync();
        await Assert.ThrowsAsync<SqlException>(() =>
            db.ExecAsync("INSERT INTO ductwork.JobMember (JobId, UserEmail, Role, AddedBy) VALUES (@j, 'Mixed@Example.com', 'Viewer', 'x');", ("@j", job.JobId)));
    }
}
