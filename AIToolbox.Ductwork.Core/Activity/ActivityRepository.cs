using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Jobs;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Activity;

public sealed record ActivitySessionRecord(
    string SessionId, long JobId, string UserEmail, string? WorkflowStep, string? Surface, string? ReviewLevel,
    DateTime StartedUtc, DateTime LastHeartbeatUtc, DateTime? EndedUtc, double ActiveSeconds, double IdleSeconds, double UncreditedSeconds, int Heartbeats);

/// <summary>Human time on a job, by person and workflow step, for the ROI summary (plan section 10.1).</summary>
public sealed record JobActivitySummary(long JobId, double ActiveSeconds, double IdleSeconds, int Sessions,
    IReadOnlyList<ActivityBucket> ByUser, IReadOnlyList<ActivityBucket> ByStep);

public sealed record ActivityBucket(string Key, double ActiveSeconds, double IdleSeconds, int Sessions);

/// <summary>
/// Persists time on job. The browser only reports whether the person is active or idle; the server
/// measures every duration from its own clock at each heartbeat, so the client cannot invent time, and a
/// closed laptop credits at most two heartbeat intervals. The crediting rule is shared with the in-memory
/// <see cref="ActivityAccumulator"/>.
/// </summary>
public sealed class ActivityRepository
{
    private readonly Func<CancellationToken, Task<SqlConnection>> _open;
    private readonly TimeSpan _maxCreditedGap;

    public ActivityRepository(Func<CancellationToken, Task<SqlConnection>> openConnection, TimeSpan? heartbeatInterval = null)
    {
        _open = openConnection;
        _maxCreditedGap = (heartbeatInterval ?? ActivityAccumulator.DefaultHeartbeatInterval) * 2;
    }

    public ActivityRepository(string connectionString, TimeSpan? heartbeatInterval = null)
        : this(async ct =>
        {
            var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);
            return connection;
        }, heartbeatInterval)
    {
    }

    /// <summary>
    /// One heartbeat from an open job. Anyone who can view the job is measured, including viewers: reviewing
    /// is part of the work. Not audited: it is high-frequency liveness, and the session totals are the durable record.
    /// </summary>
    [NoAudit("A time-on-job heartbeat is high-frequency liveness. The session totals are the durable record, and each session is tied to its job and user.")]
    public async Task<ActivitySessionRecord> RecordHeartbeatAsync(long jobId, AuditContext context, ActivityState state, WorkflowStep? step, string? surface,
        ReviewLevel? reviewLevel, CancellationToken cancellationToken = default)
    {
        var (email, session) = Identify(context);
        await using var connection = await _open(cancellationToken);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (await JobRepository.ReadJobAsync(connection, tx, jobId, forUpdate: false, cancellationToken) is null) throw new JobNotFoundException(jobId);
            var (role, manager) = await JobAccess.LoadAsync(connection, tx, jobId, email, cancellationToken);
            if (!JobPermissions.Can(role, manager, JobCapability.View))
                throw new JobAccessDeniedException("You do not have access to this job.");

            var existing = await ReadAsync(connection, tx, jobId, session, forUpdate: true, cancellationToken);
            // A session id belongs to one person. If another person presents it, their time must not be credited to the first.
            if (existing is not null && !string.Equals(existing.UserEmail, email, StringComparison.OrdinalIgnoreCase))
                throw new JobRuleViolationException("That session belongs to a different person.");
            if (existing is null)
            {
                await JobRepository.ExecAsync(connection, tx,
                    """
                    INSERT INTO ductwork.ActivitySession (SessionId, JobId, UserEmail, WorkflowStep, Surface, ReviewLevel, StartedUtc, LastHeartbeatUtc, Heartbeats)
                    VALUES (@s, @j, @e, @step, @surface, @level, SYSUTCDATETIME(), SYSUTCDATETIME(), 1);
                    """, cancellationToken, ("@s", session), ("@j", jobId), ("@e", email), ("@step", step?.ToString()),
                    ("@surface", Trim(surface, 20)), ("@level", reviewLevel?.ToString()));
            }
            else if (existing.EndedUtc is null)
            {
                await using var now = connection.CreateCommand();
                now.Transaction = tx;
                now.CommandText = "SELECT SYSUTCDATETIME();";
                var at = (DateTime)(await now.ExecuteScalarAsync(cancellationToken))!;
                var (active, idle, uncredited) = ActivityAccumulator.Credit(at - existing.LastHeartbeatUtc, state, _maxCreditedGap);

                await JobRepository.ExecAsync(connection, tx,
                    """
                    UPDATE ductwork.ActivitySession
                    SET ActiveSeconds = ActiveSeconds + @a, IdleSeconds = IdleSeconds + @i, UncreditedSeconds = UncreditedSeconds + @u,
                        LastHeartbeatUtc = SYSUTCDATETIME(), Heartbeats = Heartbeats + 1,
                        WorkflowStep = COALESCE(@step, WorkflowStep), Surface = COALESCE(@surface, Surface), ReviewLevel = COALESCE(@level, ReviewLevel)
                    WHERE JobId = @j AND SessionId = @s;
                    """, cancellationToken, ("@a", active), ("@i", idle), ("@u", uncredited), ("@step", step?.ToString()),
                    ("@surface", Trim(surface, 20)), ("@level", reviewLevel?.ToString()), ("@j", jobId), ("@s", session));
            }

            var result = (await ReadAsync(connection, tx, jobId, session, forUpdate: false, cancellationToken))!;
            await tx.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>A browser closing the job ends its session, so no further time accrues to it.</summary>
    [NoAudit("Ending a time-on-job session only stamps its end time. It is liveness bookkeeping, not a change to the job.")]
    public async Task<bool> EndSessionAsync(long jobId, AuditContext context, CancellationToken cancellationToken = default)
    {
        var (email, session) = Identify(context);
        await using var connection = await _open(cancellationToken);
        return await JobRepository.ExecAsync(connection, null,
            "UPDATE ductwork.ActivitySession SET EndedUtc = SYSUTCDATETIME() WHERE JobId = @j AND SessionId = @s AND UserEmail = @e AND EndedUtc IS NULL;",
            cancellationToken, ("@j", jobId), ("@s", session), ("@e", email)) > 0;
    }

    public async Task<IReadOnlyList<ActivitySessionRecord>> ListSessionsAsync(long jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM ductwork.ActivitySession WHERE JobId = @j ORDER BY StartedUtc, SessionId;";
        command.Parameters.AddWithValue("@j", jobId);
        var list = new List<ActivitySessionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) list.Add(Map(reader));
        return list;
    }

    public async Task<JobActivitySummary> GetSummaryAsync(long jobId, CancellationToken cancellationToken = default)
    {
        var sessions = await ListSessionsAsync(jobId, cancellationToken);
        return new JobActivitySummary(jobId, sessions.Sum(s => s.ActiveSeconds), sessions.Sum(s => s.IdleSeconds), sessions.Count,
            Bucket(sessions, s => s.UserEmail), Bucket(sessions, s => s.WorkflowStep ?? "Unspecified"));
    }

    private static IReadOnlyList<ActivityBucket> Bucket(IReadOnlyList<ActivitySessionRecord> sessions, Func<ActivitySessionRecord, string> key) =>
        sessions.GroupBy(key).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ActivityBucket(g.Key, g.Sum(s => s.ActiveSeconds), g.Sum(s => s.IdleSeconds), g.Count())).ToList();

    private static (string Email, string Session) Identify(AuditContext context)
    {
        if (context.ActorType != ActorType.User || string.IsNullOrWhiteSpace(context.ActorEmail) || string.IsNullOrWhiteSpace(context.SessionId))
            throw new ArgumentException("Time on job belongs to a person's browser session, so the context needs the user and a session id.", nameof(context));
        return (context.ActorEmail, context.SessionId);
    }

    private static string? Trim(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];

    private const string Columns =
        "SessionId, JobId, UserEmail, WorkflowStep, Surface, ReviewLevel, StartedUtc, LastHeartbeatUtc, EndedUtc, ActiveSeconds, IdleSeconds, UncreditedSeconds, Heartbeats";

    private static async Task<ActivitySessionRecord?> ReadAsync(SqlConnection c, SqlTransaction tx, long jobId, string session, bool forUpdate, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT {Columns} FROM ductwork.ActivitySession {(forUpdate ? "WITH (UPDLOCK, HOLDLOCK)" : "")} WHERE JobId = @j AND SessionId = @s;";
        command.Parameters.AddWithValue("@j", jobId);
        command.Parameters.AddWithValue("@s", session);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    private static ActivitySessionRecord Map(SqlDataReader r) => new(
        r.GetString(0), r.GetInt64(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), DateTime.SpecifyKind(r.GetDateTime(6), DateTimeKind.Utc), DateTime.SpecifyKind(r.GetDateTime(7), DateTimeKind.Utc),
        r.IsDBNull(8) ? null : DateTime.SpecifyKind(r.GetDateTime(8), DateTimeKind.Utc), r.GetDouble(9), r.GetDouble(10), r.GetDouble(11), r.GetInt32(12));
}
