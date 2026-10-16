using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Jobs;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Tasks;

public sealed record TaskBindingRecord(
    long TaskBindingId, string TaskType, long ProducerId, int EvalRunId, bool SendsDrawingContentToHostedModel, int MaxAttempts,
    DateTime ActiveFromUtc, DateTime? RetiredUtc, string CreatedBy)
{
    public bool IsActive => RetiredUtc is null;
}

/// <summary>
/// Which producer runs each task type, and the gate that keeps unmeasured work away from users (plan
/// section 6.2). A binding needs an evaluation run of that same producer, so a task type cannot be
/// enabled until it has been scored. Only an administrator changes bindings, every change is recorded in
/// the settings audit log, and bindings are retired, never deleted.
/// </summary>
/// <summary>A producer an administrator could bind, with the evaluation runs that measured it.</summary>
public sealed record BindableProducer(long ProducerId, string Kind, string Label, bool SupportsVision, IReadOnlyList<BindableEvalRun> EvalRuns);

public sealed record BindableEvalRun(int EvalRunId, string TaskKey, DateTime StartedUtc, DateTime? FinishedUtc);

public sealed class TaskBindingRepository
{
    private readonly Func<CancellationToken, Task<SqlConnection>> _open;

    public TaskBindingRepository(Func<CancellationToken, Task<SqlConnection>> openConnection) => _open = openConnection;

    public TaskBindingRepository(string connectionString)
        : this(async ct =>
        {
            var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);
            return connection;
        })
    {
    }

    public async Task<TaskBindingRecord?> GetActiveAsync(string taskType, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return await ReadActiveAsync(connection, null, taskType, cancellationToken);
    }

    public async Task<IReadOnlyList<TaskBindingRecord>> ListAsync(bool includeRetired = false, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM ductwork.TaskBinding {(includeRetired ? "" : "WHERE RetiredUtc IS NULL")} ORDER BY TaskType, TaskBindingId;";
        var list = new List<TaskBindingRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) list.Add(Map(reader));
        return list;
    }

    /// <summary>The producers that have been evaluated at all, newest first, so an administrator can pick what to enable. A producer with no evaluation run cannot be bound and is not listed.</summary>
    public async Task<IReadOnlyList<BindableProducer>> ListBindableProducersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.ProducerId, p.Kind, p.AlgorithmName, p.AlgorithmVersion, p.UserEmail, ps.ModelName, ps.EndpointHost, ISNULL(ps.SupportsVision, 0),
                   r.EvalRunId, r.TaskKey, r.StartedUtc, r.FinishedUtc
            FROM ductwork.Producer p
            JOIN ductwork.EvalRun r ON r.ProducerId = p.ProducerId
            LEFT JOIN ductwork.ProviderSnapshot ps ON ps.ProviderSnapshotId = p.ProviderSnapshotId
            ORDER BY p.ProducerId DESC, r.EvalRunId DESC;
            """;
        var map = new Dictionary<long, (string Kind, string Label, bool Vision, List<BindableEvalRun> Runs)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            if (!map.TryGetValue(id, out var entry))
            {
                var kind = reader.GetString(1);
                var label = kind switch
                {
                    "Algorithm" => $"{reader.GetString(2)} {reader.GetString(3)}",
                    "Llm" => $"{(reader.IsDBNull(5) ? "model" : reader.GetString(5))} via {(reader.IsDBNull(6) ? "?" : reader.GetString(6))}",
                    _ => $"Manual ({(reader.IsDBNull(4) ? "?" : reader.GetString(4))})"
                };
                entry = (kind, label, reader.GetBoolean(7), []);
                map[id] = entry;
            }

            entry.Runs.Add(new BindableEvalRun(reader.GetInt32(8), reader.GetString(9), reader.GetDateTime(10), reader.IsDBNull(11) ? null : reader.GetDateTime(11)));
        }

        return map.Select(kv => new BindableProducer(kv.Key, kv.Value.Kind, kv.Value.Label, kv.Value.Vision, kv.Value.Runs)).ToList();
    }

    /// <summary>Enables a task type with a producer, replacing any active binding. The evaluation run must be of this exact producer.</summary>
    public async Task<TaskBindingRecord> BindAsync(string taskType, long producerId, int evalRunId, bool sendsDrawingContentToHostedModel, int maxAttempts,
        AuditContext context, CancellationToken cancellationToken = default)
    {
        RequireAdministrator(context);
        var type = taskType?.Trim() ?? "";
        if (type.Length == 0 || type.Length > 60) throw new JobRuleViolationException("A task type needs a name of up to 60 characters.");
        if (maxAttempts < 1) throw new JobRuleViolationException("A task needs at least one attempt.");

        await using var connection = await _open(cancellationToken);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var check = connection.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText =
                    """
                    SELECT r.ProducerId, p.Kind, ps.SupportsVision
                    FROM ductwork.EvalRun r
                    JOIN ductwork.Producer p ON p.ProducerId = @p
                    LEFT JOIN ductwork.ProviderSnapshot ps ON ps.ProviderSnapshotId = p.ProviderSnapshotId
                    WHERE r.EvalRunId = @r;
                    """;
                check.Parameters.AddWithValue("@p", producerId);
                check.Parameters.AddWithValue("@r", evalRunId);
                await using var reader = await check.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    throw new JobRuleViolationException("That evaluation run, or that producer, does not exist.");
                if (reader.GetInt64(0) != producerId)
                    throw new JobRuleViolationException("The evaluation run measured a different producer, so it cannot qualify this one.");
                if (sendsDrawingContentToHostedModel && !(reader.GetString(1) == "Llm" && !reader.IsDBNull(2) && reader.GetBoolean(2)))
                    throw new JobRuleViolationException("Only a vision-enabled model can be bound to a task that sends drawing content to a hosted model.");
            }

            var previous = await ReadActiveAsync(connection, tx, type, cancellationToken);
            if (previous is not null)
                await JobRepository.ExecAsync(connection, tx, "UPDATE ductwork.TaskBinding SET RetiredUtc = SYSUTCDATETIME() WHERE TaskBindingId = @id;",
                    cancellationToken, ("@id", previous.TaskBindingId));

            long id;
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText =
                    """
                    INSERT INTO ductwork.TaskBinding (TaskType, ProducerId, EvalRunId, SendsDrawingContentToHostedModel, MaxAttempts, CreatedBy)
                    VALUES (@t, @p, @r, @s, @m, @by);
                    SELECT CAST(SCOPE_IDENTITY() AS BIGINT);
                    """;
                insert.Parameters.AddWithValue("@t", type);
                insert.Parameters.AddWithValue("@p", producerId);
                insert.Parameters.AddWithValue("@r", evalRunId);
                insert.Parameters.AddWithValue("@s", sendsDrawingContentToHostedModel);
                insert.Parameters.AddWithValue("@m", maxAttempts);
                insert.Parameters.AddWithValue("@by", JobRepository.Actor(context));
                id = (long)(await insert.ExecuteScalarAsync(cancellationToken))!;
            }

            await SettingsAudit.AppendAsync(connection, tx, context, "task-binding", "binding.created",
                previous is null ? $"Enabled the task type '{type}'." : $"Replaced the binding for '{type}'.",
                previous is null ? null : Describe(previous),
                JobRepository.Json(new Dictionary<string, object?>
                    { ["taskType"] = type, ["producerId"] = producerId, ["evalRunId"] = evalRunId, ["sendsDrawing"] = sendsDrawingContentToHostedModel }),
                cancellationToken);

            await tx.CommitAsync(cancellationToken);
            return (await ReadByIdAsync(connection, null, id, cancellationToken))!;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>Disables a task type. Queued work of that type still runs; nothing new can be queued.</summary>
    public async Task<bool> RetireAsync(string taskType, AuditContext context, CancellationToken cancellationToken = default)
    {
        RequireAdministrator(context);
        await using var connection = await _open(cancellationToken);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var active = await ReadActiveAsync(connection, tx, taskType.Trim(), cancellationToken);
            if (active is null)
            {
                await tx.CommitAsync(cancellationToken);
                return false;
            }

            await JobRepository.ExecAsync(connection, tx, "UPDATE ductwork.TaskBinding SET RetiredUtc = SYSUTCDATETIME() WHERE TaskBindingId = @id;",
                cancellationToken, ("@id", active.TaskBindingId));
            await SettingsAudit.AppendAsync(connection, tx, context, "task-binding", "binding.retired", $"Disabled the task type '{active.TaskType}'.",
                Describe(active), null, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    // ------------------------------------------------------------------ internals

    private static void RequireAdministrator(AuditContext context)
    {
        context.EnsureValid();
        if (context.ActorType == ActorType.User && !context.IsAdmin)
            throw new JobAccessDeniedException("Only an administrator can change which tasks are enabled.");
    }

    private static string Describe(TaskBindingRecord b) => JobRepository.Json(new Dictionary<string, object?>
        { ["taskType"] = b.TaskType, ["producerId"] = b.ProducerId, ["evalRunId"] = b.EvalRunId, ["sendsDrawing"] = b.SendsDrawingContentToHostedModel });

    internal static async Task<TaskBindingRecord?> ReadActiveAsync(SqlConnection c, SqlTransaction? tx, string taskType, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT {Columns} FROM ductwork.TaskBinding WHERE TaskType = @t AND RetiredUtc IS NULL;";
        command.Parameters.AddWithValue("@t", taskType);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    private static async Task<TaskBindingRecord?> ReadByIdAsync(SqlConnection c, SqlTransaction? tx, long id, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT {Columns} FROM ductwork.TaskBinding WHERE TaskBindingId = @id;";
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    private const string Columns = "TaskBindingId, TaskType, ProducerId, EvalRunId, SendsDrawingContentToHostedModel, MaxAttempts, ActiveFromUtc, RetiredUtc, CreatedBy";

    private static TaskBindingRecord Map(SqlDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt32(3), r.GetBoolean(4), r.GetInt32(5),
        DateTime.SpecifyKind(r.GetDateTime(6), DateTimeKind.Utc), r.IsDBNull(7) ? null : DateTime.SpecifyKind(r.GetDateTime(7), DateTimeKind.Utc), r.GetString(8));
}

/// <summary>Append-only record of instance-level setting changes (not tied to one job).</summary>
internal static class SettingsAudit
{
    public static Task AppendAsync(SqlConnection connection, SqlTransaction tx, AuditContext context, string category, string action, string summary,
        string? beforeJson, string? afterJson, CancellationToken ct) =>
        JobRepository.ExecAsync(connection, tx,
            """
            INSERT INTO ductwork.SettingsAuditEvent (ActorType, ActorEmail, ActorDisplayName, ClientIp, Category, Action, Summary, BeforeJson, AfterJson, Reason)
            VALUES (@type, @email, @name, @ip, @cat, @act, @sum, @before, @after, @reason);
            """, ct,
            ("@type", context.ActorType.ToString()), ("@email", context.ActorEmail), ("@name", context.ActorDisplayName), ("@ip", context.ClientIp),
            ("@cat", category), ("@act", action), ("@sum", summary), ("@before", beforeJson), ("@after", afterJson), ("@reason", context.Reason));
}
