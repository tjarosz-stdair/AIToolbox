using System.Text.Json;
using AIToolbox.Ductwork.Core.Jobs;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Tasks;

public enum WorkerStatus
{
    /// <summary>Seen recently and free to take work.</summary>
    Online,
    /// <summary>Told to stop taking work and still finishing some.</summary>
    Draining,
    /// <summary>Told to stop taking work and has nothing left to do.</summary>
    Drained,
    /// <summary>Not heard from lately. Its running tasks, if any, will be handed back when their leases run out.</summary>
    Offline
}

/// <summary>What a worker says about itself when it connects.</summary>
public sealed record WorkerHello(string ContractVersion, string? WorkerVersion, string? HostName, int? MaxConcurrency, IReadOnlyList<string> TaskTypes);

public sealed record WorkerInstanceRecord
{
    public int WorkerInstanceId { get; init; }
    public int ApiConsumerId { get; init; }
    public required string WorkerName { get; init; }
    /// <summary>The id the worker is known by in tasks and the audit log.</summary>
    public string WorkerId => $"c{ApiConsumerId}:{WorkerName}";
    public string? HostName { get; init; }
    public string? WorkerVersion { get; init; }
    public required string ContractVersion { get; init; }
    public IReadOnlyList<string> TaskTypes { get; init; } = [];
    public int? MaxConcurrency { get; init; }
    public DateTime FirstSeenUtc { get; init; }
    public DateTime LastSeenUtc { get; init; }
    public DateTime? LastClaimUtc { get; init; }
    public bool DrainRequested { get; init; }
    public string? DrainRequestedBy { get; init; }
    public DateTime? DrainRequestedUtc { get; init; }
    public int RunningTasks { get; init; }
    public double SecondsSinceSeen { get; init; }
    public WorkerStatus Status { get; init; }
}

/// <summary>
/// Who is serving this instance. A worker records itself at handshake and is touched as it works; an administrator can see
/// whether each is alive and what it is running, ask one to drain (finish what it has, take nothing new) before an upgrade, and
/// forget one that is gone. It is bookkeeping about workers, never about jobs: nothing a worker may do depends on this table
/// except being told to stop taking work.
/// </summary>
public sealed class WorkerRegistry
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A worker polls every few seconds when idle, so a minute of silence means it is not there.</summary>
    public static readonly TimeSpan DefaultOnlineWithin = TimeSpan.FromSeconds(60);

    private readonly Func<CancellationToken, Task<SqlConnection>> _open;

    public WorkerRegistry(Func<CancellationToken, Task<SqlConnection>> openConnection) => _open = openConnection;

    /// <summary>A worker connects (or reconnects). Records what it is, and whether it had been asked to drain stays as it was.</summary>
    public async Task<WorkerInstanceRecord> RecordHandshakeAsync(int apiConsumerId, string workerName, WorkerHello hello, AuditContext worker, CancellationToken cancellationToken = default)
    {
        worker.EnsureValid();
        if (worker.ActorType != ActorType.Worker) throw new ArgumentException("A worker introduces itself, so the context must be a worker's.", nameof(worker));
        var name = workerName.Trim();
        if (name.Length is 0 or > 60) throw new ArgumentException("A worker name is 1 to 60 characters.", nameof(workerName));

        await using var connection = await _open(cancellationToken);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var existing = await ScalarAsync(connection, tx, "SELECT WorkerInstanceId FROM ductwork.WorkerInstance WITH (UPDLOCK, HOLDLOCK) WHERE ApiConsumerId = @c AND WorkerName = @n;", cancellationToken, ("@c", apiConsumerId), ("@n", name));
            var types = JsonSerializer.Serialize(hello.TaskTypes.Distinct().Order().ToList());
            if (existing is null)
            {
                await JobRepository.ExecAsync(connection, tx,
                    """
                    INSERT INTO ductwork.WorkerInstance (ApiConsumerId, WorkerName, HostName, WorkerVersion, ContractVersion, TaskTypesJson, MaxConcurrency)
                    VALUES (@c, @n, @h, @v, @cv, @t, @m);
                    """, cancellationToken, ("@c", apiConsumerId), ("@n", name), ("@h", Clean(hello.HostName, 100)), ("@v", Clean(hello.WorkerVersion, 40)), ("@cv", hello.ContractVersion),
                    ("@t", types), ("@m", hello.MaxConcurrency is >= 1 ? hello.MaxConcurrency : null));
                await SettingsAudit.AppendAsync(connection, tx, worker, "worker-registry", "worker.registered", $"A new worker, {name}, connected.", null,
                    JobRepository.Json(new() { ["worker"] = worker.ActorDisplayName, ["host"] = hello.HostName, ["version"] = hello.WorkerVersion, ["contract"] = hello.ContractVersion }), cancellationToken);
            }
            else
            {
                await JobRepository.ExecAsync(connection, tx,
                    """
                    UPDATE ductwork.WorkerInstance
                    SET HostName = @h, WorkerVersion = @v, ContractVersion = @cv, TaskTypesJson = @t, MaxConcurrency = @m, LastSeenUtc = SYSUTCDATETIME(), LastHandshakeUtc = SYSUTCDATETIME()
                    WHERE WorkerInstanceId = @id;
                    """, cancellationToken, ("@h", Clean(hello.HostName, 100)), ("@v", Clean(hello.WorkerVersion, 40)), ("@cv", hello.ContractVersion), ("@t", types),
                    ("@m", hello.MaxConcurrency is >= 1 ? hello.MaxConcurrency : null), ("@id", existing.Value));
            }

            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }

        return (await ListAsync(DefaultOnlineWithin, cancellationToken)).Single(w => w.ApiConsumerId == apiConsumerId && w.WorkerName == name);
    }

    /// <summary>The worker is alive and working. Only writes if the last note is a few seconds old, so a busy worker costs almost nothing.</summary>
    [NoAudit("Liveness bookkeeping only: it records that a worker is alive, and changes nothing a person decided.")]
    public async Task TouchAsync(int apiConsumerId, string workerName, bool claimed, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        await JobRepository.ExecAsync(connection, null,
            """
            UPDATE ductwork.WorkerInstance
            SET LastSeenUtc = SYSUTCDATETIME(), LastClaimUtc = CASE WHEN @claimed = 1 THEN SYSUTCDATETIME() ELSE LastClaimUtc END
            WHERE ApiConsumerId = @c AND WorkerName = @n AND (@claimed = 1 OR LastSeenUtc < DATEADD(SECOND, -10, SYSUTCDATETIME()));
            """, cancellationToken, ("@c", apiConsumerId), ("@n", workerName.Trim()), ("@claimed", claimed));
    }

    /// <summary>Whether this worker has been asked to take no new work. A worker that was never recorded is not draining.</summary>
    public async Task<bool> GetDrainAsync(int apiConsumerId, string workerName, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return await ScalarAsync(connection, null, "SELECT CAST(DrainRequested AS INT) FROM ductwork.WorkerInstance WHERE ApiConsumerId = @c AND WorkerName = @n;", cancellationToken,
            ("@c", apiConsumerId), ("@n", workerName.Trim())) == 1;
    }

    public async Task<IReadOnlyList<WorkerInstanceRecord>> ListAsync(TimeSpan? onlineWithin = null, CancellationToken cancellationToken = default)
    {
        var window = onlineWithin ?? DefaultOnlineWithin;
        await using var connection = await _open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT w.WorkerInstanceId, w.ApiConsumerId, w.WorkerName, w.HostName, w.WorkerVersion, w.ContractVersion, w.TaskTypesJson, w.MaxConcurrency, w.FirstSeenUtc, w.LastSeenUtc,
                   w.LastClaimUtc, w.DrainRequested, w.DrainRequestedBy, w.DrainRequestedUtc,
                   (SELECT COUNT(*) FROM ductwork.ProcessingTask t WHERE t.Status = 'Running' AND t.LeaseOwner = 'c' + CAST(w.ApiConsumerId AS NVARCHAR(20)) + ':' + w.WorkerName),
                   DATEDIFF_BIG(MILLISECOND, w.LastSeenUtc, SYSUTCDATETIME()) / 1000.0
            FROM ductwork.WorkerInstance w
            ORDER BY w.ApiConsumerId, w.WorkerName;
            """;
        var list = new List<WorkerInstanceRecord>();
        await using var r = await command.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            var seen = Convert.ToDouble(r.GetValue(15));
            var running = r.GetInt32(14);
            var drain = r.GetBoolean(11);
            var alive = seen <= window.TotalSeconds;
            list.Add(new WorkerInstanceRecord
            {
                WorkerInstanceId = r.GetInt32(0), ApiConsumerId = r.GetInt32(1), WorkerName = r.GetString(2), HostName = r.IsDBNull(3) ? null : r.GetString(3), WorkerVersion = r.IsDBNull(4) ? null : r.GetString(4),
                ContractVersion = r.GetString(5), TaskTypes = JsonSerializer.Deserialize<List<string>>(r.GetString(6), Json) ?? [], MaxConcurrency = r.IsDBNull(7) ? null : r.GetInt32(7),
                FirstSeenUtc = r.GetDateTime(8), LastSeenUtc = r.GetDateTime(9), LastClaimUtc = r.IsDBNull(10) ? null : r.GetDateTime(10), DrainRequested = drain,
                DrainRequestedBy = r.IsDBNull(12) ? null : r.GetString(12), DrainRequestedUtc = r.IsDBNull(13) ? null : r.GetDateTime(13), RunningTasks = running, SecondsSinceSeen = seen,
                Status = !alive ? WorkerStatus.Offline : drain ? (running > 0 ? WorkerStatus.Draining : WorkerStatus.Drained) : WorkerStatus.Online
            });
        }

        return list;
    }

    /// <summary>An administrator asks a worker to stop taking new work (or lifts that). What it is running carries on.</summary>
    public async Task<WorkerInstanceRecord> SetDrainAsync(int workerInstanceId, bool drain, AuditContext context, CancellationToken cancellationToken = default)
    {
        RequireAdmin(context);
        await using (var connection = await _open(cancellationToken))
        {
            await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var name = await TextAsync(connection, tx, "SELECT WorkerName FROM ductwork.WorkerInstance WITH (UPDLOCK) WHERE WorkerInstanceId = @id;", cancellationToken, ("@id", workerInstanceId))
                    ?? throw new KeyNotFoundException($"Worker {workerInstanceId} does not exist.");
                await JobRepository.ExecAsync(connection, tx,
                    "UPDATE ductwork.WorkerInstance SET DrainRequested = @d, DrainRequestedBy = @by, DrainRequestedUtc = CASE WHEN @d = 1 THEN SYSUTCDATETIME() END WHERE WorkerInstanceId = @id;",
                    cancellationToken, ("@d", drain), ("@by", drain ? JobRepository.Actor(context) : null), ("@id", workerInstanceId));
                await SettingsAudit.AppendAsync(connection, tx, context, "worker-registry", drain ? "worker.drain" : "worker.resume",
                    drain ? $"Asked worker {name} to finish its work and take no more." : $"Let worker {name} take work again.", null, null, cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        return (await ListAsync(DefaultOnlineWithin, cancellationToken)).Single(w => w.WorkerInstanceId == workerInstanceId);
    }

    /// <summary>Forgets a worker that is gone. One that is still running tasks, or was seen lately, is not removed.</summary>
    public async Task ForgetAsync(int workerInstanceId, AuditContext context, CancellationToken cancellationToken = default)
    {
        RequireAdmin(context);
        var worker = (await ListAsync(DefaultOnlineWithin, cancellationToken)).SingleOrDefault(w => w.WorkerInstanceId == workerInstanceId)
            ?? throw new KeyNotFoundException($"Worker {workerInstanceId} does not exist.");
        if (worker.Status is WorkerStatus.Online or WorkerStatus.Draining)
            throw new JobRuleViolationException("That worker is still connected. Ask it to drain and stop it first.");
        if (worker.RunningTasks > 0)
            throw new JobRuleViolationException("That worker still holds running tasks. Wait for their leases to run out, and they will be handed to another worker.");

        await using var connection = await _open(cancellationToken);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await JobRepository.ExecAsync(connection, tx, "DELETE FROM ductwork.WorkerInstance WHERE WorkerInstanceId = @id;", cancellationToken, ("@id", workerInstanceId));
        await SettingsAudit.AppendAsync(connection, tx, context, "worker-registry", "worker.forgotten", $"Removed worker {worker.WorkerName} from the list.", null, null, cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    /// <summary>An administrator created an API key for a worker. Only the fact is recorded: never the key.</summary>
    public async Task RecordKeyIssuedAsync(int apiConsumerId, string workerName, AuditContext context, CancellationToken cancellationToken = default)
    {
        RequireAdmin(context);
        await using var connection = await _open(cancellationToken);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await SettingsAudit.AppendAsync(connection, tx, context, "worker-registry", "worker.key-issued", $"Created an API key for a worker named {workerName.Trim()}.", null,
            JobRepository.Json(new() { ["apiConsumerId"] = apiConsumerId, ["workerName"] = workerName.Trim() }), cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    private static void RequireAdmin(AuditContext context)
    {
        context.EnsureValid();
        if (context.ActorType != ActorType.User || !context.IsAdmin) throw new JobAccessDeniedException("Only an administrator can manage workers.");
    }

    private static string? Clean(string? text, int max)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }

    private static async Task<long?> ScalarAsync(SqlConnection c, SqlTransaction? tx, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in args) JobRepository.P(command, name, value);
        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    private static async Task<string?> TextAsync(SqlConnection c, SqlTransaction? tx, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in args) JobRepository.P(command, name, value);
        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToString(result);
    }
}
