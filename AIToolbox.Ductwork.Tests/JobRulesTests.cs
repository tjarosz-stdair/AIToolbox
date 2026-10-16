using System.Reflection;
using AIToolbox.Ductwork.Core.Jobs;

namespace AIToolbox.Ductwork.Tests;

public class JobRulesTests
{
    private static readonly CompletionFacts Ready = new(AllStepsDone: true, ActiveProcessingTasks: 0, UnconfirmedMultipliers: 0);

    // ---- lifecycle

    [Theory]
    [InlineData(JobState.Draft, true)]
    [InlineData(JobState.InReview, true)]
    [InlineData(JobState.Completing, false)]
    [InlineData(JobState.Complete, false)]
    [InlineData(JobState.Closed, false)]
    [InlineData(JobState.Archiving, false)]
    [InlineData(JobState.Archived, false)]
    [InlineData(JobState.Restoring, false)]
    public void Only_a_working_job_is_editable(JobState state, bool editable) =>
        Assert.Equal(editable, JobLifecycle.IsEditable(state));

    [Fact]
    public void Completing_needs_every_step_done_no_running_tasks_and_confirmed_multipliers()
    {
        Assert.True(JobLifecycle.CanBeginComplete(JobState.Draft, Ready).Allowed);
        Assert.True(JobLifecycle.CanBeginComplete(JobState.InReview, Ready).Allowed);

        var steps = JobLifecycle.CanBeginComplete(JobState.Draft, Ready with { AllStepsDone = false });
        Assert.False(steps.Allowed);
        Assert.Contains("close it instead", steps.Reason);

        var tasks = JobLifecycle.CanBeginComplete(JobState.Draft, Ready with { ActiveProcessingTasks = 2 });
        Assert.False(tasks.Allowed);
        Assert.Contains("2 processing task", tasks.Reason);

        var multipliers = JobLifecycle.CanBeginComplete(JobState.Draft, Ready with { UnconfirmedMultipliers = 1 });
        Assert.False(multipliers.Allowed);
        Assert.Contains("multiplier", multipliers.Reason);
    }

    [Theory]
    [InlineData(JobState.Completing)]
    [InlineData(JobState.Complete)]
    [InlineData(JobState.Closed)]
    [InlineData(JobState.Archived)]
    public void A_job_that_is_not_working_cannot_begin_completing(JobState state) =>
        Assert.False(JobLifecycle.CanBeginComplete(state, Ready).Allowed);

    [Fact]
    public void Only_a_completing_job_can_be_marked_complete()
    {
        Assert.True(JobLifecycle.CanFinishComplete(JobState.Completing).Allowed);
        Assert.False(JobLifecycle.CanFinishComplete(JobState.Draft).Allowed);
        Assert.False(JobLifecycle.CanFinishComplete(JobState.Complete).Allowed);
    }

    [Fact]
    public void Closing_needs_a_reason_and_no_running_tasks_but_not_finished_steps()
    {
        var unfinished = new CompletionFacts(false, 0, 0);
        Assert.True(JobLifecycle.CanClose(JobState.Draft, "Lost the bid", unfinished).Allowed);
        Assert.False(JobLifecycle.CanClose(JobState.Draft, "  ", unfinished).Allowed);
        Assert.False(JobLifecycle.CanClose(JobState.Draft, null, unfinished).Allowed);
        Assert.False(JobLifecycle.CanClose(JobState.Draft, "x", unfinished with { ActiveProcessingTasks = 1 }).Allowed);
        Assert.False(JobLifecycle.CanClose(JobState.Complete, "x", unfinished).Allowed);
    }

    [Fact]
    public void Reopening_needs_a_reason_and_a_complete_or_closed_job()
    {
        Assert.True(JobLifecycle.CanReopen(JobState.Complete, "Addendum received").Allowed);
        Assert.True(JobLifecycle.CanReopen(JobState.Closed, "Bid is live again").Allowed);
        Assert.False(JobLifecycle.CanReopen(JobState.Complete, "").Allowed);
        Assert.False(JobLifecycle.CanReopen(JobState.Draft, "x").Allowed);
        Assert.Contains("restored", JobLifecycle.CanReopen(JobState.Archived, "x").Reason);
    }

    [Theory]
    [InlineData(JobState.Draft, true)]
    [InlineData(JobState.Complete, true)]
    [InlineData(JobState.Closed, true)]
    [InlineData(JobState.Archived, true)]
    [InlineData(JobState.Completing, false)]
    [InlineData(JobState.Archiving, false)]
    [InlineData(JobState.Restoring, false)]
    public void Metadata_can_change_on_finished_jobs_but_not_while_they_are_being_held_still(JobState state, bool allowed) =>
        Assert.Equal(allowed, JobLifecycle.AllowsMetadataChange(state));

    // ---- permissions

    [Theory]
    [InlineData(JobRole.Viewer, JobCapability.View, true)]
    [InlineData(JobRole.Viewer, JobCapability.ReadAudit, true)]
    [InlineData(JobRole.Viewer, JobCapability.Edit, false)]
    [InlineData(JobRole.Viewer, JobCapability.Tag, false)]
    [InlineData(JobRole.Editor, JobCapability.Edit, true)]
    [InlineData(JobRole.Editor, JobCapability.Complete, true)]
    [InlineData(JobRole.Editor, JobCapability.Close, true)]
    [InlineData(JobRole.Editor, JobCapability.Reopen, true)]
    [InlineData(JobRole.Editor, JobCapability.Tag, true)]
    [InlineData(JobRole.Editor, JobCapability.ManageMembers, false)]
    [InlineData(JobRole.Editor, JobCapability.Archive, false)]
    [InlineData(JobRole.Owner, JobCapability.ManageMembers, true)]
    [InlineData(JobRole.Owner, JobCapability.Archive, true)]
    [InlineData(JobRole.Owner, JobCapability.Restore, true)]
    public void Roles_map_to_capabilities(JobRole role, JobCapability capability, bool allowed) =>
        Assert.Equal(allowed, JobPermissions.Can(role, isJobManager: false, capability));

    [Fact]
    public void A_job_manager_can_archive_and_restore_any_job_but_sees_and_edits_nothing()
    {
        foreach (var capability in Enum.GetValues<JobCapability>())
        {
            var expected = capability is JobCapability.Archive or JobCapability.Restore;
            Assert.Equal(expected, JobPermissions.Can(null, isJobManager: true, capability));
        }
    }

    [Fact]
    public void A_non_member_who_is_not_a_job_manager_can_do_nothing()
    {
        foreach (var capability in Enum.GetValues<JobCapability>())
            Assert.False(JobPermissions.Can(null, false, capability));
    }

    // ---- audit hashing

    private static AuditEvent Event(Action<AuditEventBuilder>? change = null)
    {
        var b = new AuditEventBuilder();
        change?.Invoke(b);
        return b.Build();
    }

    private sealed class AuditEventBuilder
    {
        public string Summary = "Did a thing";
        public string? Reason = null;
        public long EventId = 1;
        public long StateVersion = 1;
        public string? ActorEmail = "a@example.com";
        public string? BeforeJson = null;

        public AuditEvent Build() => new()
        {
            JobId = 7, EventId = EventId, OccurredUtc = new DateTime(2026, 10, 16, 9, 0, 0, DateTimeKind.Utc).AddTicks(1234567),
            ActorType = ActorType.User, ActorEmail = ActorEmail, Category = "lifecycle", Action = "job.created",
            Summary = Summary, Reason = Reason, BeforeJson = BeforeJson, StateVersion = StateVersion,
            PrevHash = AuditHasher.Genesis, Hash = ""
        };
    }

    [Fact]
    public void The_same_event_always_hashes_the_same_and_the_hash_is_64_hex_characters()
    {
        var h = AuditHasher.Hash(AuditHasher.Genesis, Event());
        Assert.Equal(h, AuditHasher.Hash(AuditHasher.Genesis, Event()));
        Assert.Matches("^[0-9a-f]{64}$", h);
    }

    [Fact]
    public void Changing_any_field_or_the_previous_hash_changes_the_hash()
    {
        var baseline = AuditHasher.Hash(AuditHasher.Genesis, Event());
        Assert.NotEqual(baseline, AuditHasher.Hash(AuditHasher.Genesis, Event(b => b.Summary = "Did another thing")));
        Assert.NotEqual(baseline, AuditHasher.Hash(AuditHasher.Genesis, Event(b => b.Reason = "because")));
        Assert.NotEqual(baseline, AuditHasher.Hash(AuditHasher.Genesis, Event(b => b.EventId = 2)));
        Assert.NotEqual(baseline, AuditHasher.Hash(AuditHasher.Genesis, Event(b => b.StateVersion = 2)));
        Assert.NotEqual(baseline, AuditHasher.Hash(AuditHasher.Genesis, Event(b => b.ActorEmail = "b@example.com")));
        Assert.NotEqual(baseline, AuditHasher.Hash(new string('1', 64), Event()));
    }

    [Fact]
    public void A_missing_value_and_an_empty_value_are_different_events()
    {
        Assert.NotEqual(
            AuditHasher.Hash(AuditHasher.Genesis, Event(b => b.Reason = null)),
            AuditHasher.Hash(AuditHasher.Genesis, Event(b => b.Reason = "")));
    }

    [Fact]
    public void Sub_second_time_differences_are_part_of_the_hash()
    {
        var a = Event();
        var b = a with { OccurredUtc = a.OccurredUtc.AddTicks(1) };
        Assert.NotEqual(AuditHasher.Hash(AuditHasher.Genesis, a), AuditHasher.Hash(AuditHasher.Genesis, b));
    }

    // ---- context

    [Fact]
    public void A_user_action_must_name_the_user_and_emails_are_normalized()
    {
        Assert.Throws<ArgumentException>(() => new AuditContext { ActorType = ActorType.User }.EnsureValid());
        Assert.Equal("a@example.com", AuditContext.ForUser("  A@Example.COM ").ActorEmail);
        AuditContext.ForSystem("importer").EnsureValid();
    }

    // ---- architecture: no mutation without an audit context

    private static readonly string[] ReadOnlyPrefixes = ["Get", "List", "Verify"];

    private static readonly Type[] DataLayer =
        [typeof(JobRepository), typeof(EditLockService), typeof(AIToolbox.Ductwork.Core.Tasks.ProcessingTaskRepository),
         typeof(AIToolbox.Ductwork.Core.Tasks.TaskBindingRepository), typeof(AIToolbox.Ductwork.Core.Counting.BuildingTypeRepository),
         typeof(AIToolbox.Ductwork.Core.Counting.TaskResultIngestor), typeof(AIToolbox.Ductwork.Core.Activity.ActivityRepository),
         typeof(AIToolbox.Ductwork.Core.Drawings.UploadService), typeof(AIToolbox.Ductwork.Core.Drawings.SheetRepository),
         typeof(AIToolbox.Ductwork.Core.Counting.EntityReviewRepository)];

    private static IEnumerable<(Type Type, MethodInfo Method)> PublicMethods() =>
        DataLayer.SelectMany(type => type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => (type, m)));

    [Fact]
    public void Every_public_method_that_changes_data_takes_an_audit_context_or_says_why_it_does_not()
    {
        var offenders = PublicMethods()
            .Where(x => !ReadOnlyPrefixes.Any(p => x.Method.Name.StartsWith(p, StringComparison.Ordinal)))
            .Where(x => x.Method.GetCustomAttribute<NoAuditAttribute>() is null)
            .Where(x => x.Method.GetParameters().All(p => p.ParameterType != typeof(AuditContext)))
            .Select(x => $"{x.Type.Name}.{x.Method.Name}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Read_only_methods_do_not_take_an_audit_context_so_the_naming_rule_is_honest()
    {
        var mislabelled = PublicMethods()
            .Where(x => ReadOnlyPrefixes.Any(p => x.Method.Name.StartsWith(p, StringComparison.Ordinal)))
            .Where(x => x.Method.GetParameters().Any(p => p.ParameterType == typeof(AuditContext)))
            .Select(x => $"{x.Type.Name}.{x.Method.Name}")
            .ToList();

        Assert.Empty(mislabelled);
    }

    [Fact]
    public void The_only_data_changes_without_an_audit_event_are_liveness_bookkeeping_and_each_says_why()
    {
        var exempt = PublicMethods().Where(x => x.Method.GetCustomAttribute<NoAuditAttribute>() is not null).ToList();

        Assert.Equal(["ActivityRepository.EndSessionAsync", "ActivityRepository.RecordHeartbeatAsync", "EditLockService.HeartbeatAsync", "ProcessingTaskRepository.HeartbeatAsync", "UploadService.AppendAsync"],
            exempt.Select(x => $"{x.Type.Name}.{x.Method.Name}").Order());
        Assert.All(exempt, x => Assert.False(string.IsNullOrWhiteSpace(x.Method.GetCustomAttribute<NoAuditAttribute>()!.Reason)));
    }
}
