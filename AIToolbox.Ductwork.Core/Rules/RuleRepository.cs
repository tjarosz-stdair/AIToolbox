using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Counting;
using AIToolbox.Ductwork.Core.Data;
using AIToolbox.Ductwork.Core.Jobs;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Rules;

public enum RuleScope { Job, BuildingType, Sheet, Instance }

public enum RuleStatus { Proposed, Approved, Retired }

public sealed record NewRule
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public RuleScope Scope { get; init; } = RuleScope.Job;
    public long? BuildingTypeId { get; init; }
    public long? SheetId { get; init; }
    public RuleMatch Match { get; init; } = new();
    public required RuleAction Action { get; init; }
    /// <summary>The finding this rule was written to answer, if any.</summary>
    public long? FromFindingId { get; init; }
}

public sealed record RuleRecord
{
    public long RuleId { get; init; }
    public Guid PublicId { get; init; }
    public Guid RuleKey { get; init; }
    public int Version { get; init; }
    public RuleScope Scope { get; init; }
    public long? JobId { get; init; }
    public long? BuildingTypeId { get; init; }
    public long? SheetId { get; init; }
    public long OriginJobId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required RuleMatch Match { get; init; }
    public required RuleAction Action { get; init; }
    public RuleStatus Status { get; init; }
    public long? FromFindingId { get; init; }
    public required string CreatedBy { get; init; }
    public DateTime CreatedUtc { get; init; }
    public string? ApprovedBy { get; init; }
    public DateTime? ApprovedUtc { get; init; }
    public string? RetiredBy { get; init; }
    public DateTime? RetiredUtc { get; init; }
    public string? RetireReason { get; init; }
}

public sealed record RulePreviewItem(Guid EntityPublicId, long EntityId, string Item, string Change);

public sealed record RuleSkip(Guid EntityPublicId, string Item, string Reason);

/// <summary>What a rule would do on a job, before anything is changed.</summary>
public sealed record RulePreview(IReadOnlyList<RulePreviewItem> Changes, IReadOnlyList<RuleSkip> Skipped, IReadOnlyList<RuleSkip> Conflicts);

public sealed record RuleApplyResult(Guid BatchId, int Changed, int Skipped, int Conflicts, int FindingsRaised);

public sealed record RuleUndoResult(int Undone, int Skipped);

/// <summary>
/// Interpretation rules stored as data (plan section 6.11). A rule has a scope (this job, one building type, one sheet, or
/// every job), a deterministic match and a deterministic action, and its content never changes: a change is a new version.
/// Only a person writes, approves, applies or retires one, and only an approved rule changes a count. Applying a rule is
/// previewed first, recorded item by item, and can be undone. Precedence is an item override, then a job rule, a
/// building-type rule, a sheet rule and an instance rule: a stronger decision is never overwritten by a weaker one, and two
/// rules of the same strength that disagree become a finding instead of a silent winner.
/// </summary>
public sealed class RuleRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly Func<CancellationToken, Task<SqlConnection>> _open;

    public RuleRepository(Func<CancellationToken, Task<SqlConnection>> openConnection) => _open = openConnection;

    /// <summary>Lower is stronger. A job rule beats a building-type rule, which beats a sheet rule, which beats an instance rule.</summary>
    internal static int Rank(RuleScope scope) => scope switch
    {
        RuleScope.Job => 1,
        RuleScope.BuildingType => 2,
        RuleScope.Sheet => 3,
        _ => 4
    };

    // ------------------------------------------------------------------ reads

    /// <summary>Every version of the rules that apply to a job: its own, and the instance rules.</summary>
    public async Task<IReadOnlyList<RuleRecord>> ListForJobAsync(long jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return await ReadRulesAsync(connection, null, "WHERE r.Scope = 'Instance' OR r.JobId = @j ORDER BY r.CreatedUtc, r.RuleId", cancellationToken, ("@j", jobId));
    }

    public async Task<RuleRecord?> GetAsync(long ruleId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return (await ReadRulesAsync(connection, null, "WHERE r.RuleId = @id", cancellationToken, ("@id", ruleId))).SingleOrDefault();
    }

    /// <summary>A rule by public id, only if it applies to this job (its own, or an instance rule).</summary>
    public async Task<RuleRecord?> GetByPublicIdAsync(long jobId, Guid publicId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return (await ReadRulesAsync(connection, null, "WHERE r.PublicId = @p AND (r.Scope = 'Instance' OR r.JobId = @j)", cancellationToken, ("@p", publicId), ("@j", jobId))).SingleOrDefault();
    }

    /// <summary>What an approved or proposed rule would change on this job.</summary>
    public async Task<RulePreview> GetPreviewAsync(long jobId, long ruleId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        var rule = (await ReadRulesAsync(connection, null, "WHERE r.RuleId = @id", cancellationToken, ("@id", ruleId))).SingleOrDefault()
            ?? throw new KeyNotFoundException($"Rule {ruleId} does not exist.");
        RequireAppliesTo(rule, jobId);
        var computed = await ComputeAsync(connection, null, jobId, rule, cancellationToken);
        return computed.ToPreview();
    }

    /// <summary>What a rule that has not been saved yet would change, so a person can see the effect before writing it down.</summary>
    public async Task<RulePreview> GetDraftPreviewAsync(long jobId, NewRule draft, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        var rule = Draft(jobId, draft);
        Validate(rule);
        var computed = await ComputeAsync(connection, null, jobId, rule, cancellationToken);
        return computed.ToPreview();
    }

    // ------------------------------------------------------------------ writing

    public Task<RuleRecord> CreateAsync(long jobId, NewRule draft, EditToken editToken, AuditContext context, CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context, cancellationToken, async t =>
        {
            RequirePerson(context, "write a rule");
            t.RequireEditable();
            await RequireScopeAllowedAsync(t, draft.Scope);
            var rule = Draft(jobId, draft);
            Validate(rule);
            await RequireTargetsOnJobAsync(t, draft);
            if (draft.FromFindingId is { } findingId) await RequireFindingOnJobAsync(t, findingId);

            var created = await InsertAsync(t, rule with { RuleKey = Guid.NewGuid(), Version = 1 });
            await t.AuditAtCurrentVersionAsync(new AuditEntry("rules", "rule.created", $"Wrote the rule \"{created.Name}\" ({created.Scope.ToString().ToLowerInvariant()}), awaiting approval.",
                "Rule", created.PublicId.ToString("D"), null, Describe(created)));
            return created;
        }, editToken, requires: JobCapability.Edit);

    /// <summary>A change to a rule is a new version. It must be approved before it replaces the one in force, and the scope cannot change.</summary>
    public Task<RuleRecord> ReviseAsync(long jobId, long ruleId, NewRule draft, EditToken editToken, AuditContext context, CancellationToken cancellationToken = default) =>
        InRuleAsync(jobId, ruleId, context, cancellationToken, editToken, async (t, current) =>
        {
            RequirePerson(context, "revise a rule");
            t.RequireEditable();
            await RequireScopeAllowedAsync(t, current.Scope);
            if (draft.Scope != current.Scope || draft.BuildingTypeId != current.BuildingTypeId || draft.SheetId != current.SheetId)
                throw new JobRuleViolationException("A rule's scope cannot change. Write a new rule for a different scope.");

            var latest = await ScalarAsync(t, "SELECT MAX(Version) FROM ductwork.InterpretationRule WHERE RuleKey = @k;", ("@k", current.RuleKey));
            var next = (current with { Name = draft.Name.Trim(), Description = Clean(draft.Description, 1000), Match = draft.Match, Action = draft.Action, Version = (int)latest + 1, FromFindingId = draft.FromFindingId });
            Validate(next);
            var created = await InsertAsync(t, next with { RuleKey = current.RuleKey, CreatedBy = JobRepository.Actor(context) });
            await t.AuditAtCurrentVersionAsync(new AuditEntry("rules", "rule.revised", $"Wrote version {created.Version} of the rule \"{created.Name}\", awaiting approval.",
                "Rule", created.PublicId.ToString("D"), Describe(current), Describe(created)));
            return created;
        });

    /// <summary>Approving a rule puts it in force. An earlier approved version of the same rule is retired, so one version is in force at a time.</summary>
    public Task<RuleRecord> ApproveAsync(long jobId, long ruleId, EditToken editToken, AuditContext context, CancellationToken cancellationToken = default) =>
        InRuleAsync(jobId, ruleId, context, cancellationToken, editToken, async (t, current) =>
        {
            RequirePerson(context, "approve a rule");
            t.RequireEditable();
            await RequireScopeAllowedAsync(t, current.Scope);
            if (current.Status != RuleStatus.Proposed) throw new JobRuleViolationException($"This rule is {current.Status.ToString().ToLowerInvariant()}, so it cannot be approved.");

            var by = JobRepository.Actor(context);
            await JobRepository.ExecAsync(t.Connection, t.Transaction,
                """
                UPDATE ductwork.InterpretationRule
                SET Status = 'Retired', RetiredBy = @by, RetiredUtc = SYSUTCDATETIME(), RetireReason = @why
                WHERE RuleKey = @k AND Status = 'Approved' AND RuleId <> @id;
                """, t.Token, ("@by", by), ("@why", $"Superseded by version {current.Version}."), ("@k", current.RuleKey), ("@id", ruleId));
            await JobRepository.ExecAsync(t.Connection, t.Transaction,
                "UPDATE ductwork.InterpretationRule SET Status = 'Approved', ApprovedBy = @by, ApprovedUtc = SYSUTCDATETIME() WHERE RuleId = @id;", t.Token, ("@by", by), ("@id", ruleId));

            var approved = (await ReadRulesAsync(t.Connection, t.Transaction, "WHERE r.RuleId = @id", t.Token, ("@id", ruleId))).Single();
            await t.AuditAtCurrentVersionAsync(new AuditEntry("rules", "rule.approved", $"Approved the rule \"{approved.Name}\" (version {approved.Version}).",
                "Rule", approved.PublicId.ToString("D"), null, Describe(approved)));
            return approved;
        });

    /// <summary>Takes a rule out of force. Items it already changed keep their values and the record of which rule changed them.</summary>
    public Task<RuleRecord> RetireAsync(long jobId, long ruleId, string reason, EditToken editToken, AuditContext context, CancellationToken cancellationToken = default) =>
        InRuleAsync(jobId, ruleId, context, cancellationToken, editToken, async (t, current) =>
        {
            RequirePerson(context, "retire a rule");
            t.RequireEditable();
            await RequireScopeAllowedAsync(t, current.Scope);
            var why = Clean(reason, 400) ?? throw new JobRuleViolationException("Retiring a rule needs a reason.");
            if (current.Status == RuleStatus.Retired) return current;

            await JobRepository.ExecAsync(t.Connection, t.Transaction,
                "UPDATE ductwork.InterpretationRule SET Status = 'Retired', RetiredBy = @by, RetiredUtc = SYSUTCDATETIME(), RetireReason = @why WHERE RuleId = @id;", t.Token,
                ("@by", JobRepository.Actor(context)), ("@why", why), ("@id", ruleId));
            var retired = (await ReadRulesAsync(t.Connection, t.Transaction, "WHERE r.RuleId = @id", t.Token, ("@id", ruleId))).Single();
            await t.AuditAtCurrentVersionAsync(new AuditEntry("rules", "rule.retired", $"Retired the rule \"{retired.Name}\" (version {retired.Version}): {why}",
                "Rule", retired.PublicId.ToString("D"), Describe(current), Describe(retired)));
            return retired;
        });

    // ------------------------------------------------------------------ applying

    /// <summary>
    /// Applies an approved rule to the job's items. What it would do is what <see cref="GetPreviewAsync"/> shows. Items a person
    /// overrode, items a stronger rule decided, rejected items and hand-added items are left alone, and a rule of the same
    /// strength that has already decided an item raises a finding instead of overwriting it.
    /// </summary>
    public async Task<RuleApplyResult> ApplyAsync(long jobId, long ruleId, EditToken editToken, AuditContext context, CancellationToken cancellationToken = default)
    {
        RequirePerson(context, "apply a rule");
        long producerId;
        await using (var connection = await _open(cancellationToken))
            producerId = await ProducerStore.EnsureProducerAsync(connection, ProducerRef.Manual(context.ActorEmail!), cancellationToken);

        return await InRuleAsync(jobId, ruleId, context, cancellationToken, editToken, async (t, rule) =>
        {
            t.RequireEditable();
            if (rule.Status != RuleStatus.Approved) throw new JobRuleViolationException("Only an approved rule can change a count. Approve it first.");

            var computed = await ComputeAsync(t.Connection, t.Transaction, jobId, rule, t.Token);
            var batch = Guid.NewGuid();
            foreach (var change in computed.Changes)
            {
                var after = change.After;
                await EntityReviewRepository.ApplyRuleAsync(t, change.Entity, rule.RuleId, after.Type, after.Size, (decimal)after.Quantity, change.Text, batch);
            }

            var raised = 0;
            if (computed.Conflicts.Count > 0)
            {
                var taskId = await EntityReviewRepository.ManualTaskAsync(t, producerId, context.ActorEmail!);
                foreach (var conflict in computed.Conflicts)
                    raised += await RaiseConflictAsync(t, taskId, producerId, rule, conflict);
            }

            await t.AuditAtCurrentVersionAsync(new AuditEntry("rules", "rule.applied",
                $"Applied the rule \"{rule.Name}\" to {computed.Changes.Count} item(s); {computed.Skipped.Count} left alone, {computed.Conflicts.Count} in conflict.",
                "Rule", rule.PublicId.ToString("D"), null,
                JobRepository.Json(new() { ["batch"] = batch, ["changed"] = computed.Changes.Count, ["skipped"] = computed.Skipped.Count, ["conflicts"] = computed.Conflicts.Count, ["version"] = rule.Version })));
            return new RuleApplyResult(batch, computed.Changes.Count, computed.Skipped.Count, computed.Conflicts.Count, raised);
        });
    }

    /// <summary>
    /// Takes back what a rule changed on this job, for every item whose latest change is still the rule's. An item changed
    /// since (a person's edit, a later rule) is left as it is, because undoing under it would erase that decision.
    /// </summary>
    public Task<RuleUndoResult> UndoApplicationAsync(long jobId, long ruleId, EditToken editToken, AuditContext context, CancellationToken cancellationToken = default) =>
        InRuleAsync(jobId, ruleId, context, cancellationToken, editToken, async (t, rule) =>
        {
            RequirePerson(context, "undo a rule");
            t.RequireEditable();
            var ids = new List<long>();
            await using (var command = t.Connection.CreateCommand())
            {
                command.Transaction = t.Transaction;
                command.CommandText = "SELECT EntityId FROM ductwork.Entity WHERE JobId = @j AND ResolvedByRuleId = @r ORDER BY EntityId;";
                command.Parameters.AddWithValue("@j", jobId);
                command.Parameters.AddWithValue("@r", ruleId);
                await using var reader = await command.ExecuteReaderAsync(t.Token);
                while (await reader.ReadAsync(t.Token)) ids.Add(reader.GetInt64(0));
            }

            int undone = 0, skipped = 0;
            foreach (var id in ids)
            {
                var entity = (await EntityReviewRepository.ReadAsync(t.Connection, t.Transaction, "WITH (UPDLOCK, ROWLOCK) WHERE EntityId = @id", t.Token, ("@id", id)))!;
                var latestAction = await ScalarTextAsync(t, "SELECT Action FROM ductwork.EntityRevision WHERE EntityId = @e AND RevisionNo = @n;", ("@e", id), ("@n", entity.Revision));
                if (latestAction != "Rule") { skipped++; continue; }
                await EntityReviewRepository.UndoCoreAsync(t, entity);
                undone++;
            }

            await t.AuditAtCurrentVersionAsync(new AuditEntry("rules", "rule.undone", $"Took back the rule \"{rule.Name}\" on {undone} item(s); {skipped} had changed since and were left as they are.",
                "Rule", rule.PublicId.ToString("D"), null, JobRepository.Json(new() { ["undone"] = undone, ["skipped"] = skipped })));
            return new RuleUndoResult(undone, skipped);
        });

    // ------------------------------------------------------------------ the matching

    private sealed record PlannedChange(EntityRecord Entity, DetectionProposal After, string Text);

    private sealed record Computed(List<PlannedChange> Changes, List<(EntityRecord Entity, string Reason)> Skipped, List<(EntityRecord Entity, string Reason)> Conflicts)
    {
        public RulePreview ToPreview() => new(
            Changes.Select(c => new RulePreviewItem(c.Entity.PublicId, c.Entity.EntityId, Label(c.Entity), c.Text)).ToList(),
            Skipped.Select(s => new RuleSkip(s.Entity.PublicId, Label(s.Entity), s.Reason)).ToList(),
            Conflicts.Select(s => new RuleSkip(s.Entity.PublicId, Label(s.Entity), s.Reason)).ToList());
    }

    private static string Label(EntityRecord e) => $"{e.Type}{(string.IsNullOrWhiteSpace(e.Size) ? "" : " " + e.Size)}{(string.IsNullOrWhiteSpace(e.Tag) ? "" : " (" + e.Tag + ")")}";

    private static async Task<Computed> ComputeAsync(SqlConnection c, SqlTransaction? tx, long jobId, RuleRecord rule, CancellationToken ct)
    {
        var entities = await EntityRepository.ListAsync(c, tx, jobId, new EntityFilter(), ct);
        var types = (await BuildingTypeRepository.ListAsync(c, tx, jobId, ct)).ToDictionary(b => b.BuildingTypeId, b => b.Name);
        var viewToSheet = await ReadPairsAsync(c, tx, "SELECT SheetViewId, SheetId FROM ductwork.SheetView WHERE JobId = @j;", ct, ("@j", jobId));
        var decidedBy = await ReadRuleInfoAsync(c, tx, jobId, ct);

        var result = new Computed([], [], []);
        var engineRule = ToEngine(rule);
        foreach (var e in entities)
        {
            if (e.ReviewStatus == ReviewStatus.Rejected) continue;
            if (rule.Scope == RuleScope.BuildingType && e.BuildingTypeId != rule.BuildingTypeId) continue;
            if (rule.Scope == RuleScope.Sheet && !(e.SheetViewId is { } view && viewToSheet.TryGetValue(view, out var sheet) && sheet == rule.SheetId)) continue;

            var proposal = new DetectionProposal
            {
                DetectionId = e.PublicId.ToString("D"), Type = e.Type, Category = e.Category, Tag = e.Tag, Size = e.Size, Quantity = (double)e.Quantity, Unit = e.Unit, Method = e.Method,
                Family = e.Family ?? DetectorFamily.LabelFlagCount, Confidence = e.Confidence,
                BuildingType = e.BuildingTypeId is { } bt && types.TryGetValue(bt, out var name) ? name : null
            };
            var applications = InterpretationRuleEngine.Preview([proposal], engineRule);
            if (applications.Count == 0) continue;                           // the rule does not match this item, so it is not part of the picture

            if (e.ResolutionSource == ResolutionSource.UserAdded) { result.Skipped.Add((e, "Added by hand, so it is the person's own count.")); continue; }
            if (e.ResolutionSource == ResolutionSource.Override) { result.Skipped.Add((e, "A person set this item themselves, and that stands over any rule.")); continue; }
            if (e.ResolvedByRuleId == rule.RuleId && rule.RuleId != 0) { result.Skipped.Add((e, "This rule already decided this item.")); continue; }

            if (e.ResolvedByRuleId is { } other && decidedBy.TryGetValue(other, out var info) && info.Key != rule.RuleKey)
            {
                if (Rank(info.Scope) < Rank(rule.Scope)) { result.Skipped.Add((e, $"A {info.Scope.ToString().ToLowerInvariant()} rule is stronger and already decided this item.")); continue; }
                if (Rank(info.Scope) == Rank(rule.Scope)) { result.Conflicts.Add((e, $"Another {info.Scope.ToString().ToLowerInvariant()} rule has already decided this item differently.")); continue; }
            }

            var after = InterpretationRuleEngine.Apply([proposal], [engineRule]).Detections[0];
            result.Changes.Add(new PlannedChange(e, after, applications[0].Change));
        }

        return result;
    }

    private static InterpretationRule ToEngine(RuleRecord rule) => new()
    {
        RuleId = rule.PublicId == Guid.Empty ? "draft" : rule.PublicId.ToString("D"), Version = rule.Version, Description = rule.Name, Match = rule.Match, Action = rule.Action
    };

    private static void Validate(RuleRecord rule)
    {
        try { InterpretationRuleEngine.Validate([ToEngine(rule)]); }
        catch (ArgumentException ex) { throw new JobRuleViolationException(ex.Message); }
        if (string.IsNullOrWhiteSpace(rule.Name)) throw new JobRuleViolationException("A rule needs a name.");
        if (rule.Match.Method is { } m && !Enum.TryParse<DetectionMethod>(m, true, out _)) throw new JobRuleViolationException($"'{m}' is not a detection method.");
    }

    private static RuleRecord Draft(long jobId, NewRule draft) => new()
    {
        RuleId = 0, PublicId = Guid.Empty, RuleKey = Guid.Empty, Version = 1, Scope = draft.Scope, OriginJobId = jobId,
        JobId = draft.Scope == RuleScope.Instance ? null : jobId, BuildingTypeId = draft.Scope == RuleScope.BuildingType ? draft.BuildingTypeId : null,
        SheetId = draft.Scope == RuleScope.Sheet ? draft.SheetId : null, Name = (draft.Name ?? "").Trim(), Description = Clean(draft.Description, 1000),
        Match = draft.Match, Action = draft.Action, Status = RuleStatus.Proposed, FromFindingId = draft.FromFindingId, CreatedBy = "draft"
    };

    private static async Task<int> RaiseConflictAsync(JobTxn t, long taskId, long producerId, RuleRecord rule, (EntityRecord Entity, string Reason) conflict)
    {
        var sourceId = $"rule-conflict-{rule.RuleId}-{conflict.Entity.EntityId}";
        var exists = await ScalarAsync(t, "SELECT COUNT(*) FROM ductwork.Finding WHERE ProcessingTaskId = @t AND SourceFindingId = @s;", ("@t", taskId), ("@s", sourceId));
        if (exists > 0) return 0;

        await JobRepository.ExecAsync(t.Connection, t.Transaction,
            """
            INSERT INTO ductwork.Finding (JobId, ProcessingTaskId, ProducerId, EntityId, SourceFindingId, Kind, Question, Evidence)
            VALUES (@j, @t, @p, @e, @s, 'Other', @q, @ev);
            """, t.Token,
            ("@j", t.Job.JobId), ("@t", taskId), ("@p", producerId), ("@e", conflict.Entity.EntityId), ("@s", sourceId),
            ("@q", $"The rule \"{rule.Name}\" and another rule of the same strength disagree about {Label(conflict.Entity)}. Which should decide it?"), ("@ev", conflict.Reason));
        return 1;
    }

    // ------------------------------------------------------------------ helpers

    private async Task<T> InRuleAsync<T>(long jobId, long ruleId, AuditContext context, CancellationToken ct, EditToken editToken, Func<JobTxn, RuleRecord, Task<T>> body)
    {
        var existing = await GetAsync(ruleId, ct) ?? throw new KeyNotFoundException($"Rule {ruleId} does not exist.");
        RequireAppliesTo(existing, jobId);
        return await JobTxn.RunAsync(_open, jobId, context, ct, async t =>
        {
            var current = (await ReadRulesAsync(t.Connection, t.Transaction, "WHERE r.RuleId = @id", t.Token, ("@id", ruleId))).Single();
            return await body(t, current);
        }, editToken, requires: JobCapability.Edit);
    }

    /// <summary>A job's own rules, and instance rules, are reachable from a job. Another job's rule is simply not found.</summary>
    private static void RequireAppliesTo(RuleRecord rule, long jobId)
    {
        if (rule.Scope != RuleScope.Instance && rule.JobId != jobId) throw new KeyNotFoundException($"Rule {rule.RuleId} does not exist.");
    }

    private static void RequirePerson(AuditContext context, string what)
    {
        if (context.ActorType != ActorType.User) throw new JobRuleViolationException($"Only a person can {what}. An AI may help draft one, but a person decides.");
    }

    /// <summary>The instance library is shared by every job, so changing it needs more than edit permission on one job.</summary>
    private static async Task RequireScopeAllowedAsync(JobTxn t, RuleScope scope)
    {
        if (scope != RuleScope.Instance || t.Context.IsAdmin) return;
        var (_, manager) = await JobAccess.LoadAsync(t.Connection, t.Transaction, t.Job.JobId, t.Context.ActorEmail!, t.Token);
        if (!manager) throw new JobAccessDeniedException("Only a Job Manager or an administrator can change the rules shared by every job.");
    }

    private static async Task RequireTargetsOnJobAsync(JobTxn t, NewRule draft)
    {
        if (draft.Scope == RuleScope.BuildingType)
        {
            if (draft.BuildingTypeId is not { } bt) throw new JobRuleViolationException("A building-type rule needs a building type.");
            if (await ScalarAsync(t, "SELECT COUNT(*) FROM ductwork.BuildingType WHERE BuildingTypeId = @b AND JobId = @j;", ("@b", bt), ("@j", t.Job.JobId)) == 0)
                throw new JobRuleViolationException("That building type is not on this job.");
        }

        if (draft.Scope == RuleScope.Sheet)
        {
            if (draft.SheetId is not { } sheet) throw new JobRuleViolationException("A sheet rule needs a sheet.");
            if (await ScalarAsync(t, "SELECT COUNT(*) FROM ductwork.Sheet WHERE SheetId = @s AND JobId = @j;", ("@s", sheet), ("@j", t.Job.JobId)) == 0)
                throw new JobRuleViolationException("That sheet is not on this job.");
        }
    }

    private static async Task RequireFindingOnJobAsync(JobTxn t, long findingId)
    {
        if (await ScalarAsync(t, "SELECT COUNT(*) FROM ductwork.Finding WHERE FindingId = @f AND JobId = @j;", ("@f", findingId), ("@j", t.Job.JobId)) == 0)
            throw new JobRuleViolationException("That finding is not on this job.");
    }

    private static async Task<RuleRecord> InsertAsync(JobTxn t, RuleRecord rule)
    {
        var id = await ScalarAsync(t,
            """
            INSERT INTO ductwork.InterpretationRule (RuleKey, Version, Scope, JobId, BuildingTypeId, SheetId, OriginJobId, Name, Description, MatchJson, ActionJson, Status, FromFindingId, CreatedBy)
            OUTPUT INSERTED.RuleId
            VALUES (@k, @v, @scope, @j, @bt, @sheet, @origin, @name, @desc, @match, @action, 'Proposed', @finding, @by);
            """,
            ("@k", rule.RuleKey), ("@v", rule.Version), ("@scope", rule.Scope.ToString()), ("@j", rule.JobId), ("@bt", rule.BuildingTypeId), ("@sheet", rule.SheetId),
            ("@origin", t.Job.JobId), ("@name", rule.Name.Trim()), ("@desc", Clean(rule.Description, 1000)), ("@match", JsonSerializer.Serialize(rule.Match, Json)),
            ("@action", JsonSerializer.Serialize(rule.Action, Json)), ("@finding", rule.FromFindingId), ("@by", JobRepository.Actor(t.Context)));
        return (await ReadRulesAsync(t.Connection, t.Transaction, "WHERE r.RuleId = @id", t.Token, ("@id", id))).Single();
    }

    private static string Describe(RuleRecord r) => JobRepository.Json(new()
    {
        ["name"] = r.Name, ["version"] = r.Version, ["scope"] = r.Scope.ToString(), ["status"] = r.Status.ToString(),
        ["match"] = JsonSerializer.Serialize(r.Match, Json), ["action"] = JsonSerializer.Serialize(r.Action, Json)
    });

    private static string? Clean(string? text, int max)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }

    private static async Task<long> ScalarAsync(JobTxn t, string sql, params (string Name, object? Value)[] args)
    {
        await using var command = t.Connection.CreateCommand();
        command.Transaction = t.Transaction;
        command.CommandText = sql;
        foreach (var (name, value) in args) JobRepository.P(command, name, value);
        var result = await command.ExecuteScalarAsync(t.Token);
        return result is null or DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ScalarTextAsync(JobTxn t, string sql, params (string Name, object? Value)[] args)
    {
        await using var command = t.Connection.CreateCommand();
        command.Transaction = t.Transaction;
        command.CommandText = sql;
        foreach (var (name, value) in args) JobRepository.P(command, name, value);
        var result = await command.ExecuteScalarAsync(t.Token);
        return result is null or DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    private static async Task<Dictionary<long, long>> ReadPairsAsync(SqlConnection c, SqlTransaction? tx, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in args) JobRepository.P(command, name, value);
        var map = new Dictionary<long, long>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) map[reader.GetInt64(0)] = reader.GetInt64(1);
        return map;
    }

    private static async Task<Dictionary<long, (Guid Key, RuleScope Scope)>> ReadRuleInfoAsync(SqlConnection c, SqlTransaction? tx, long jobId, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            "SELECT RuleId, RuleKey, Scope FROM ductwork.InterpretationRule WHERE RuleId IN (SELECT DISTINCT ResolvedByRuleId FROM ductwork.Entity WHERE JobId = @j AND ResolvedByRuleId IS NOT NULL);";
        command.Parameters.AddWithValue("@j", jobId);
        var map = new Dictionary<long, (Guid, RuleScope)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) map[reader.GetInt64(0)] = (reader.GetGuid(1), Enum.Parse<RuleScope>(reader.GetString(2)));
        return map;
    }

    internal static async Task<List<RuleRecord>> ReadRulesAsync(SqlConnection c, SqlTransaction? tx, string where, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            """
            SELECT r.RuleId, r.PublicId, r.RuleKey, r.Version, r.Scope, r.JobId, r.BuildingTypeId, r.SheetId, r.OriginJobId, r.Name, r.Description, r.MatchJson, r.ActionJson,
                   r.Status, r.FromFindingId, r.CreatedBy, r.CreatedUtc, r.ApprovedBy, r.ApprovedUtc, r.RetiredBy, r.RetiredUtc, r.RetireReason
            FROM ductwork.InterpretationRule r
            """ + " " + where;
        foreach (var (name, value) in args) JobRepository.P(command, name, value);
        var list = new List<RuleRecord>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new RuleRecord
            {
                RuleId = r.GetInt64(0), PublicId = r.GetGuid(1), RuleKey = r.GetGuid(2), Version = r.GetInt32(3), Scope = Enum.Parse<RuleScope>(r.GetString(4)),
                JobId = r.IsDBNull(5) ? null : r.GetInt64(5), BuildingTypeId = r.IsDBNull(6) ? null : r.GetInt64(6), SheetId = r.IsDBNull(7) ? null : r.GetInt64(7),
                OriginJobId = r.GetInt64(8), Name = r.GetString(9), Description = r.IsDBNull(10) ? null : r.GetString(10),
                Match = JsonSerializer.Deserialize<RuleMatch>(r.GetString(11), Json) ?? new RuleMatch(),
                Action = JsonSerializer.Deserialize<RuleAction>(r.GetString(12), Json) ?? new RuleAction(),
                Status = Enum.Parse<RuleStatus>(r.GetString(13)), FromFindingId = r.IsDBNull(14) ? null : r.GetInt64(14), CreatedBy = r.GetString(15), CreatedUtc = r.GetDateTime(16),
                ApprovedBy = r.IsDBNull(17) ? null : r.GetString(17), ApprovedUtc = r.IsDBNull(18) ? null : r.GetDateTime(18),
                RetiredBy = r.IsDBNull(19) ? null : r.GetString(19), RetiredUtc = r.IsDBNull(20) ? null : r.GetDateTime(20), RetireReason = r.IsDBNull(21) ? null : r.GetString(21)
            });
        return list;
    }
}
