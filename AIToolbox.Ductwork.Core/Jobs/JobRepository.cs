using System.Text.Json;
using AIToolbox.Ductwork.Contracts;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Jobs;

/// <summary>
/// Jobs, members, tags, workflow steps and lifecycle (plan section 8), with the audit log (section 10).
///
/// Structural rules, enforced here and not only in the interface:
/// * Every method that changes data takes an <see cref="AuditContext"/> and writes the change and its
///   audit event in ONE transaction. An architecture test fails the build if one does not.
/// * The job row is locked for the length of every change, so changes to one job are serialized, its
///   events are numbered and chained in order, and its version is always the version that was changed.
/// * Permission and lifecycle rules are checked on every call, using the identity in the context.
/// * A change that changes nothing writes nothing: no audit event without a mutation.
/// </summary>
public sealed class JobRepository
{
    private static readonly string[] AllSteps = ["Setup", "Process", "Review", "Report"];

    private readonly Func<CancellationToken, Task<SqlConnection>> _open;
    private readonly ICompletionProbe _probe;

    public JobRepository(Func<CancellationToken, Task<SqlConnection>> openConnection, ICompletionProbe? probe = null)
    {
        _open = openConnection;
        _probe = probe ?? new Counting.JobCompletionProbe();
    }

    public JobRepository(string connectionString, ICompletionProbe? probe = null)
        : this(async ct =>
        {
            var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);
            return connection;
        }, probe)
    {
    }

    // ------------------------------------------------------------------ reads

    public async Task<JobRecord?> GetAsync(long jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return await ReadJobAsync(connection, null, jobId, forUpdate: false, cancellationToken);
    }

    public async Task<IReadOnlyList<JobRecord>> ListAsync(JobListFilter? filter = null, CancellationToken cancellationToken = default)
    {
        filter ??= new JobListFilter();
        await using var connection = await _open(cancellationToken);
        await using var command = connection.CreateCommand();

        var where = new List<string>();
        if (filter.States is { Count: > 0 })
        {
            var names = filter.States.Select((s, i) => (Name: $"@s{i}", Value: s.ToString())).ToList();
            where.Add($"j.State IN ({string.Join(", ", names.Select(n => n.Name))})");
            foreach (var n in names) command.Parameters.AddWithValue(n.Name, n.Value);
        }

        if (filter.OwnerEmail is not null)
        {
            where.Add("j.OwnerEmail = @owner");
            command.Parameters.AddWithValue("@owner", AuditContext.Normalize(filter.OwnerEmail));
        }

        if (filter.MemberEmail is not null)
        {
            where.Add("EXISTS (SELECT 1 FROM ductwork.JobMember m WHERE m.JobId = j.JobId AND m.UserEmail = @member)");
            command.Parameters.AddWithValue("@member", AuditContext.Normalize(filter.MemberEmail));
        }

        if (filter.Tags is { Count: > 0 })
        {
            var normalized = filter.Tags.Select(NormalizeTag).Select(t => t.Normalized).Distinct().ToList();
            var names = normalized.Select((t, i) => (Name: $"@t{i}", Value: t)).ToList();
            where.Add($"""
                (SELECT COUNT(DISTINCT t.TagId) FROM ductwork.JobTag jt JOIN ductwork.Tag t ON t.TagId = jt.TagId
                 WHERE jt.JobId = j.JobId AND t.NameNormalized IN ({string.Join(", ", names.Select(n => n.Name))})) = {names.Count}
                """);
            foreach (var n in names) command.Parameters.AddWithValue(n.Name, n.Value);
        }

        command.CommandText = $"""
            SELECT TOP (@limit) {JobColumns("j")}
            FROM ductwork.Job j
            {(where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where))}
            ORDER BY j.UpdatedUtc DESC, j.JobId DESC;
            """;
        command.Parameters.AddWithValue("@limit", Math.Clamp(filter.Limit, 1, 2000));

        var jobs = new List<JobRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) jobs.Add(MapJob(reader));
        return jobs;
    }

    public async Task<IReadOnlyList<JobMemberRecord>> GetMembersAsync(long jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return await ReadMembersAsync(connection, null, jobId, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetTagsAsync(long jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT t.Name FROM ductwork.JobTag jt JOIN ductwork.Tag t ON t.TagId = jt.TagId WHERE jt.JobId = @j ORDER BY t.NameNormalized;";
        command.Parameters.AddWithValue("@j", jobId);
        var tags = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) tags.Add(reader.GetString(0));
        return tags;
    }

    public async Task<IReadOnlyDictionary<string, StepStatus>> GetWorkflowAsync(long jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return await ReadStepsAsync(connection, null, jobId, cancellationToken);
    }

    public async Task<IReadOnlyList<AuditEvent>> ListAuditAsync(long jobId, AuditFilter? filter = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return await AuditLog.ListAsync(connection, jobId, filter, cancellationToken);
    }

    public async Task<ChainVerification> VerifyAuditChainAsync(long jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return await AuditLog.VerifyAsync(connection, jobId, cancellationToken);
    }

    // ------------------------------------------------------------------ create

    public async Task<JobRecord> CreateAsync(NewJob job, AuditContext context, CancellationToken cancellationToken = default)
    {
        context.EnsureValid();
        if (context.ActorType != ActorType.User)
            throw new ArgumentException("A job is created by a user, who becomes its owner.", nameof(context));
        var name = Required(job.Name, "A job needs a name.");

        await using var connection = await _open(cancellationToken);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            long jobId;
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText =
                    """
                    INSERT INTO ductwork.Job (Name, CustomerName, ProjectName, SiteAddress, OwnerEmail, ReviewLevel, CreatedBy)
                    VALUES (@name, @customer, @project, @site, @owner, @level, @by);
                    SELECT CAST(SCOPE_IDENTITY() AS BIGINT);
                    """;
                insert.Parameters.AddWithValue("@name", name);
                P(insert, "@customer", Blank(job.CustomerName));
                P(insert, "@project", Blank(job.ProjectName));
                P(insert, "@site", Blank(job.SiteAddress));
                insert.Parameters.AddWithValue("@owner", context.ActorEmail!);
                insert.Parameters.AddWithValue("@level", job.ReviewLevel.ToString());
                insert.Parameters.AddWithValue("@by", context.ActorEmail!);
                jobId = (long)(await insert.ExecuteScalarAsync(cancellationToken))!;
            }

            await ExecAsync(connection, tx,
                "INSERT INTO ductwork.JobMember (JobId, UserEmail, Role, AddedBy) VALUES (@j, @e, 'Owner', @e);",
                cancellationToken, ("@j", jobId), ("@e", context.ActorEmail!));
            foreach (var step in AllSteps)
                await ExecAsync(connection, tx, "INSERT INTO ductwork.JobWorkflowStep (JobId, Step) VALUES (@j, @s);",
                    cancellationToken, ("@j", jobId), ("@s", step));

            var created = (await ReadJobAsync(connection, tx, jobId, forUpdate: false, cancellationToken))!;
            await AuditLog.AppendAsync(connection, tx, jobId, created.StateVersion,
                new AuditEntry("lifecycle", "job.created", $"Created job \"{name}\".", "Job", created.PublicId.ToString("D"),
                    null, Json(new Dictionary<string, object?>
                    {
                        ["name"] = name, ["customerName"] = created.CustomerName, ["projectName"] = created.ProjectName,
                        ["siteAddress"] = created.SiteAddress, ["reviewLevel"] = job.ReviewLevel.ToString()
                    })),
                context, cancellationToken);

            await tx.CommitAsync(cancellationToken);
            return created;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    // ------------------------------------------------------------------ header details

    public Task<JobRecord> UpdateDetailsAsync(long jobId, JobDetails details, long expectedStateVersion, EditToken editToken,
        AuditContext context, CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context, cancellationToken, async t =>
        {
            await t.RequireAsync(JobCapability.Edit);
            t.RequireEditable();
            if (t.Job.StateVersion != expectedStateVersion) throw new JobConcurrencyException(expectedStateVersion, t.Job.StateVersion);

            var before = new Dictionary<string, object?>();
            var after = new Dictionary<string, object?>();
            var sets = new List<string>();
            var args = new List<(string, object?)>();

            void Change(string column, string key, string? oldValue, string? newValue)
            {
                if (string.Equals(oldValue, newValue, StringComparison.Ordinal)) return;
                before[key] = oldValue;
                after[key] = newValue;
                sets.Add($"{column} = @{key}");
                args.Add(($"@{key}", newValue));
            }

            if (details.Name is not null) Change("Name", "name", t.Job.Name, Required(details.Name, "A job needs a name."));
            Optional(details.CustomerName, "CustomerName", "customerName", t.Job.CustomerName, details.Clear, Change);
            Optional(details.ProjectName, "ProjectName", "projectName", t.Job.ProjectName, details.Clear, Change);
            Optional(details.SiteAddress, "SiteAddress", "siteAddress", t.Job.SiteAddress, details.Clear, Change);

            if (sets.Count == 0) return t.Job;

            var version = await t.BumpAsync(string.Join(", ", sets), args.ToArray());
            await t.AuditAsync(version, new AuditEntry("lifecycle", "job.edited", "Edited the job details.", "Job",
                t.Job.PublicId.ToString("D"), Json(before), Json(after)));
            return await t.ReloadAsync();
        }, editToken, requires: JobCapability.Edit);

    public Task<JobRecord> SetReviewLevelAsync(long jobId, ReviewLevel level, EditToken editToken, AuditContext context, CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context, cancellationToken, async t =>
        {
            await t.RequireAsync(JobCapability.Edit);
            t.RequireEditable();
            if (t.Job.ReviewLevel == level) return t.Job;

            var version = await t.BumpAsync("ReviewLevel = @level", ("@level", level.ToString()));
            await t.AuditAsync(version, new AuditEntry("setup", "job.review-level-changed",
                $"Changed the review level from {t.Job.ReviewLevel} to {level}.", "Job", t.Job.PublicId.ToString("D"),
                Json(new Dictionary<string, object?> { ["reviewLevel"] = t.Job.ReviewLevel.ToString() }),
                Json(new Dictionary<string, object?> { ["reviewLevel"] = level.ToString() })));
            return await t.ReloadAsync();
        }, editToken, requires: JobCapability.Edit);

    /// <summary>The estimator's own estimate of the manual time, used for ROI (plan section 10.1). It is metadata, so it can change after completion.</summary>
    public Task<JobRecord> SetBaselineManualHoursAsync(long jobId, decimal? hours, AuditContext context, CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context, cancellationToken, async t =>
        {
            await t.RequireAsync(JobCapability.Edit);
            t.RequireMetadataChange();
            if (hours is < 0) throw new JobRuleViolationException("The baseline manual time cannot be negative.");
            if (t.Job.BaselineManualHours == hours) return t.Job;

            var version = await t.BumpAsync("BaselineManualHours = @h", ("@h", hours));
            await t.AuditAsync(version, new AuditEntry("setup", "job.baseline-changed", "Changed the baseline manual hours.", "Job",
                t.Job.PublicId.ToString("D"),
                Json(new Dictionary<string, object?> { ["baselineManualHours"] = t.Job.BaselineManualHours }),
                Json(new Dictionary<string, object?> { ["baselineManualHours"] = hours })));
            return await t.ReloadAsync();
        });

    // ------------------------------------------------------------------ members

    public Task<IReadOnlyList<JobMemberRecord>> AddMemberAsync(long jobId, string email, JobRole role, AuditContext context,
        CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context, cancellationToken, async t =>
        {
            await t.RequireAsync(JobCapability.ManageMembers);
            t.RequireMetadataChange();
            var address = ValidEmail(email);

            var members = await ReadMembersAsync(t.Connection, t.Transaction, jobId, t.Token);
            if (members.Any(m => m.UserEmail == address))
                throw new JobRuleViolationException($"{address} is already a member. Change their role instead.");

            await ExecAsync(t.Connection, t.Transaction,
                "INSERT INTO ductwork.JobMember (JobId, UserEmail, Role, AddedBy) VALUES (@j, @e, @r, @by);", t.Token,
                ("@j", jobId), ("@e", address), ("@r", role.ToString()), ("@by", context.ActorEmail ?? context.ActorDisplayName ?? "system"));
            var version = await t.BumpAsync("");
            await t.AuditAsync(version, new AuditEntry("access", "member.added", $"Added {address} as {role}.", "Member", address,
                null, Json(new Dictionary<string, object?> { ["email"] = address, ["role"] = role.ToString() })));
            return await ReadMembersAsync(t.Connection, t.Transaction, jobId, t.Token);
        });

    public Task<IReadOnlyList<JobMemberRecord>> ChangeMemberRoleAsync(long jobId, string email, JobRole role, AuditContext context,
        CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context, cancellationToken, async t =>
        {
            await t.RequireAsync(JobCapability.ManageMembers);
            t.RequireMetadataChange();
            var address = ValidEmail(email);

            var members = await ReadMembersAsync(t.Connection, t.Transaction, jobId, t.Token);
            var member = members.FirstOrDefault(m => m.UserEmail == address)
                ?? throw new JobRuleViolationException($"{address} is not a member of this job.");
            if (member.Role == role) return members;
            if (member.Role == JobRole.Owner && members.Count(m => m.Role == JobRole.Owner) == 1)
                throw new JobRuleViolationException("A job must keep at least one owner. Make someone else an owner first.");

            await ExecAsync(t.Connection, t.Transaction, "UPDATE ductwork.JobMember SET Role = @r WHERE JobId = @j AND UserEmail = @e;",
                t.Token, ("@r", role.ToString()), ("@j", jobId), ("@e", address));
            var version = await t.BumpAsync("");
            await t.SyncOwnerAsync();
            await t.AuditAsync(version, new AuditEntry("access", "member.role-changed", $"Changed {address} from {member.Role} to {role}.",
                "Member", address,
                Json(new Dictionary<string, object?> { ["email"] = address, ["role"] = member.Role.ToString() }),
                Json(new Dictionary<string, object?> { ["email"] = address, ["role"] = role.ToString() })));
            return await ReadMembersAsync(t.Connection, t.Transaction, jobId, t.Token);
        });

    public Task<IReadOnlyList<JobMemberRecord>> RemoveMemberAsync(long jobId, string email, AuditContext context,
        CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context, cancellationToken, async t =>
        {
            await t.RequireAsync(JobCapability.ManageMembers);
            t.RequireMetadataChange();
            var address = ValidEmail(email);

            var members = await ReadMembersAsync(t.Connection, t.Transaction, jobId, t.Token);
            var member = members.FirstOrDefault(m => m.UserEmail == address)
                ?? throw new JobRuleViolationException($"{address} is not a member of this job.");
            if (member.Role == JobRole.Owner && members.Count(m => m.Role == JobRole.Owner) == 1)
                throw new JobRuleViolationException("A job must keep at least one owner. Make someone else an owner first.");

            await ExecAsync(t.Connection, t.Transaction, "DELETE FROM ductwork.JobMember WHERE JobId = @j AND UserEmail = @e;",
                t.Token, ("@j", jobId), ("@e", address));
            var version = await t.BumpAsync("");
            await t.SyncOwnerAsync();
            await t.AuditAsync(version, new AuditEntry("access", "member.removed", $"Removed {address} ({member.Role}).", "Member", address,
                Json(new Dictionary<string, object?> { ["email"] = address, ["role"] = member.Role.ToString() }), null));
            return await ReadMembersAsync(t.Connection, t.Transaction, jobId, t.Token);
        });

    // ------------------------------------------------------------------ tags

    /// <summary>Tags are header metadata: editors can change them on a Complete, Closed or Archived job without reopening it.</summary>
    public Task<IReadOnlyList<string>> AddTagAsync(long jobId, string tag, AuditContext context, CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context, cancellationToken, async t =>
        {
            await t.RequireAsync(JobCapability.Tag);
            t.RequireMetadataChange();
            var (display, normalized) = NormalizeTag(tag);

            var tagId = await EnsureTagAsync(t, display, normalized);
            var added = await ExecAsync(t.Connection, t.Transaction,
                """
                INSERT INTO ductwork.JobTag (JobId, TagId, AddedBy)
                SELECT @j, @t, @by WHERE NOT EXISTS (SELECT 1 FROM ductwork.JobTag WHERE JobId = @j AND TagId = @t);
                """, t.Token, ("@j", jobId), ("@t", tagId), ("@by", context.ActorEmail ?? context.ActorDisplayName ?? "system"));

            if (added > 0)
            {
                var version = await t.BumpAsync("");
                await t.AuditAsync(version, new AuditEntry("tags", "tag.added", $"Added the tag \"{display}\".", "Tag", normalized,
                    null, Json(new Dictionary<string, object?> { ["tag"] = display })));
            }

            return await TagsAsync(t);
        });

    public Task<IReadOnlyList<string>> RemoveTagAsync(long jobId, string tag, AuditContext context, CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context, cancellationToken, async t =>
        {
            await t.RequireAsync(JobCapability.Tag);
            t.RequireMetadataChange();
            var (display, normalized) = NormalizeTag(tag);

            var removed = await ExecAsync(t.Connection, t.Transaction,
                """
                DELETE jt FROM ductwork.JobTag jt JOIN ductwork.Tag g ON g.TagId = jt.TagId
                WHERE jt.JobId = @j AND g.NameNormalized = @n;
                """, t.Token, ("@j", jobId), ("@n", normalized));

            if (removed > 0)
            {
                var version = await t.BumpAsync("");
                await t.AuditAsync(version, new AuditEntry("tags", "tag.removed", $"Removed the tag \"{display}\".", "Tag", normalized,
                    Json(new Dictionary<string, object?> { ["tag"] = display }), null));
            }

            return await TagsAsync(t);
        });

    // ------------------------------------------------------------------ workflow and lifecycle

    public Task<IReadOnlyDictionary<string, StepStatus>> SetWorkflowStepAsync(long jobId, WorkflowStep step, StepStatus status,
        EditToken editToken, AuditContext context, CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context, cancellationToken, async t =>
        {
            await t.RequireAsync(JobCapability.Edit);
            t.RequireEditable();

            var steps = await ReadStepsAsync(t.Connection, t.Transaction, jobId, t.Token);
            var old = steps[step.ToString()];
            if (old == status) return steps;

            await ExecAsync(t.Connection, t.Transaction,
                "UPDATE ductwork.JobWorkflowStep SET Status = @s, UpdatedUtc = SYSUTCDATETIME() WHERE JobId = @j AND Step = @step;",
                t.Token, ("@s", status.ToString()), ("@j", jobId), ("@step", step.ToString()));
            var version = await t.BumpAsync("");
            await t.AuditAsync(version, new AuditEntry("workflow", "step.changed", $"{step} changed from {old} to {status}.", "Step", step.ToString(),
                Json(new Dictionary<string, object?> { ["step"] = step.ToString(), ["status"] = old.ToString() }),
                Json(new Dictionary<string, object?> { ["step"] = step.ToString(), ["status"] = status.ToString() })));
            return await ReadStepsAsync(t.Connection, t.Transaction, jobId, t.Token);
        }, editToken, requires: JobCapability.Edit);

    /// <summary>Starts completing: the job becomes read-only while the issued reports are made. A failed report run leaves it Completing, never silently Complete.</summary>
    public Task<JobRecord> BeginCompleteAsync(long jobId, AuditContext context, CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context, cancellationToken, async t =>
        {
            await t.RequireAsync(JobCapability.Complete);
            var steps = await ReadStepsAsync(t.Connection, t.Transaction, jobId, t.Token);
            var (active, unconfirmed) = await _probe.GetAsync(t.Connection, t.Transaction, jobId, t.Token);
            var facts = new CompletionFacts(steps.Values.All(s => s == StepStatus.Done), active, unconfirmed);
            t.Require(JobLifecycle.CanBeginComplete(t.Job.State, facts));

            var version = await t.BumpAsync("State = 'Completing'");
            await t.AuditAsync(version, new AuditEntry("lifecycle", "job.completing", "Started completing the job.", "Job",
                t.Job.PublicId.ToString("D"), Json(new Dictionary<string, object?> { ["state"] = t.Job.State.ToString() }),
                Json(new Dictionary<string, object?> { ["state"] = "Completing" })));
            return await t.ReloadAsync();
        }, refuseIfLockedByAnother: true, requires: JobCapability.Complete);

    public Task<JobRecord> FinishCompleteAsync(long jobId, AuditContext context, CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context, cancellationToken, async t =>
        {
            await t.RequireAsync(JobCapability.Complete);
            t.Require(JobLifecycle.CanFinishComplete(t.Job.State));

            var version = await t.BumpAsync(
                "State = 'Complete', CompletionVersion = CompletionVersion + 1, CompletedUtc = SYSUTCDATETIME(), CompletedBy = @by",
                ("@by", Actor(context)));
            await t.AuditAsync(version, new AuditEntry("lifecycle", "job.completed", $"Completed the job (version {t.Job.CompletionVersion + 1}).",
                "Job", t.Job.PublicId.ToString("D"),
                Json(new Dictionary<string, object?> { ["state"] = "Completing" }),
                Json(new Dictionary<string, object?> { ["state"] = "Complete", ["completionVersion"] = t.Job.CompletionVersion + 1 })));
            return await t.ReloadAsync();
        }, refuseIfLockedByAnother: true, requires: JobCapability.Complete);

    /// <summary>"We are not working on this anymore." A deliberate end for a job that did not run through every step.</summary>
    public Task<JobRecord> CloseAsync(long jobId, string reason, AuditContext context, CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context.WithReason(reason), cancellationToken, async t =>
        {
            await t.RequireAsync(JobCapability.Close);
            var (active, unconfirmed) = await _probe.GetAsync(t.Connection, t.Transaction, jobId, t.Token);
            t.Require(JobLifecycle.CanClose(t.Job.State, reason, new CompletionFacts(false, active, unconfirmed)));

            var version = await t.BumpAsync("State = 'Closed', ClosedUtc = SYSUTCDATETIME(), ClosedBy = @by, CloseReason = @reason",
                ("@by", Actor(context)), ("@reason", reason.Trim()));
            await t.AuditAsync(version, new AuditEntry("lifecycle", "job.closed", "Closed the job.", "Job", t.Job.PublicId.ToString("D"),
                Json(new Dictionary<string, object?> { ["state"] = t.Job.State.ToString() }),
                Json(new Dictionary<string, object?> { ["state"] = "Closed" })));
            return await t.ReloadAsync();
        }, refuseIfLockedByAnother: true, requires: JobCapability.Close);

    public Task<JobRecord> ReopenAsync(long jobId, string reason, AuditContext context, CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context.WithReason(reason), cancellationToken, async t =>
        {
            await t.RequireAsync(JobCapability.Reopen);
            t.Require(JobLifecycle.CanReopen(t.Job.State, reason));

            var version = await t.BumpAsync("State = 'Draft', ReopenedUtc = SYSUTCDATETIME(), ReopenedBy = @by, ReopenReason = @reason",
                ("@by", Actor(context)), ("@reason", reason.Trim()));
            await t.AuditAsync(version, new AuditEntry("lifecycle", "job.reopened", "Reopened the job.", "Job", t.Job.PublicId.ToString("D"),
                Json(new Dictionary<string, object?> { ["state"] = t.Job.State.ToString() }),
                Json(new Dictionary<string, object?> { ["state"] = "Draft" })));
            return await t.ReloadAsync();
        }, refuseIfLockedByAnother: true, requires: JobCapability.Reopen);

    // ------------------------------------------------------------------ helpers

    internal static async Task<int> ExecAsync(SqlConnection c, SqlTransaction? tx, string sql, CancellationToken ct,
        params (string Name, object? Value)[] args)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in args) P(command, name, value);
        return await command.ExecuteNonQueryAsync(ct);
    }

    internal static void P(SqlCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private const int UniqueKeyViolation = 2627;
    private const int UniqueIndexViolation = 2601;

    private static async Task<int> EnsureTagAsync(JobTxn t, string display, string normalized)
    {
        async Task<int?> Find()
        {
            await using var find = t.Connection.CreateCommand();
            find.Transaction = t.Transaction;
            find.CommandText = "SELECT TagId FROM ductwork.Tag WHERE NameNormalized = @n;";
            find.Parameters.AddWithValue("@n", normalized);
            return await find.ExecuteScalarAsync(t.Token) as int?;
        }

        if (await Find() is { } existing) return existing;

        // A savepoint keeps a lost race on the unique key from dooming the whole transaction.
        t.Transaction.Save("tag");
        try
        {
            await using var insert = t.Connection.CreateCommand();
            insert.Transaction = t.Transaction;
            insert.CommandText = "INSERT INTO ductwork.Tag (Name, NameNormalized) VALUES (@d, @n); SELECT CAST(SCOPE_IDENTITY() AS INT);";
            insert.Parameters.AddWithValue("@d", display);
            insert.Parameters.AddWithValue("@n", normalized);
            return (int)(await insert.ExecuteScalarAsync(t.Token))!;
        }
        catch (SqlException ex) when (ex.Number is UniqueKeyViolation or UniqueIndexViolation)
        {
            t.Transaction.Rollback("tag");
            return await Find() ?? throw new InvalidOperationException("Tag vanished after a duplicate-key conflict.", ex);
        }
    }

    private static async Task<IReadOnlyList<string>> TagsAsync(JobTxn t)
    {
        await using var command = t.Connection.CreateCommand();
        command.Transaction = t.Transaction;
        command.CommandText =
            "SELECT g.Name FROM ductwork.JobTag jt JOIN ductwork.Tag g ON g.TagId = jt.TagId WHERE jt.JobId = @j ORDER BY g.NameNormalized;";
        command.Parameters.AddWithValue("@j", t.Job.JobId);
        var tags = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(t.Token);
        while (await reader.ReadAsync(t.Token)) tags.Add(reader.GetString(0));
        return tags;
    }

    /// <summary>Trims, collapses inner spaces and keeps the display spelling. Tags are unique ignoring case.</summary>
    public static (string Display, string Normalized) NormalizeTag(string tag)
    {
        var display = string.Join(' ', (tag ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (display.Length == 0) throw new JobRuleViolationException("A tag cannot be empty.");
        if (display.Length > 100) throw new JobRuleViolationException("A tag can be at most 100 characters.");
        return (display, display.ToLowerInvariant());
    }

    private static string ValidEmail(string email)
    {
        var address = AuditContext.Normalize(email);
        if (address.Length == 0 || !address.Contains('@') || address.Contains(' '))
            throw new JobRuleViolationException($"\"{email}\" is not a valid email address.");
        return address;
    }

    private static string Required(string? value, string message)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) throw new JobRuleViolationException(message);
        return trimmed;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Optional(string? requested, string column, string key, string? current, IReadOnlySet<string> clear,
        Action<string, string, string?, string?> change)
    {
        if (clear.Contains(column) || clear.Contains(key)) change(column, key, current, null);
        else if (requested is not null) change(column, key, current, Blank(requested));
    }

    internal static string Actor(AuditContext context) => context.ActorEmail ?? context.ActorDisplayName ?? "system";

    internal static string Json(Dictionary<string, object?> values) => JsonSerializer.Serialize(values);

    private static string JobColumns(string a) =>
        $"{a}.JobId, {a}.PublicId, {a}.Name, {a}.CustomerName, {a}.ProjectName, {a}.SiteAddress, {a}.OwnerEmail, {a}.State, {a}.StateBeforeArchive, " +
        $"{a}.CompletionVersion, {a}.StateVersion, {a}.ReviewLevel, {a}.BaselineManualHours, {a}.CompletedUtc, {a}.CompletedBy, {a}.ClosedUtc, " +
        $"{a}.ClosedBy, {a}.CloseReason, {a}.ReopenedUtc, {a}.ReopenedBy, {a}.ReopenReason, {a}.CreatedUtc, {a}.CreatedBy, {a}.UpdatedUtc";

    internal static async Task<JobRecord?> ReadJobAsync(SqlConnection c, SqlTransaction? tx, long jobId, bool forUpdate, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT {JobColumns("j")} FROM ductwork.Job j {(forUpdate ? "WITH (UPDLOCK, ROWLOCK)" : "")} WHERE j.JobId = @j;";
        command.Parameters.AddWithValue("@j", jobId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapJob(reader) : null;
    }

    internal static JobRecord MapJob(SqlDataReader r) => new()
    {
        JobId = r.GetInt64(0),
        PublicId = r.GetGuid(1),
        Name = r.GetString(2),
        CustomerName = S(r, 3),
        ProjectName = S(r, 4),
        SiteAddress = S(r, 5),
        OwnerEmail = r.GetString(6),
        State = Enum.Parse<JobState>(r.GetString(7)),
        StateBeforeArchive = r.IsDBNull(8) ? null : Enum.Parse<JobState>(r.GetString(8)),
        CompletionVersion = r.GetInt32(9),
        StateVersion = r.GetInt64(10),
        ReviewLevel = Enum.Parse<ReviewLevel>(r.GetString(11)),
        BaselineManualHours = r.IsDBNull(12) ? null : r.GetDecimal(12),
        CompletedUtc = D(r, 13),
        CompletedBy = S(r, 14),
        ClosedUtc = D(r, 15),
        ClosedBy = S(r, 16),
        CloseReason = S(r, 17),
        ReopenedUtc = D(r, 18),
        ReopenedBy = S(r, 19),
        ReopenReason = S(r, 20),
        CreatedUtc = DateTime.SpecifyKind(r.GetDateTime(21), DateTimeKind.Utc),
        CreatedBy = r.GetString(22),
        UpdatedUtc = DateTime.SpecifyKind(r.GetDateTime(23), DateTimeKind.Utc)
    };

    private static string? S(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static DateTime? D(SqlDataReader r, int i) => r.IsDBNull(i) ? null : DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc);

    internal static async Task<IReadOnlyList<JobMemberRecord>> ReadMembersAsync(SqlConnection c, SqlTransaction? tx, long jobId, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT UserEmail, Role, AddedUtc, AddedBy FROM ductwork.JobMember WHERE JobId = @j ORDER BY AddedUtc, UserEmail;";
        command.Parameters.AddWithValue("@j", jobId);
        var members = new List<JobMemberRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            members.Add(new JobMemberRecord(jobId, reader.GetString(0), Enum.Parse<JobRole>(reader.GetString(1)),
                DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc), reader.GetString(3)));
        return members;
    }

    internal static async Task<IReadOnlyDictionary<string, StepStatus>> ReadStepsAsync(SqlConnection c, SqlTransaction? tx, long jobId, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT Step, Status FROM ductwork.JobWorkflowStep WHERE JobId = @j;";
        command.Parameters.AddWithValue("@j", jobId);
        var steps = new Dictionary<string, StepStatus>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) steps[reader.GetString(0)] = Enum.Parse<StepStatus>(reader.GetString(1));
        return steps;
    }
}
