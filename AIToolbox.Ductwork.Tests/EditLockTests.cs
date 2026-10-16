using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Jobs;

namespace AIToolbox.Ductwork.Tests;

public class EditLockTests(SqlFixture db) : IClassFixture<SqlFixture>
{
    private const string Owner = "owner@example.com";
    private const string Editor = "editor@example.com";
    private const string Viewer = "viewer@example.com";

    // Short timings, so expiry and takeover timeouts can be tested for real without long waits.
    private static readonly LockSettings Quick = new()
    {
        Expiry = TimeSpan.FromMilliseconds(700),
        TakeoverTimeout = TimeSpan.FromMilliseconds(500),
        HeartbeatInterval = TimeSpan.FromMilliseconds(100)
    };

    private EditLockService Locks(LockSettings? settings = null) => new(db.ConnectionString, settings);
    private JobRepository Repo() => new(db.ConnectionString);

    private static AuditContext Ctx(string email, string? session = null, bool admin = false) =>
        AuditContext.ForUser(email, displayName: email.Split('@')[0], sessionId: session ?? "s-" + email, clientIp: "10.0.0.2", userAgent: "tests", isAdmin: admin);

    private async Task<JobRecord> NewJobAsync()
    {
        var repo = Repo();
        var job = await repo.CreateAsync(new NewJob { Name = "Lock job " + Guid.NewGuid().ToString("N")[..8] }, Ctx(Owner));
        await repo.AddMemberAsync(job.JobId, Editor, JobRole.Editor, Ctx(Owner));
        await repo.AddMemberAsync(job.JobId, Viewer, JobRole.Viewer, Ctx(Owner));
        return job;
    }

    private async Task<IReadOnlyList<string>> ActionsAsync(long jobId) => (await Repo().ListAuditAsync(jobId)).Select(e => e.Action).ToList();

    // ---- acquiring

    [Fact]
    public async Task A_free_lock_is_taken_and_the_same_session_asking_again_gets_the_same_token()
    {
        var job = await NewJobAsync();
        var first = await Locks().AcquireAsync(job.JobId, Ctx(Editor));
        var again = await Locks().AcquireAsync(job.JobId, Ctx(Editor));

        Assert.Equal(LockOutcome.Acquired, first.Outcome);
        Assert.Equal(LockOutcome.AlreadyYours, again.Outcome);
        Assert.Equal(first.Token!.FencingToken, again.Token!.FencingToken);
        Assert.Contains("lock.acquired", await ActionsAsync(job.JobId));
        Assert.Single(await ActionsAsync(job.JobId), a => a == "lock.acquired");   // asking again wrote nothing
    }

    [Fact]
    public async Task A_second_tab_of_the_same_person_does_not_get_the_lock()
    {
        var job = await NewJobAsync();
        var tab1 = await Locks().AcquireAsync(job.JobId, Ctx(Editor, session: "tab-1"));
        var tab2 = await Locks().AcquireAsync(job.JobId, Ctx(Editor, session: "tab-2"));

        Assert.Equal(LockOutcome.Acquired, tab1.Outcome);
        Assert.Equal(LockOutcome.HeldByOther, tab2.Outcome);
        Assert.Null(tab2.Token);
        Assert.Equal("tab-1", tab2.Holder!.HolderSessionId);
    }

    [Fact]
    public async Task Only_people_who_can_edit_can_take_the_lock_and_only_on_a_job_that_can_be_edited()
    {
        var job = await NewJobAsync();
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => Locks().AcquireAsync(job.JobId, Ctx(Viewer)));
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => Locks().AcquireAsync(job.JobId, Ctx("stranger@example.com")));
        await Assert.ThrowsAsync<ArgumentException>(() => Locks().AcquireAsync(job.JobId, AuditContext.ForUser(Editor)));   // no session

        await Repo().CloseAsync(job.JobId, "Lost", Ctx(Owner));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Locks().AcquireAsync(job.JobId, Ctx(Editor)));
    }

    [Fact]
    public async Task Status_shows_the_holder_and_gives_the_holders_session_its_token()
    {
        var job = await NewJobAsync();
        var mine = await Locks().AcquireAsync(job.JobId, Ctx(Editor));

        var holderView = await Locks().GetStatusAsync(job.JobId, "s-" + Editor);
        var otherView = await Locks().GetStatusAsync(job.JobId, "s-" + Owner);

        Assert.True(holderView.YouHoldIt);
        Assert.Equal(mine.Token!.FencingToken, holderView.YourToken!.FencingToken);
        Assert.False(otherView.YouHoldIt);
        Assert.Equal(Editor, otherView.Lock!.HolderEmail);
        Assert.False(otherView.Lock.IsExpired);
    }

    [Fact]
    public async Task Many_sessions_racing_for_one_job_produce_exactly_one_holder()
    {
        var job = await NewJobAsync();
        for (var i = 0; i < 7; i++) await Repo().AddMemberAsync(job.JobId, $"racer{i}@example.com", JobRole.Editor, Ctx(Owner));

        var results = await Task.WhenAll(Enumerable.Range(0, 7).Select(i => Locks().AcquireAsync(job.JobId, Ctx($"racer{i}@example.com"))));

        Assert.Single(results, r => r.Outcome == LockOutcome.Acquired);
        Assert.Equal(6, results.Count(r => r.Outcome == LockOutcome.HeldByOther));
        Assert.True((await Repo().VerifyAuditChainAsync(job.JobId)).IsValid);
    }

    [Fact]
    public async Task Fencing_tokens_only_ever_increase_even_across_a_release()
    {
        var job = await NewJobAsync();
        var a = (await Locks().AcquireAsync(job.JobId, Ctx(Editor))).Token!;
        await Locks().ReleaseAsync(a, Ctx(Editor));
        var b = (await Locks().AcquireAsync(job.JobId, Ctx(Owner))).Token!;

        Assert.True(b.FencingToken > a.FencingToken);
    }

    // ---- expiry and heartbeat

    [Fact]
    public async Task A_lock_that_stops_heartbeating_expires_and_the_next_session_takes_it_and_is_logged()
    {
        var job = await NewJobAsync();
        var locks = Locks(Quick);
        var old = (await locks.AcquireAsync(job.JobId, Ctx(Editor))).Token!;

        await Task.Delay(1000);
        var taken = await locks.AcquireAsync(job.JobId, Ctx(Owner));

        Assert.Equal(LockOutcome.Acquired, taken.Outcome);
        Assert.True(taken.Token!.FencingToken > old.FencingToken);
        Assert.Contains("lock.acquired-after-expiry", await ActionsAsync(job.JobId));

        var beat = await locks.HeartbeatAsync(old);
        Assert.False(beat.StillHolder);
        Assert.Contains("taken over by owner", beat.LostMessage);
    }

    [Fact]
    public async Task Heartbeats_keep_a_lock_alive_past_its_expiry_and_write_no_audit_events()
    {
        var job = await NewJobAsync();
        var locks = Locks(Quick);
        var token = (await locks.AcquireAsync(job.JobId, Ctx(Editor))).Token!;
        var eventsBefore = (await Repo().ListAuditAsync(job.JobId)).Count;

        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(250);
            Assert.True((await locks.HeartbeatAsync(token)).StillHolder);
        }

        Assert.Equal(LockOutcome.HeldByOther, (await locks.AcquireAsync(job.JobId, Ctx(Owner))).Outcome);   // still live after 1.5 s
        Assert.Equal(eventsBefore, (await Repo().ListAuditAsync(job.JobId)).Count);
    }

    [Fact]
    public async Task A_heartbeat_with_a_token_that_was_replaced_is_refused_and_the_new_holders_is_not_disturbed()
    {
        var job = await NewJobAsync();
        var old = (await Locks().AcquireAsync(job.JobId, Ctx(Editor))).Token!;
        await Locks().ReleaseAsync(old, Ctx(Editor));
        var current = (await Locks().AcquireAsync(job.JobId, Ctx(Owner))).Token!;

        var stale = await Locks().HeartbeatAsync(old);
        Assert.False(stale.StillHolder);
        Assert.NotNull(stale.LostMessage);
        Assert.True((await Locks().HeartbeatAsync(current)).StillHolder);
    }

    [Fact]
    public async Task Releasing_frees_the_lock_and_a_stale_release_does_nothing()
    {
        var job = await NewJobAsync();
        var a = (await Locks().AcquireAsync(job.JobId, Ctx(Editor))).Token!;

        Assert.True(await Locks().ReleaseAsync(a, Ctx(Editor)));
        Assert.False(await Locks().ReleaseAsync(a, Ctx(Editor)));                         // already released
        var b = (await Locks().AcquireAsync(job.JobId, Ctx(Owner))).Token!;
        Assert.False(await Locks().ReleaseAsync(a, Ctx(Editor)));                         // the old token cannot release someone else's lock
        Assert.True((await Locks().GetStatusAsync(job.JobId, "s-" + Owner)).YouHoldIt);
        Assert.Equal(1, (await ActionsAsync(job.JobId)).Count(x => x == "lock.released"));
        Assert.NotNull(b);
    }

    // ---- fencing on writes

    [Fact]
    public async Task A_late_write_from_a_session_that_was_taken_over_is_refused_and_the_refusal_is_logged()
    {
        var job = await NewJobAsync();
        var locks = Locks(Quick);
        var repo = Repo();
        var oldToken = (await locks.AcquireAsync(job.JobId, Ctx(Editor))).Token!;
        await Task.Delay(1000);
        var newToken = (await locks.AcquireAsync(job.JobId, Ctx(Owner))).Token!;
        var version = (await repo.GetAsync(job.JobId))!.StateVersion;

        var stale = await Assert.ThrowsAsync<StaleEditTokenException>(() =>
            repo.UpdateDetailsAsync(job.JobId, new JobDetails { Name = "late write" }, version, oldToken, Ctx(Editor)));
        Assert.Contains("taken over by owner", stale.Message);

        Assert.NotEqual("late write", (await repo.GetAsync(job.JobId))!.Name);
        Assert.Contains("write.rejected", await ActionsAsync(job.JobId));

        var ok = await repo.UpdateDetailsAsync(job.JobId, new JobDetails { Name = "good write" }, version, newToken, Ctx(Owner));
        Assert.Equal("good write", ok.Name);
        Assert.True((await repo.VerifyAuditChainAsync(job.JobId)).IsValid);
    }

    [Fact]
    public async Task A_token_is_only_good_for_the_session_and_job_it_was_issued_to_and_only_while_unexpired()
    {
        var job = await NewJobAsync();
        var other = await NewJobAsync();
        var locks = Locks(Quick);
        var repo = Repo();
        var token = (await locks.AcquireAsync(job.JobId, Ctx(Editor))).Token!;
        var otherJobToken = (await locks.AcquireAsync(other.JobId, Ctx(Editor))).Token!;

        // Someone else's session presenting the token.
        await Assert.ThrowsAsync<StaleEditTokenException>(() =>
            repo.UpdateDetailsAsync(job.JobId, new JobDetails { Name = "x" }, 1, token, Ctx(Owner)));
        // The right session, but a token for another job.
        await Assert.ThrowsAsync<StaleEditTokenException>(() =>
            repo.UpdateDetailsAsync(job.JobId, new JobDetails { Name = "x" }, 1, otherJobToken, Ctx(Editor)));

        await Task.Delay(1000);
        var expired = await Assert.ThrowsAsync<StaleEditTokenException>(() =>
            repo.UpdateDetailsAsync(job.JobId, new JobDetails { Name = "x" }, 1, token, Ctx(Editor)));
        Assert.Contains("expired", expired.Message);
    }

    [Fact]
    public async Task Without_permission_nothing_about_the_lock_is_revealed()
    {
        var job = await NewJobAsync();
        var token = (await Locks().AcquireAsync(job.JobId, Ctx(Editor))).Token!;

        // A viewer using the editor's session id is told they may not, not told who is editing.
        await Assert.ThrowsAsync<JobAccessDeniedException>(() =>
            Repo().UpdateDetailsAsync(job.JobId, new JobDetails { Name = "x" }, 1, token, Ctx(Viewer, session: "s-" + Editor)));
    }

    // ---- takeover

    [Fact]
    public async Task Taking_over_from_an_expired_session_is_immediate_and_needs_no_prompt()
    {
        var job = await NewJobAsync();
        var locks = Locks(Quick);
        await locks.AcquireAsync(job.JobId, Ctx(Editor));
        await Task.Delay(1000);

        var result = await locks.RequestTakeoverAsync(job.JobId, Ctx(Owner));

        Assert.True(result.Granted);
        Assert.NotNull(result.Token);
        Assert.Contains("takeover.stale", await ActionsAsync(job.JobId));
    }

    [Fact]
    public async Task A_live_editor_is_asked_and_can_hand_over_immediately()
    {
        var job = await NewJobAsync();
        var locks = Locks();
        var holder = (await locks.AcquireAsync(job.JobId, Ctx(Editor))).Token!;

        var request = await locks.RequestTakeoverAsync(job.JobId, Ctx(Owner));
        Assert.False(request.Granted);
        Assert.NotNull(request.Request);

        var beat = await locks.HeartbeatAsync(holder);
        Assert.True(beat.StillHolder);
        Assert.Equal(request.Request!.PreemptRequestId, beat.PendingRequest!.PreemptRequestId);   // the holder learns within one heartbeat

        var response = await locks.RespondToTakeoverAsync(holder, request.Request.PreemptRequestId, handOver: true, Ctx(Editor));
        Assert.True(response.Granted);

        var status = await locks.GetStatusAsync(job.JobId, "s-" + Owner);
        Assert.True(status.YouHoldIt);
        Assert.Null(status.PendingRequest);
        Assert.False((await locks.HeartbeatAsync(holder)).StillHolder);
        Assert.Contains("takeover.requested", await ActionsAsync(job.JobId));
        Assert.Contains("takeover.granted", await ActionsAsync(job.JobId));
    }

    [Fact]
    public async Task The_holder_can_decline_and_keep_editing_and_the_requester_can_ask_again_later()
    {
        var job = await NewJobAsync();
        var locks = Locks();
        var holder = (await locks.AcquireAsync(job.JobId, Ctx(Editor))).Token!;
        var first = await locks.RequestTakeoverAsync(job.JobId, Ctx(Owner));

        var declined = await locks.RespondToTakeoverAsync(holder, first.Request!.PreemptRequestId, handOver: false, Ctx(Editor));

        Assert.False(declined.Granted);
        Assert.True((await locks.HeartbeatAsync(holder)).StillHolder);
        Assert.Contains("takeover.denied", await ActionsAsync(job.JobId));
        var second = await locks.RequestTakeoverAsync(job.JobId, Ctx(Owner));
        Assert.NotEqual(first.Request.PreemptRequestId, second.Request!.PreemptRequestId);
    }

    [Fact]
    public async Task The_requester_can_withdraw_and_only_the_requester_can()
    {
        var job = await NewJobAsync();
        var locks = Locks();
        await locks.AcquireAsync(job.JobId, Ctx(Editor));
        var request = (await locks.RequestTakeoverAsync(job.JobId, Ctx(Owner))).Request!;

        Assert.False(await locks.CancelTakeoverAsync(job.JobId, request.PreemptRequestId, Ctx(Editor)));
        Assert.True(await locks.CancelTakeoverAsync(job.JobId, request.PreemptRequestId, Ctx(Owner)));
        Assert.False(await locks.CancelTakeoverAsync(job.JobId, request.PreemptRequestId, Ctx(Owner)));
        Assert.Null((await locks.GetStatusAsync(job.JobId, "s-" + Owner)).PendingRequest);
    }

    [Fact]
    public async Task If_the_holder_does_not_answer_in_time_the_request_is_granted()
    {
        var job = await NewJobAsync();
        var locks = Locks(new LockSettings { Expiry = TimeSpan.FromSeconds(30), TakeoverTimeout = TimeSpan.FromMilliseconds(400) });
        var holder = (await locks.AcquireAsync(job.JobId, Ctx(Editor))).Token!;
        await locks.RequestTakeoverAsync(job.JobId, Ctx(Owner));

        Assert.Null(await locks.AdvanceAsync(job.JobId, AuditContext.ForSystem("timer")));   // too early
        await Task.Delay(700);
        var granted = await locks.AdvanceAsync(job.JobId, AuditContext.ForSystem("timer"));

        Assert.Equal(PreemptStatus.Granted, granted!.Status);
        Assert.True((await locks.GetStatusAsync(job.JobId, "s-" + Owner)).YouHoldIt);
        Assert.False((await locks.HeartbeatAsync(holder)).StillHolder);   // an unattended tab cannot block the job forever
        Assert.Null(await locks.AdvanceAsync(job.JobId, AuditContext.ForSystem("timer")));   // nothing left to do
    }

    [Fact]
    public async Task Only_one_takeover_request_can_wait_at_a_time()
    {
        var job = await NewJobAsync();
        await Repo().AddMemberAsync(job.JobId, "third@example.com", JobRole.Editor, Ctx(Owner));
        var locks = Locks();
        await locks.AcquireAsync(job.JobId, Ctx(Editor));
        await locks.RequestTakeoverAsync(job.JobId, Ctx(Owner));

        var again = await locks.RequestTakeoverAsync(job.JobId, Ctx(Owner));            // the same person asking again just sees theirs
        Assert.False(again.Granted);
        await Assert.ThrowsAsync<JobRuleViolationException>(() => locks.RequestTakeoverAsync(job.JobId, Ctx("third@example.com")));
    }

    [Fact]
    public async Task Only_the_current_holder_can_answer_a_request()
    {
        var job = await NewJobAsync();
        var locks = Locks();
        var holder = (await locks.AcquireAsync(job.JobId, Ctx(Editor))).Token!;
        var request = (await locks.RequestTakeoverAsync(job.JobId, Ctx(Owner))).Request!;
        await locks.RespondToTakeoverAsync(holder, request.PreemptRequestId, handOver: true, Ctx(Editor));

        // The old holder no longer holds anything, so its token cannot answer, hand over again, or release.
        await Assert.ThrowsAsync<StaleEditTokenException>(() =>
            locks.RespondToTakeoverAsync(holder, request.PreemptRequestId, handOver: true, Ctx(Editor)));
    }

    // ---- force

    [Fact]
    public async Task An_owner_can_force_a_takeover_but_an_editor_cannot()
    {
        var job = await NewJobAsync();
        var locks = Locks();
        var editorsToken = (await locks.AcquireAsync(job.JobId, Ctx(Editor))).Token!;
        await locks.RequestTakeoverAsync(job.JobId, Ctx(Owner));

        await Assert.ThrowsAsync<JobAccessDeniedException>(() => locks.ForceTakeoverAsync(job.JobId, Ctx(Viewer)));
        var forced = await locks.ForceTakeoverAsync(job.JobId, Ctx(Owner));

        Assert.NotNull(forced);
        Assert.False((await locks.HeartbeatAsync(editorsToken)).StillHolder);
        Assert.Null((await locks.GetStatusAsync(job.JobId, "s-" + Owner)).PendingRequest);   // a waiting request is closed
        Assert.Contains("takeover.forced", await ActionsAsync(job.JobId));
    }

    [Fact]
    public async Task An_administrator_who_cannot_edit_the_job_only_frees_the_lock()
    {
        var job = await NewJobAsync();
        var locks = Locks();
        var token = (await locks.AcquireAsync(job.JobId, Ctx(Editor))).Token!;

        var result = await locks.ForceTakeoverAsync(job.JobId, Ctx("admin@example.com", admin: true));

        Assert.Null(result);
        Assert.False((await locks.HeartbeatAsync(token)).StillHolder);
        Assert.Null((await locks.GetStatusAsync(job.JobId, "s-x")).Lock);
    }

    // ---- completing needs the lock

    [Fact]
    public async Task Completing_and_closing_are_refused_while_someone_else_is_actively_editing()
    {
        var job = await NewJobAsync();
        var repo = Repo();
        var locks = Locks();
        await locks.AcquireAsync(job.JobId, Ctx(Owner));

        var closing = await Assert.ThrowsAsync<JobLockedException>(() => repo.CloseAsync(job.JobId, "reason", Ctx(Editor)));
        Assert.Contains("owner", closing.Message);
        await Assert.ThrowsAsync<JobLockedException>(() => repo.BeginCompleteAsync(job.JobId, Ctx(Editor)));

        // The holder themselves may close, and a free lock does not block anyone.
        await repo.CloseAsync(job.JobId, "reason", Ctx(Owner));
        Assert.Equal(JobState.Closed, (await repo.GetAsync(job.JobId))!.State);
    }

    [Fact]
    public async Task A_viewer_asking_to_close_is_told_they_may_not_not_that_someone_is_editing()
    {
        var job = await NewJobAsync();
        await Locks().AcquireAsync(job.JobId, Ctx(Owner));
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => Repo().CloseAsync(job.JobId, "reason", Ctx(Viewer)));
    }

    // ---- structure

    [Fact]
    public async Task The_database_allows_one_lock_per_job_and_one_pending_request_per_job()
    {
        var job = await NewJobAsync();
        await Locks().AcquireAsync(job.JobId, Ctx(Editor));

        await Assert.ThrowsAnyAsync<Exception>(() => db.ExecAsync(
            "INSERT INTO ductwork.JobEditLock (JobId, HolderUserEmail, HolderSessionId, FencingToken, AcquiredUtc, LastHeartbeatUtc, ExpiresUtc) " +
            "VALUES (@j, 'x@example.com', 's', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME());", ("@j", job.JobId)));

        const string insert = "INSERT INTO ductwork.PreemptRequest (JobId, RequesterEmail, RequesterSessionId, RequestedUtc, ExpiresUtc) " +
                              "VALUES (@j, 'x@example.com', 's', SYSUTCDATETIME(), SYSUTCDATETIME());";
        await db.ExecAsync(insert, ("@j", job.JobId));
        await Assert.ThrowsAnyAsync<Exception>(() => db.ExecAsync(insert, ("@j", job.JobId)));
    }
}
