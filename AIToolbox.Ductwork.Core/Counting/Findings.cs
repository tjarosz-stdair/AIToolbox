using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Rules;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Counting;

public enum FindingState { Open, Resolved, Waived }

public enum FindingResolutionKind { Rule, Override, Waiver }

public sealed record FindingRecord
{
    public long FindingId { get; init; }
    public Guid PublicId { get; init; }
    public long JobId { get; init; }
    public long? EntityId { get; init; }
    public Guid? EntityPublicId { get; init; }
    public string? Item { get; init; }
    public FindingKind Kind { get; init; }
    public required string Question { get; init; }
    public string? CandidatesJson { get; init; }
    public string? Evidence { get; init; }
    public FindingState State { get; init; }
    public FindingResolutionKind? ResolvedByKind { get; init; }
    public Guid? ResolvedRulePublicId { get; init; }
    public string? ResolvedBy { get; init; }
    public DateTime? ResolvedUtc { get; init; }
    public string? ResolutionNote { get; init; }
    public DateTime CreatedUtc { get; init; }
}

/// <summary>
/// Findings are the questions a detector could not answer (plan section 6.11). The user is the arbiter: a finding stays
/// open, and on the count sheet, until a person closes it in one of three ways, each of which leaves its reason on
/// record. By a rule (the rule must be in force and must actually have decided the item), by an override (the person
/// set the item themselves, or rejected it), or by a waiver (a written reason for leaving the question unanswered).
/// </summary>
public sealed class FindingRepository
{
    private readonly Func<CancellationToken, Task<SqlConnection>> _open;

    public FindingRepository(Func<CancellationToken, Task<SqlConnection>> openConnection) => _open = openConnection;

    // ------------------------------------------------------------------ reads

    public async Task<IReadOnlyList<FindingRecord>> ListByJobAsync(long jobId, FindingState? state = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return await ReadAsync(connection, null, "WHERE f.JobId = @j AND (@s IS NULL OR f.State = @s) ORDER BY f.FindingId", cancellationToken,
            ("@j", jobId), ("@s", state?.ToString()));
    }

    public async Task<FindingRecord?> GetByPublicIdAsync(long jobId, Guid publicId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return (await ReadAsync(connection, null, "WHERE f.JobId = @j AND f.PublicId = @p", cancellationToken, ("@j", jobId), ("@p", publicId))).SingleOrDefault();
    }

    internal static Task<List<FindingRecord>> ListOpenAsync(SqlConnection c, SqlTransaction? tx, long jobId, CancellationToken ct) =>
        ReadAsync(c, tx, "WHERE f.JobId = @j AND f.State = 'Open' ORDER BY f.FindingId", ct, ("@j", jobId));

    // ------------------------------------------------------------------ resolving

    /// <summary>The finding is answered by a rule. The rule must be approved and must have decided the finding's item, so a finding cannot be closed by a rule that did nothing.</summary>
    public Task<FindingRecord> ResolveByRuleAsync(long jobId, long findingId, long ruleId, string? note, EditToken editToken, AuditContext context,
        CancellationToken cancellationToken = default) =>
        InFindingAsync(jobId, findingId, context, cancellationToken, editToken, async (t, finding) =>
        {
            RequireOpen(finding);
            var rule = (await RuleRepository.ReadRulesAsync(t.Connection, t.Transaction, "WHERE r.RuleId = @id AND (r.Scope = 'Instance' OR r.JobId = @j)", t.Token,
                ("@id", ruleId), ("@j", jobId))).SingleOrDefault() ?? throw new JobRuleViolationException("That rule is not available on this job.");
            if (rule.Status != RuleStatus.Approved) throw new JobRuleViolationException("Only an approved rule can answer a finding.");

            if (finding.EntityId is { } entityId)
            {
                var entity = (await EntityReviewRepository.ReadAsync(t.Connection, t.Transaction, "WHERE EntityId = @id", t.Token, ("@id", entityId)))!;
                var decidedKey = entity.ResolvedByRuleId is { } decided
                    ? (await RuleRepository.ReadRulesAsync(t.Connection, t.Transaction, "WHERE r.RuleId = @id", t.Token, ("@id", decided))).SingleOrDefault()?.RuleKey
                    : null;
                if (decidedKey != rule.RuleKey)
                    throw new JobRuleViolationException("That rule has not decided this item yet. Apply the rule first, then close the finding.");
            }

            return await CloseAsync(t, finding, FindingState.Resolved, FindingResolutionKind.Rule, ruleId, note,
                $"Resolved a finding with the rule \"{rule.Name}\".", "finding.resolved-by-rule");
        });

    /// <summary>The person answered it themselves: they changed the item, or rejected it. A finding about an item they have not touched cannot be closed this way.</summary>
    public Task<FindingRecord> ResolveByOverrideAsync(long jobId, long findingId, string? note, EditToken editToken, AuditContext context,
        CancellationToken cancellationToken = default) =>
        InFindingAsync(jobId, findingId, context, cancellationToken, editToken, async (t, finding) =>
        {
            RequireOpen(finding);
            if (finding.EntityId is not { } entityId) throw new JobRuleViolationException("This finding is not about one item, so there is nothing to override. Use a rule, or waive it with a reason.");
            var entity = (await EntityReviewRepository.ReadAsync(t.Connection, t.Transaction, "WHERE EntityId = @id", t.Token, ("@id", entityId)))!;
            if (entity.ResolutionSource != ResolutionSource.Override && entity.ReviewStatus != ReviewStatus.Rejected)
                throw new JobRuleViolationException("Change the item, or reject it, and then close the finding.");
            return await CloseAsync(t, finding, FindingState.Resolved, FindingResolutionKind.Override, null, note, "Resolved a finding by overriding the item.", "finding.resolved-by-override");
        });

    /// <summary>The question is left unanswered on purpose. The reason is required and stays on record.</summary>
    public Task<FindingRecord> WaiveAsync(long jobId, long findingId, string note, EditToken editToken, AuditContext context, CancellationToken cancellationToken = default) =>
        InFindingAsync(jobId, findingId, context, cancellationToken, editToken, async (t, finding) =>
        {
            RequireOpen(finding);
            if (string.IsNullOrWhiteSpace(note)) throw new JobRuleViolationException("Waiving a finding needs a reason.");
            return await CloseAsync(t, finding, FindingState.Waived, FindingResolutionKind.Waiver, null, note, "Waived a finding.", "finding.waived");
        });

    public Task<FindingRecord> ReopenAsync(long jobId, long findingId, EditToken editToken, AuditContext context, CancellationToken cancellationToken = default) =>
        InFindingAsync(jobId, findingId, context, cancellationToken, editToken, async (t, finding) =>
        {
            if (finding.State == FindingState.Open) return finding;
            await JobRepository.ExecAsync(t.Connection, t.Transaction,
                "UPDATE ductwork.Finding SET State = 'Open', ResolvedByKind = NULL, ResolvedRuleId = NULL, ResolvedBy = NULL, ResolvedUtc = NULL, ResolutionNote = NULL WHERE FindingId = @id;",
                t.Token, ("@id", finding.FindingId));
            await t.AuditAtCurrentVersionAsync(new AuditEntry("review", "finding.reopened", "Reopened a finding.", "Finding", finding.PublicId.ToString("D"),
                JobRepository.Json(new() { ["state"] = finding.State.ToString(), ["by"] = finding.ResolvedByKind?.ToString(), ["note"] = finding.ResolutionNote }), null));
            return (await ReadAsync(t.Connection, t.Transaction, "WHERE f.FindingId = @id", t.Token, ("@id", finding.FindingId))).Single();
        });

    // ------------------------------------------------------------------ helpers

    private async Task<T> InFindingAsync<T>(long jobId, long findingId, AuditContext context, CancellationToken ct, EditToken editToken, Func<JobTxn, FindingRecord, Task<T>> body)
    {
        if (context.ActorType != ActorType.User) throw new JobRuleViolationException("Only a person can answer a finding. The detector asked; a person decides.");
        return await JobTxn.RunAsync(_open, jobId, context, ct, async t =>
        {
            t.RequireEditable();
            var finding = (await ReadAsync(t.Connection, t.Transaction, "WHERE f.FindingId = @id AND f.JobId = @j", t.Token, ("@id", findingId), ("@j", jobId))).SingleOrDefault()
                ?? throw new KeyNotFoundException($"Finding {findingId} is not on this job.");
            return await body(t, finding);
        }, editToken, requires: JobCapability.Edit);
    }

    private static void RequireOpen(FindingRecord finding)
    {
        if (finding.State != FindingState.Open) throw new JobRuleViolationException($"This finding is already {finding.State.ToString().ToLowerInvariant()}. Reopen it to answer it differently.");
    }

    private static async Task<FindingRecord> CloseAsync(JobTxn t, FindingRecord finding, FindingState state, FindingResolutionKind kind, long? ruleId, string? note, string summary, string action)
    {
        var clean = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(400, note.Trim().Length)];
        await JobRepository.ExecAsync(t.Connection, t.Transaction,
            "UPDATE ductwork.Finding SET State = @s, ResolvedByKind = @k, ResolvedRuleId = @r, ResolvedBy = @by, ResolvedUtc = SYSUTCDATETIME(), ResolutionNote = @n WHERE FindingId = @id;",
            t.Token, ("@s", state.ToString()), ("@k", kind.ToString()), ("@r", ruleId), ("@by", JobRepository.Actor(t.Context)), ("@n", clean), ("@id", finding.FindingId));
        await t.AuditAtCurrentVersionAsync(new AuditEntry("review", action, summary, "Finding", finding.PublicId.ToString("D"), null,
            JobRepository.Json(new() { ["kind"] = kind.ToString(), ["note"] = clean, ["question"] = finding.Question })));
        return (await ReadAsync(t.Connection, t.Transaction, "WHERE f.FindingId = @id", t.Token, ("@id", finding.FindingId))).Single();
    }

    private static async Task<List<FindingRecord>> ReadAsync(SqlConnection c, SqlTransaction? tx, string where, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            """
            SELECT f.FindingId, f.PublicId, f.JobId, f.EntityId, e.PublicId, e.Type, e.Size, e.Tag, f.Kind, f.Question, f.CandidatesJson, f.Evidence, f.State,
                   f.ResolvedByKind, rr.PublicId, f.ResolvedBy, f.ResolvedUtc, f.ResolutionNote, f.CreatedUtc
            FROM ductwork.Finding f
            LEFT JOIN ductwork.Entity e ON e.EntityId = f.EntityId
            LEFT JOIN ductwork.InterpretationRule rr ON rr.RuleId = f.ResolvedRuleId
            """ + " " + where;
        foreach (var (name, value) in args) JobRepository.P(command, name, value);
        var list = new List<FindingRecord>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            string? item = null;
            if (!r.IsDBNull(5))
            {
                var size = r.IsDBNull(6) ? "" : " " + r.GetString(6);
                var tag = r.IsDBNull(7) ? "" : " (" + r.GetString(7) + ")";
                item = r.GetString(5) + size + tag;
            }

            list.Add(new FindingRecord
            {
                FindingId = r.GetInt64(0), PublicId = r.GetGuid(1), JobId = r.GetInt64(2), EntityId = r.IsDBNull(3) ? null : r.GetInt64(3), EntityPublicId = r.IsDBNull(4) ? null : r.GetGuid(4),
                Item = item, Kind = Enum.Parse<FindingKind>(r.GetString(8)), Question = r.GetString(9), CandidatesJson = r.IsDBNull(10) ? null : r.GetString(10),
                Evidence = r.IsDBNull(11) ? null : r.GetString(11), State = Enum.Parse<FindingState>(r.GetString(12)),
                ResolvedByKind = r.IsDBNull(13) ? null : Enum.Parse<FindingResolutionKind>(r.GetString(13)), ResolvedRulePublicId = r.IsDBNull(14) ? null : r.GetGuid(14),
                ResolvedBy = r.IsDBNull(15) ? null : r.GetString(15), ResolvedUtc = r.IsDBNull(16) ? null : r.GetDateTime(16), ResolutionNote = r.IsDBNull(17) ? null : r.GetString(17),
                CreatedUtc = r.GetDateTime(18)
            });
        }

        return list;
    }
}
