using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Jobs;

/// <summary>
/// One transaction on one job. Every data-layer change in Ductwork runs through here, so each gets the
/// same guarantees: the job row is locked for the whole change (changes to a job are serialized, its
/// audit events are numbered and chained in order, the version checked is the version changed),
/// permission is checked from the identity in the <see cref="AuditContext"/>, and the change and its
/// audit event commit together or not at all.
/// </summary>
internal sealed class JobTxn(SqlConnection connection, SqlTransaction transaction, JobRecord job, AuditContext context, CancellationToken token)
{
    public SqlConnection Connection { get; } = connection;
    public SqlTransaction Transaction { get; } = transaction;
    public JobRecord Job { get; private set; } = job;
    public AuditContext Context { get; } = context;
    public CancellationToken Token { get; } = token;

    /// <param name="editToken">For content edits: the fencing token the caller was issued. A stale one is refused and the refusal audited.</param>
    /// <param name="requires">The capability the caller needs, checked before anything else.</param>
    /// <param name="refuseIfLockedByAnother">For completing and closing, which need the edit lock: refuse while another session holds it.</param>
    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<SqlConnection>> open, long jobId, AuditContext context, CancellationToken cancellationToken,
        Func<JobTxn, Task<T>> body, EditToken? editToken = null, bool refuseIfLockedByAnother = false, JobCapability? requires = null)
    {
        context.EnsureValid();
        await using var connection = await open(cancellationToken);
        try
        {
            await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var job = await JobRepository.ReadJobAsync(connection, tx, jobId, forUpdate: true, cancellationToken)
                    ?? throw new JobNotFoundException(jobId);
                var txn = new JobTxn(connection, tx, job, context, cancellationToken);

                // Permission comes first, so someone who may not touch the job learns nothing about who is editing it.
                if (requires is { } capability) await txn.RequireAsync(capability);
                if (editToken is not null) await EditLockService.RequireValidTokenAsync(txn, editToken);
                if (refuseIfLockedByAnother) await EditLockService.RequireNoForeignLockAsync(txn);

                var result = await body(txn);
                await tx.CommitAsync(cancellationToken);
                return result;
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        catch (StaleEditTokenException stale)
        {
            // The refused write is itself part of the record (plan section 10), so it is logged on its own
            // after the change was rolled back.
            await EditLockService.RecordRejectedWriteAsync(open, jobId, context, stale, cancellationToken);
            throw;
        }
    }

    public async Task RequireAsync(JobCapability capability)
    {
        // Server-side actors (the system, the worker) are not users and are not subject to membership.
        if (Context.ActorType != ActorType.User) return;

        var (role, manager) = await JobAccess.LoadAsync(Connection, Transaction, Job.JobId, Context.ActorEmail!, Token);
        if (!JobPermissions.Can(role, manager, capability))
            throw new JobAccessDeniedException($"You do not have permission to do that on this job ({capability}).");
    }

    public void Require(Decision decision)
    {
        if (!decision.Allowed) throw new JobRuleViolationException(decision.Reason!);
    }

    public void RequireEditable()
    {
        if (!JobLifecycle.IsEditable(Job.State))
            throw new JobRuleViolationException($"This job is {Job.State} and cannot be edited. Reopen it to make changes.");
    }

    public void RequireMetadataChange()
    {
        if (!JobLifecycle.AllowsMetadataChange(Job.State))
            throw new JobRuleViolationException($"This job is {Job.State} and is being held still. Try again when that finishes.");
    }

    public async Task<long> BumpAsync(string extraSet, params (string Name, object? Value)[] args)
    {
        await using var command = Connection.CreateCommand();
        command.Transaction = Transaction;
        command.CommandText =
            $"UPDATE ductwork.Job SET StateVersion = StateVersion + 1, UpdatedUtc = SYSUTCDATETIME(){(extraSet.Length == 0 ? "" : ", " + extraSet)} WHERE JobId = @j; " +
            "SELECT StateVersion FROM ductwork.Job WHERE JobId = @j;";
        command.Parameters.AddWithValue("@j", Job.JobId);
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return (long)(await command.ExecuteScalarAsync(Token))!;
    }

    /// <summary>Appends an event at the job's current version, for changes that are not changes to the job's content (locks, tasks).</summary>
    public Task AuditAtCurrentVersionAsync(AuditEntry entry) => AuditAsync(Job.StateVersion, entry);

    /// <summary>Appends an event that belongs to a processing task, recording the task and the producer on it.</summary>
    public Task AuditForTaskAsync(AuditEntry entry, long processingTaskId, long producerId) =>
        AuditLog.AppendAsync(Connection, Transaction, Job.JobId, Job.StateVersion, entry,
            Context with { ProcessingTaskId = processingTaskId, ProducerId = producerId }, Token);

    public Task AuditAsync(long version, AuditEntry entry) =>
        AuditLog.AppendAsync(Connection, Transaction, Job.JobId, version, entry, Context, Token);

    public async Task<JobRecord> ReloadAsync()
    {
        Job = (await JobRepository.ReadJobAsync(Connection, Transaction, Job.JobId, forUpdate: false, Token))!;
        return Job;
    }

    /// <summary>Keeps the job's primary owner pointing at a member who still owns it.</summary>
    public async Task SyncOwnerAsync()
    {
        await JobRepository.ExecAsync(Connection, Transaction,
            """
            UPDATE j SET OwnerEmail = (SELECT TOP 1 UserEmail FROM ductwork.JobMember m WHERE m.JobId = j.JobId AND m.Role = 'Owner' ORDER BY m.AddedUtc, m.UserEmail)
            FROM ductwork.Job j
            WHERE j.JobId = @j
              AND NOT EXISTS (SELECT 1 FROM ductwork.JobMember m WHERE m.JobId = j.JobId AND m.Role = 'Owner' AND m.UserEmail = j.OwnerEmail);
            """, Token, ("@j", Job.JobId));
    }
}

/// <summary>Looks up what a person may do on a job: their membership and whether they hold the Job Manager permission.</summary>
internal static class JobAccess
{
    public static async Task<(JobRole? Role, bool IsJobManager)> LoadAsync(
        SqlConnection connection, SqlTransaction? transaction, long jobId, string email, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT (SELECT Role FROM ductwork.JobMember WHERE JobId = @j AND UserEmail = @e),
                   CASE WHEN EXISTS (SELECT 1 FROM ductwork.ToolPermission WHERE UserEmail = @e AND Permission = 'JobManager') THEN 1 ELSE 0 END;
            """;
        command.Parameters.AddWithValue("@j", jobId);
        command.Parameters.AddWithValue("@e", email);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        JobRole? role = reader.IsDBNull(0) ? null : Enum.Parse<JobRole>(reader.GetString(0));
        return (role, reader.GetInt32(1) == 1);
    }
}
