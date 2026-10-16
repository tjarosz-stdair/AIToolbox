using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Jobs;

/// <summary>Marks a public method that changes data without writing a job audit event, and says why. Each one is deliberate and listed by a test.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class NoAuditAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}

/// <summary>Lock timings (plan section 9). All three are meant to be administrator-configurable.</summary>
public sealed record LockSettings
{
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan Expiry { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>How long a takeover request waits for the holder before it is granted.</summary>
    public TimeSpan TakeoverTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

public enum PreemptStatus { Pending, Granted, Cancelled, Denied, Expired }

public enum LockOutcome { Acquired, AlreadyYours, HeldByOther }

public sealed record EditLockInfo(
    long JobId, string HolderEmail, string? HolderDisplayName, string HolderSessionId, long FencingToken,
    DateTime AcquiredUtc, DateTime LastHeartbeatUtc, DateTime ExpiresUtc, bool IsExpired);

public sealed record PreemptRequestRecord(
    long PreemptRequestId, long JobId, string RequesterEmail, string? RequesterDisplayName, string RequesterSessionId,
    DateTime RequestedUtc, DateTime ExpiresUtc, PreemptStatus Status, DateTime? ResolvedUtc, string? ResolvedBy);

/// <summary>
/// Proof that a session holds a job's edit lock, issued only by <see cref="EditLockService"/> and
/// required by every content edit. The fencing token changes whenever the lock changes hands, so a late
/// write from a session that was taken over is refused even if its browser never learned of it.
/// </summary>
public sealed class EditToken
{
    internal EditToken(long jobId, string sessionId, long fencingToken)
    {
        JobId = jobId;
        SessionId = sessionId;
        FencingToken = fencingToken;
    }

    public long JobId { get; }
    public string SessionId { get; }
    public long FencingToken { get; }

    public override string ToString() => $"EditToken(job {JobId}, token {FencingToken})";
}

public sealed record LockResult(LockOutcome Outcome, EditToken? Token, EditLockInfo? Holder);

public sealed record TakeoverResult(bool Granted, EditToken? Token, PreemptRequestRecord? Request, EditLockInfo? Holder, string Message);

public sealed record HeartbeatResult(bool StillHolder, PreemptRequestRecord? PendingRequest, string? LostMessage);

public sealed record LockStatus(EditLockInfo? Lock, EditToken? YourToken, PreemptRequestRecord? PendingRequest)
{
    public bool YouHoldIt => YourToken is not null;
}

public sealed class StaleEditTokenException(string message) : InvalidOperationException(message);

public sealed class JobLockedException(string holder)
    : InvalidOperationException($"{holder} is editing this job. Ask them to finish, or take over editing.");

/// <summary>
/// The single-editor lock (plan section 9). One session edits a job at a time. The lock lives in SQL and
/// expiry is judged by the database clock, so it does not depend on any web server's clock.
/// </summary>
public sealed class EditLockService
{
    private readonly Func<CancellationToken, Task<SqlConnection>> _open;
    private readonly LockSettings _settings;

    public EditLockService(Func<CancellationToken, Task<SqlConnection>> openConnection, LockSettings? settings = null)
    {
        _open = openConnection;
        _settings = settings ?? new LockSettings();
    }

    public EditLockService(string connectionString, LockSettings? settings = null)
        : this(async ct =>
        {
            var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);
            return connection;
        }, settings)
    {
    }

    public LockSettings Settings => _settings;

    // ------------------------------------------------------------------ reads

    /// <summary>Who holds the lock and whether a takeover is waiting. If <paramref name="sessionId"/> holds it, the result carries that session's token.</summary>
    public async Task<LockStatus> GetStatusAsync(long jobId, string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        var info = await ReadLockAsync(connection, null, jobId, forUpdate: false, cancellationToken);
        var pending = await ReadPendingAsync(connection, null, jobId, cancellationToken);
        EditToken? mine = info is { IsExpired: false } && info.HolderSessionId == sessionId
            ? new EditToken(jobId, sessionId, info.FencingToken)
            : null;
        return new LockStatus(info, mine, pending);
    }

    // ------------------------------------------------------------------ acquire and release

    /// <summary>
    /// Takes the lock if it is free, expired, or already this session's. Otherwise reports who holds it
    /// and changes nothing. A different session of the same person does not get it.
    /// </summary>
    public Task<LockResult> AcquireAsync(long jobId, AuditContext context, CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context, cancellationToken, async t =>
        {
            var session = RequireSession(context);
            await t.RequireAsync(JobCapability.Edit);
            t.RequireEditable();

            var current = await ReadLockAsync(t.Connection, t.Transaction, jobId, forUpdate: true, t.Token);
            if (current is not null && !current.IsExpired)
            {
                return current.HolderSessionId == session
                    ? new LockResult(LockOutcome.AlreadyYours, new EditToken(jobId, session, current.FencingToken), current)
                    : new LockResult(LockOutcome.HeldByOther, null, current);
            }

            var token = await WriteLockAsync(t, context, session);
            var summary = current is null
                ? "Took the editing lock."
                : $"Took the editing lock after {current.HolderEmail}'s session expired.";
            await t.AuditAtCurrentVersionAsync(new AuditEntry("lock", current is null ? "lock.acquired" : "lock.acquired-after-expiry", summary, "Lock",
                null, current is null ? null : Describe(current), Describe(context, token)));
            return new LockResult(LockOutcome.Acquired, new EditToken(jobId, session, token), null);
        });

    /// <summary>Gives the lock up. Does nothing, and writes nothing, if this session no longer holds it.</summary>
    public Task<bool> ReleaseAsync(EditToken editToken, AuditContext context, CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, editToken.JobId, context, cancellationToken, async t =>
        {
            var current = await ReadLockAsync(t.Connection, t.Transaction, editToken.JobId, forUpdate: true, t.Token);
            if (current is null || current.FencingToken != editToken.FencingToken || current.HolderSessionId != editToken.SessionId)
                return false;

            await JobRepository.ExecAsync(t.Connection, t.Transaction, "DELETE FROM ductwork.JobEditLock WHERE JobId = @j;", t.Token, ("@j", editToken.JobId));
            await t.AuditAtCurrentVersionAsync(new AuditEntry("lock", "lock.released", "Released the editing lock.", "Lock", null, Describe(current), null));
            return true;
        });

    /// <summary>
    /// Keeps the lock alive. This runs every few seconds per open editor and is not job data, so it is not
    /// audited. Returns whether this session still holds the lock and, if so, any waiting takeover request, so the holder
    /// sees it within one heartbeat.
    /// </summary>
    [NoAudit("A heartbeat only extends the lock's expiry. It is high-frequency liveness, not a change to job data.")]
    public async Task<HeartbeatResult> HeartbeatAsync(EditToken editToken, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
            UPDATE ductwork.JobEditLock
            SET LastHeartbeatUtc = @now, ExpiresUtc = DATEADD(MILLISECOND, @expiryMs, @now)
            WHERE JobId = @j AND HolderSessionId = @s AND FencingToken = @t AND ExpiresUtc > @now;
            SELECT @@ROWCOUNT;
            """;
        command.Parameters.AddWithValue("@expiryMs", (int)_settings.Expiry.TotalMilliseconds);
        command.Parameters.AddWithValue("@j", editToken.JobId);
        command.Parameters.AddWithValue("@s", editToken.SessionId);
        command.Parameters.AddWithValue("@t", editToken.FencingToken);
        var updated = (int)(await command.ExecuteScalarAsync(cancellationToken))!;

        if (updated == 1)
            return new HeartbeatResult(true, await ReadPendingAsync(connection, null, editToken.JobId, cancellationToken), null);

        var now = await ReadLockAsync(connection, null, editToken.JobId, forUpdate: false, cancellationToken);
        var message = now is null
            ? "Your editing session ended."
            : now.IsExpired && now.HolderSessionId == editToken.SessionId
                ? "Your editing session expired."
                : $"Editing was taken over by {now.HolderDisplayName ?? now.HolderEmail}.";
        return new HeartbeatResult(false, null, message);
    }

    // ------------------------------------------------------------------ takeover

    /// <summary>
    /// Asks to take over editing. A free or expired lock is taken at once. If a session is actively
    /// editing, a request is created for them to answer, and it is granted anyway if they do not answer in time.
    /// </summary>
    public Task<TakeoverResult> RequestTakeoverAsync(long jobId, AuditContext context, CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context, cancellationToken, async t =>
        {
            var session = RequireSession(context);
            await t.RequireAsync(JobCapability.Edit);
            t.RequireEditable();

            var current = await ReadLockAsync(t.Connection, t.Transaction, jobId, forUpdate: true, t.Token);
            if (current is not null && !current.IsExpired && current.HolderSessionId == session)
                return new TakeoverResult(true, new EditToken(jobId, session, current.FencingToken), null, null, "You already hold the lock.");

            if (current is null || current.IsExpired)
            {
                var token = await WriteLockAsync(t, context, session);
                await t.AuditAtCurrentVersionAsync(new AuditEntry("lock", "takeover.stale", current is null
                        ? "Took the editing lock."
                        : $"Took over editing from {current.HolderEmail}, whose session had expired.",
                    "Lock", null, current is null ? null : Describe(current), Describe(context, token)));
                return new TakeoverResult(true, new EditToken(jobId, session, token), null, null, "Editing taken over.");
            }

            var pending = await ReadPendingAsync(t.Connection, t.Transaction, jobId, t.Token);
            if (pending is not null)
            {
                if (pending.RequesterSessionId == session)
                    return new TakeoverResult(false, null, pending, current, "Your request is waiting for an answer.");
                throw new JobRuleViolationException($"{pending.RequesterDisplayName ?? pending.RequesterEmail} has already asked to take over editing.");
            }

            var requestId = await InsertRequestAsync(t, context, session);
            var request = (await ReadRequestAsync(t.Connection, t.Transaction, requestId, t.Token))!;
            await t.AuditAtCurrentVersionAsync(new AuditEntry("lock", "takeover.requested",
                $"Asked to take over editing from {current.HolderDisplayName ?? current.HolderEmail}.", "Request", requestId.ToString(), null, Describe(request)));
            return new TakeoverResult(false, null, request, current, "Waiting for the current editor to respond.");
        });

    /// <summary>The requester withdraws a waiting request.</summary>
    public Task<bool> CancelTakeoverAsync(long jobId, long requestId, AuditContext context, CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context, cancellationToken, async t =>
        {
            var session = RequireSession(context);
            var request = await ReadRequestAsync(t.Connection, t.Transaction, requestId, t.Token);
            if (request is null || request.JobId != jobId || request.Status != PreemptStatus.Pending || request.RequesterSessionId != session)
                return false;

            await ResolveRequestAsync(t, requestId, PreemptStatus.Cancelled, context);
            await t.AuditAtCurrentVersionAsync(new AuditEntry("lock", "takeover.cancelled", "Withdrew the request to take over editing.", "Request", requestId.ToString(), null, null));
            return true;
        });

    /// <summary>The current editor answers: hand over now, or keep editing.</summary>
    public Task<TakeoverResult> RespondToTakeoverAsync(EditToken holder, long requestId, bool handOver, AuditContext context,
        CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, holder.JobId, context, cancellationToken, async t =>
        {
            var current = await ReadLockAsync(t.Connection, t.Transaction, holder.JobId, forUpdate: true, t.Token);
            if (current is null || current.FencingToken != holder.FencingToken || current.HolderSessionId != holder.SessionId)
                throw new StaleEditTokenException("You no longer hold the editing lock.");
            var request = await ReadRequestAsync(t.Connection, t.Transaction, requestId, t.Token);
            if (request is null || request.JobId != holder.JobId || request.Status != PreemptStatus.Pending)
                return new TakeoverResult(false, null, request, current, "That request is no longer waiting.");

            if (!handOver)
            {
                await ResolveRequestAsync(t, requestId, PreemptStatus.Denied, context);
                await t.AuditAtCurrentVersionAsync(new AuditEntry("lock", "takeover.denied",
                    $"Kept editing; declined {request.RequesterEmail}'s request to take over.", "Request", requestId.ToString(), null, null));
                return new TakeoverResult(false, null, request, current, "You kept editing.");
            }

            await ResolveRequestAsync(t, requestId, PreemptStatus.Granted, context);
            var token = await GrantToRequesterAsync(t, request);
            await t.AuditAtCurrentVersionAsync(new AuditEntry("lock", "takeover.granted",
                $"Handed editing over to {request.RequesterEmail}.", "Request", requestId.ToString(), Describe(current),
                Describe(request.RequesterEmail, request.RequesterSessionId, token)));
            return new TakeoverResult(true, null, request, null, "Handed over.");
        });

    /// <summary>
    /// Grants any waiting request whose time ran out, because an open but unattended tab must not block a
    /// job forever. Called by a poll or a background timer; safe to call at any time.
    /// </summary>
    public Task<PreemptRequestRecord?> AdvanceAsync(long jobId, AuditContext context, CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context, cancellationToken, async t =>
        {
            var pending = await ReadPendingAsync(t.Connection, t.Transaction, jobId, t.Token);
            if (pending is null) return null;

            await using var due = t.Connection.CreateCommand();
            due.Transaction = t.Transaction;
            due.CommandText = "SELECT CASE WHEN @e <= SYSUTCDATETIME() THEN 1 ELSE 0 END;";
            due.Parameters.AddWithValue("@e", pending.ExpiresUtc);
            if ((int)(await due.ExecuteScalarAsync(t.Token))! == 0) return null;

            var current = await ReadLockAsync(t.Connection, t.Transaction, jobId, forUpdate: true, t.Token);
            await ResolveRequestAsync(t, pending.PreemptRequestId, PreemptStatus.Granted, context);
            var token = await GrantToRequesterAsync(t, pending);
            await t.AuditAtCurrentVersionAsync(new AuditEntry("lock", "takeover.granted",
                $"{pending.RequesterEmail} took over editing because the current editor did not respond in time.", "Request",
                pending.PreemptRequestId.ToString(), current is null ? null : Describe(current), Describe(pending.RequesterEmail, pending.RequesterSessionId, token)));
            return (PreemptRequestRecord?)await ReadRequestAsync(t.Connection, t.Transaction, pending.PreemptRequestId, t.Token);
        });

    /// <summary>
    /// An owner or an AIToolbox administrator removes whoever is editing. An owner or editor who forces it
    /// takes the lock. An administrator who cannot edit the job just frees it, since holding it would be
    /// no use to them. Always audited. Returns the forcer's token, or null if the lock was only freed.
    /// </summary>
    public Task<EditToken?> ForceTakeoverAsync(long jobId, AuditContext context, CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context, cancellationToken, async t =>
        {
            var session = RequireSession(context);
            var (role, _) = await JobAccess.LoadAsync(t.Connection, t.Transaction, jobId, context.ActorEmail!, t.Token);
            if (role != JobRole.Owner && !context.IsAdmin)
                throw new JobAccessDeniedException("Only a job owner or an administrator can force a takeover.");
            t.RequireEditable();

            var current = await ReadLockAsync(t.Connection, t.Transaction, jobId, forUpdate: true, t.Token);
            var pending = await ReadPendingAsync(t.Connection, t.Transaction, jobId, t.Token);
            if (pending is not null) await ResolveRequestAsync(t, pending.PreemptRequestId, PreemptStatus.Cancelled, context);

            if (!JobPermissions.HasEditPermission(role))
            {
                await JobRepository.ExecAsync(t.Connection, t.Transaction, "DELETE FROM ductwork.JobEditLock WHERE JobId = @j;", t.Token, ("@j", jobId));
                if (current is not null)
                    await t.AuditAtCurrentVersionAsync(new AuditEntry("lock", "takeover.forced",
                        $"Administrator freed the editing lock held by {current.HolderEmail}.", "Lock", null, Describe(current), null));
                return null;
            }

            var token = await WriteLockAsync(t, context, session);
            await t.AuditAtCurrentVersionAsync(new AuditEntry("lock", "takeover.forced",
                current is null ? "Took the editing lock by force." : $"Forced a takeover from {current.HolderEmail}.", "Lock", null,
                current is null ? null : Describe(current), Describe(context, token)));
            return new EditToken(jobId, session, token);
        });

    /// <summary>
    /// Turns the fencing token a browser sent with a write into the token the data layer needs, if that
    /// session still holds the lock under exactly that token. A browser holding an older token (the lock
    /// changed hands since it last looked, even back to the same session) is refused and the refusal is
    /// audited. The token proves the write was made from the state the editor last saw.
    /// </summary>
    public Task<EditToken> ResolveTokenAsync(long jobId, long claimedFencingToken, AuditContext context, CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context, cancellationToken, async t =>
        {
            var session = RequireSession(context);
            var current = await ReadLockAsync(t.Connection, t.Transaction, jobId, forUpdate: false, t.Token);
            if (current is null)
                throw new StaleEditTokenException("You no longer hold the editing lock. Reload the job to continue.");
            if (current.HolderSessionId != session || current.FencingToken != claimedFencingToken)
                throw new StaleEditTokenException(
                    $"Editing was taken over by {current.HolderDisplayName ?? current.HolderEmail}. Your view may be out of date; reload the latest.");
            if (current.IsExpired)
                throw new StaleEditTokenException("Your editing session expired. Reload the job to continue.");
            return new EditToken(jobId, session, current.FencingToken);
        }, requires: JobCapability.Edit);

    // ------------------------------------------------------------------ used by every content edit

    internal static async Task RequireValidTokenAsync(JobTxn t, EditToken editToken)
    {
        if (editToken.JobId != t.Job.JobId)
            throw new StaleEditTokenException("That edit token is for a different job.");
        if (t.Context.SessionId != editToken.SessionId)
            throw new StaleEditTokenException("That edit token belongs to a different session.");

        var current = await ReadLockAsync(t.Connection, t.Transaction, t.Job.JobId, forUpdate: true, t.Token);
        if (current is null)
            throw new StaleEditTokenException("You no longer hold the editing lock. Reload the job to continue.");
        if (current.FencingToken != editToken.FencingToken || current.HolderSessionId != editToken.SessionId)
            throw new StaleEditTokenException(
                $"Editing was taken over by {current.HolderDisplayName ?? current.HolderEmail}. Your view may be out of date; reload the latest.");
        if (current.IsExpired)
            throw new StaleEditTokenException("Your editing session expired. Reload the job to continue.");
    }

    /// <summary>Completing, closing and reopening need the lock: refuse while another live session holds it.</summary>
    internal static async Task RequireNoForeignLockAsync(JobTxn t)
    {
        var current = await ReadLockAsync(t.Connection, t.Transaction, t.Job.JobId, forUpdate: true, t.Token);
        if (current is { IsExpired: false } && current.HolderSessionId != t.Context.SessionId)
            throw new JobLockedException(current.HolderDisplayName ?? current.HolderEmail);
    }

    internal static async Task RecordRejectedWriteAsync(
        Func<CancellationToken, Task<SqlConnection>> open, long jobId, AuditContext context, StaleEditTokenException stale, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await open(cancellationToken);
            await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var job = await JobRepository.ReadJobAsync(connection, tx, jobId, forUpdate: true, cancellationToken);
            if (job is null) return;
            await AuditLog.AppendAsync(connection, tx, jobId, job.StateVersion,
                new AuditEntry("lock", "write.rejected", "A change was refused because its editing token was stale: " + stale.Message, "Lock"),
                context, cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch (SqlException)
        {
            // Recording the refusal must never hide the refusal itself.
        }
    }

    // ------------------------------------------------------------------ SQL

    private static string RequireSession(AuditContext context)
    {
        if (context.ActorType != ActorType.User || string.IsNullOrWhiteSpace(context.SessionId))
            throw new ArgumentException("An editing lock belongs to a user's browser session, so the context needs a session id.", nameof(context));
        return context.SessionId;
    }

    private async Task<long> WriteLockAsync(JobTxn t, AuditContext context, string session) =>
        await WriteLockAsync(t, context.ActorEmail!, context.ActorDisplayName, session);

    private async Task<long> WriteLockAsync(JobTxn t, string email, string? displayName, string session)
    {
        await using var command = t.Connection.CreateCommand();
        command.Transaction = t.Transaction;
        command.CommandText =
            """
            DECLARE @token BIGINT = NEXT VALUE FOR ductwork.FencingTokenSeq;
            DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
            UPDATE ductwork.JobEditLock
            SET HolderUserEmail = @email, HolderDisplayName = @name, HolderSessionId = @session, FencingToken = @token,
                AcquiredUtc = @now, LastHeartbeatUtc = @now, ExpiresUtc = DATEADD(MILLISECOND, @expiryMs, @now)
            WHERE JobId = @j;
            IF @@ROWCOUNT = 0
                INSERT INTO ductwork.JobEditLock (JobId, HolderUserEmail, HolderDisplayName, HolderSessionId, FencingToken, AcquiredUtc, LastHeartbeatUtc, ExpiresUtc)
                VALUES (@j, @email, @name, @session, @token, @now, @now, DATEADD(MILLISECOND, @expiryMs, @now));
            SELECT @token;
            """;
        command.Parameters.AddWithValue("@j", t.Job.JobId);
        command.Parameters.AddWithValue("@email", email);
        command.Parameters.AddWithValue("@name", (object?)displayName ?? DBNull.Value);
        command.Parameters.AddWithValue("@session", session);
        command.Parameters.AddWithValue("@expiryMs", (int)_settings.Expiry.TotalMilliseconds);
        return (long)(await command.ExecuteScalarAsync(t.Token))!;
    }

    private async Task<long> InsertRequestAsync(JobTxn t, AuditContext context, string session)
    {
        await using var command = t.Connection.CreateCommand();
        command.Transaction = t.Transaction;
        command.CommandText =
            """
            DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
            INSERT INTO ductwork.PreemptRequest (JobId, RequesterEmail, RequesterDisplayName, RequesterSessionId, RequestedUtc, ExpiresUtc)
            VALUES (@j, @email, @name, @session, @now, DATEADD(MILLISECOND, @timeoutMs, @now));
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);
            """;
        command.Parameters.AddWithValue("@j", t.Job.JobId);
        command.Parameters.AddWithValue("@email", context.ActorEmail!);
        command.Parameters.AddWithValue("@name", (object?)context.ActorDisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("@session", session);
        command.Parameters.AddWithValue("@timeoutMs", (int)_settings.TakeoverTimeout.TotalMilliseconds);
        return (long)(await command.ExecuteScalarAsync(t.Token))!;
    }

    private async Task<long> GrantToRequesterAsync(JobTxn t, PreemptRequestRecord request) =>
        await WriteLockAsync(t, request.RequesterEmail, request.RequesterDisplayName, request.RequesterSessionId);

    private static Task ResolveRequestAsync(JobTxn t, long requestId, PreemptStatus status, AuditContext context) =>
        JobRepository.ExecAsync(t.Connection, t.Transaction,
            "UPDATE ductwork.PreemptRequest SET Status = @s, ResolvedUtc = SYSUTCDATETIME(), ResolvedBy = @by WHERE PreemptRequestId = @id AND Status = 'Pending';",
            t.Token, ("@s", status.ToString()), ("@by", context.ActorEmail ?? context.ActorDisplayName ?? "system"), ("@id", requestId));

    private const string LockColumns =
        "JobId, HolderUserEmail, HolderDisplayName, HolderSessionId, FencingToken, AcquiredUtc, LastHeartbeatUtc, ExpiresUtc, " +
        "CASE WHEN ExpiresUtc <= SYSUTCDATETIME() THEN 1 ELSE 0 END";

    internal static async Task<EditLockInfo?> ReadLockAsync(SqlConnection c, SqlTransaction? tx, long jobId, bool forUpdate, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT {LockColumns} FROM ductwork.JobEditLock {(forUpdate ? "WITH (UPDLOCK, ROWLOCK)" : "")} WHERE JobId = @j;";
        command.Parameters.AddWithValue("@j", jobId);
        await using var r = await command.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new EditLockInfo(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetInt64(4),
            Utc(r, 5), Utc(r, 6), Utc(r, 7), r.GetInt32(8) == 1);
    }

    private const string RequestColumns =
        "PreemptRequestId, JobId, RequesterEmail, RequesterDisplayName, RequesterSessionId, RequestedUtc, ExpiresUtc, Status, ResolvedUtc, ResolvedBy";

    private static async Task<PreemptRequestRecord?> ReadPendingAsync(SqlConnection c, SqlTransaction? tx, long jobId, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT TOP 1 {RequestColumns} FROM ductwork.PreemptRequest WHERE JobId = @j AND Status = 'Pending';";
        command.Parameters.AddWithValue("@j", jobId);
        return await ReadRequestAsync(command, ct);
    }

    private static async Task<PreemptRequestRecord?> ReadRequestAsync(SqlConnection c, SqlTransaction? tx, long requestId, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT {RequestColumns} FROM ductwork.PreemptRequest WHERE PreemptRequestId = @id;";
        command.Parameters.AddWithValue("@id", requestId);
        return await ReadRequestAsync(command, ct);
    }

    private static async Task<PreemptRequestRecord?> ReadRequestAsync(SqlCommand command, CancellationToken ct)
    {
        await using var r = await command.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new PreemptRequestRecord(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4),
            Utc(r, 5), Utc(r, 6), Enum.Parse<PreemptStatus>(r.GetString(7)), r.IsDBNull(8) ? null : Utc(r, 8), r.IsDBNull(9) ? null : r.GetString(9));
    }

    private static DateTime Utc(SqlDataReader r, int i) => DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc);

    private static string Describe(EditLockInfo l) => JobRepository.Json(new Dictionary<string, object?>
        { ["holder"] = l.HolderEmail, ["session"] = l.HolderSessionId, ["fencingToken"] = l.FencingToken });

    private static string Describe(AuditContext c, long token) => Describe(c.ActorEmail!, c.SessionId, token);

    private static string Describe(string email, string? session, long token) => JobRepository.Json(new Dictionary<string, object?>
        { ["holder"] = email, ["session"] = session, ["fencingToken"] = token });

    private static string Describe(PreemptRequestRecord r) => JobRepository.Json(new Dictionary<string, object?>
        { ["requester"] = r.RequesterEmail, ["session"] = r.RequesterSessionId, ["expiresUtc"] = r.ExpiresUtc.ToString("O") });
}
