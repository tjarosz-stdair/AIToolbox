using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Jobs;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Tasks;

public enum ProcessingTaskStatus { Queued, Running, Succeeded, Failed, Cancelled }

public sealed record ProcessingTaskRecord
{
    public long ProcessingTaskId { get; init; }
    public Guid PublicId { get; init; }
    public long? ParentTaskId { get; init; }
    public long JobId { get; init; }
    public required string TaskType { get; init; }
    public ProcessingTaskStatus Status { get; init; }
    public bool CancelRequested { get; init; }
    public int ProgressDone { get; init; }
    public int? ProgressTotal { get; init; }
    public string? ProgressMessage { get; init; }
    public int Attempt { get; init; }
    public int MaxAttempts { get; init; }
    public DateTime? NotBeforeUtc { get; init; }
    public string? LeaseOwner { get; init; }
    public DateTime? LeaseExpiresUtc { get; init; }
    public long ProducerId { get; init; }
    public string? InputManifestRef { get; init; }
    public string? ResultManifestRef { get; init; }
    public required string ContractVersion { get; init; }
    public bool SendsDrawingContentToHostedModel { get; init; }
    public long? VisionApprovalId { get; init; }
    public DateTime CreatedUtc { get; init; }
    public required string CreatedBy { get; init; }
    public DateTime? StartedUtc { get; init; }
    public DateTime? FinishedUtc { get; init; }
    public string? ErrorMessage { get; init; }

    public bool IsActive => Status is ProcessingTaskStatus.Queued or ProcessingTaskStatus.Running;
}

public sealed record NewProcessingTask
{
    public required long JobId { get; init; }
    public required string TaskType { get; init; }
    /// <summary>The algorithm or model this task will run as. Every result is attributable to it.</summary>
    public required long ProducerId { get; init; }
    public long? ParentTaskId { get; init; }
    public string? InputManifestRef { get; init; }
    public int MaxAttempts { get; init; } = 3;
    /// <summary>True for any task that sends drawing content to a hosted model. Such a task cannot be created without an approval.</summary>
    public bool SendsDrawingContentToHostedModel { get; init; }
    public long? VisionApprovalId { get; init; }
}

public sealed record TaskSettings
{
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan RetryBackoffBase { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan RetryBackoffMax { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed record TaskHeartbeatResult(bool Continue, bool CancelRequested);

/// <summary>
/// The work queue the central worker pulls from (plan sections 6.5 and 6.9). The worker never reads the
/// database: it claims, heartbeats, reports and completes through the web app, which calls these methods.
/// Every state change that matters is also a job audit event, written in the same transaction.
/// </summary>
public sealed class ProcessingTaskRepository
{
    private readonly Func<CancellationToken, Task<SqlConnection>> _open;
    private readonly TaskSettings _settings;

    public ProcessingTaskRepository(Func<CancellationToken, Task<SqlConnection>> openConnection, TaskSettings? settings = null)
    {
        _open = openConnection;
        _settings = settings ?? new TaskSettings();
    }

    public ProcessingTaskRepository(string connectionString, TaskSettings? settings = null)
        : this(async ct =>
        {
            var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);
            return connection;
        }, settings)
    {
    }

    // ------------------------------------------------------------------ reads

    public async Task<ProcessingTaskRecord?> GetAsync(long taskId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return await ReadAsync(connection, null, taskId, forUpdate: false, cancellationToken);
    }

    public async Task<IReadOnlyList<ProcessingTaskRecord>> ListByJobAsync(long jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM ductwork.ProcessingTask WHERE JobId = @j ORDER BY ProcessingTaskId;";
        command.Parameters.AddWithValue("@j", jobId);
        var tasks = new List<ProcessingTaskRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) tasks.Add(Map(reader));
        return tasks;
    }

    public async Task<int> GetActiveCountAsync(long jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return (await new Counting.JobCompletionProbe().GetAsync(connection, null!, jobId, cancellationToken)).ActiveProcessingTasks;
    }

    // ------------------------------------------------------------------ enqueue

    /// <summary>
    /// Queues work. Starting or re-running detection changes a job, so it needs the edit lock like any
    /// other edit, and a job that is Complete or Closed cannot take new work.
    /// </summary>
    public Task<ProcessingTaskRecord> EnqueueAsync(NewProcessingTask task, EditToken editToken, AuditContext context,
        CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, task.JobId, context, cancellationToken, t => EnqueueCoreAsync(t, task, context), editToken, requires: JobCapability.Edit);

    /// <summary>
    /// Queues work by task type. The producer, the attempt limit and whether drawings go to a hosted model
    /// come from the type's active binding, which exists only for a task type that has been evaluated. A type
    /// with no binding cannot be queued, whoever asks.
    /// </summary>
    public Task<ProcessingTaskRecord> EnqueueByTypeAsync(long jobId, string taskType, string? inputManifestRef, long? visionApprovalId, EditToken editToken,
        AuditContext context, CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context, cancellationToken, async t =>
        {
            var binding = await TaskBindingRepository.ReadActiveAsync(t.Connection, t.Transaction, taskType.Trim(), t.Token)
                ?? throw new JobRuleViolationException($"The task type '{taskType}' is not enabled: it has no active, evaluated binding.");
            return await EnqueueCoreAsync(t, new NewProcessingTask
            {
                JobId = jobId, TaskType = binding.TaskType, ProducerId = binding.ProducerId, InputManifestRef = inputManifestRef, MaxAttempts = binding.MaxAttempts,
                SendsDrawingContentToHostedModel = binding.SendsDrawingContentToHostedModel, VisionApprovalId = visionApprovalId
            }, context);
        }, editToken, requires: JobCapability.Edit);

    private static async Task<ProcessingTaskRecord> EnqueueCoreAsync(JobTxn t, NewProcessingTask task, AuditContext context)
    {
            t.RequireEditable();
            if (string.IsNullOrWhiteSpace(task.TaskType)) throw new JobRuleViolationException("A task needs a type.");
            if (task.MaxAttempts < 1) throw new JobRuleViolationException("A task needs at least one attempt.");
            // Drawings go to a hosted model only under a live approval for this model. The check is made here, where the task is
            // created, and again by the gateway before every send.
            if (task.SendsDrawingContentToHostedModel)
                task = task with { VisionApprovalId = await Llm.VisionRepository.BindApprovalAsync(t, task.VisionApprovalId, task.TaskType, task.ProducerId) };
            else if (task.VisionApprovalId is not null)
                throw new JobRuleViolationException("This task does not send drawings to a hosted model, so it cannot hold an approval for that.");

            if (task.ParentTaskId is { } parentId)
            {
                var parent = await ReadAsync(t.Connection, t.Transaction, parentId, forUpdate: false, t.Token);
                if (parent is null || parent.JobId != task.JobId)
                    throw new JobRuleViolationException("A task's parent must be a task of the same job.");
            }

            long id;
            await using (var insert = t.Connection.CreateCommand())
            {
                insert.Transaction = t.Transaction;
                insert.CommandText =
                    """
                    INSERT INTO ductwork.ProcessingTask
                        (ParentTaskId, JobId, TaskType, MaxAttempts, ProducerId, InputManifestRef, ContractVersion,
                         SendsDrawingContentToHostedModel, VisionApprovalId, CreatedBy)
                    VALUES (@parent, @j, @type, @max, @producer, @input, @contract, @sends, @approval, @by);
                    SELECT CAST(SCOPE_IDENTITY() AS BIGINT);
                    """;
                JobRepository.P(insert, "@parent", task.ParentTaskId);
                insert.Parameters.AddWithValue("@j", task.JobId);
                insert.Parameters.AddWithValue("@type", task.TaskType.Trim());
                insert.Parameters.AddWithValue("@max", task.MaxAttempts);
                insert.Parameters.AddWithValue("@producer", task.ProducerId);
                JobRepository.P(insert, "@input", task.InputManifestRef);
                insert.Parameters.AddWithValue("@contract", ContractInfo.Version);
                insert.Parameters.AddWithValue("@sends", task.SendsDrawingContentToHostedModel);
                JobRepository.P(insert, "@approval", task.VisionApprovalId);
                insert.Parameters.AddWithValue("@by", JobRepository.Actor(context));
                id = (long)(await insert.ExecuteScalarAsync(t.Token))!;
            }

            if (task.SendsDrawingContentToHostedModel) await Llm.VisionRepository.MarkUsedAsync(t, task.VisionApprovalId!.Value, id);

            var created = (await ReadAsync(t.Connection, t.Transaction, id, forUpdate: false, t.Token))!;
            await t.AuditForTaskAsync(new AuditEntry("processing", "task.enqueued", $"Queued a {created.TaskType} task.", "Task",
                created.PublicId.ToString("D"), null, JobRepository.Json(new Dictionary<string, object?>
                {
                    ["taskType"] = created.TaskType, ["producerId"] = created.ProducerId, ["maxAttempts"] = created.MaxAttempts
                })), id, created.ProducerId);
            return created;
    }

    // ------------------------------------------------------------------ the worker's side

    /// <summary>
    /// Atomically hands the oldest runnable task to a worker for a lease period. A task whose worker
    /// stopped heartbeating is given to another worker, up to its attempt limit, and then failed.
    /// Returns null when nothing is runnable.
    /// </summary>
    public async Task<ProcessingTaskRecord?> ClaimNextAsync(AuditContext worker, IReadOnlyCollection<string>? taskTypes = null,
        CancellationToken cancellationToken = default)
    {
        var workerId = WorkerId(worker);
        var context = worker;
        var skipped = new List<long>();

        for (var attempt = 0; attempt < 25; attempt++)
        {
            var (taskId, jobId) = await FindCandidateAsync(taskTypes, skipped, cancellationToken) ?? default;
            if (taskId == 0) return null;

            var claimed = await JobTxn.RunAsync(_open, jobId, context, cancellationToken, async t =>
            {
                var task = await ReadAsync(t.Connection, t.Transaction, taskId, forUpdate: true, t.Token);
                if (task is null) return null;

                var due = await IsDueAsync(t, task);
                if (!due) return null;   // another worker got it first, or it is no longer runnable

                if (task.Status == ProcessingTaskStatus.Running)
                {
                    // Its worker stopped heartbeating.
                    if (task.CancelRequested)
                    {
                        await FinishAsync(t, task, ProcessingTaskStatus.Cancelled, "Cancelled while its worker was away.");
                        await t.AuditForTaskAsync(new AuditEntry("processing", "task.cancelled", "Task cancelled; its worker had stopped.", "Task", task.PublicId.ToString("D")), task.ProcessingTaskId, task.ProducerId);
                        return null;
                    }

                    if (task.Attempt >= task.MaxAttempts)
                    {
                        await FinishAsync(t, task, ProcessingTaskStatus.Failed, "The worker stopped responding and no attempts are left.");
                        await t.AuditForTaskAsync(new AuditEntry("processing", "task.failed",
                            $"Task failed: its worker stopped responding and all {task.MaxAttempts} attempts are used.", "Task", task.PublicId.ToString("D")), task.ProcessingTaskId, task.ProducerId);
                        return null;
                    }
                }

                await JobRepository.ExecAsync(t.Connection, t.Transaction,
                    """
                    UPDATE ductwork.ProcessingTask
                    SET Status = 'Running', Attempt = Attempt + 1, LeaseOwner = @w,
                        LeaseExpiresUtc = DATEADD(MILLISECOND, @leaseMs, SYSUTCDATETIME()),
                        StartedUtc = COALESCE(StartedUtc, SYSUTCDATETIME()), NotBeforeUtc = NULL
                    WHERE ProcessingTaskId = @id;
                    """, t.Token, ("@w", workerId), ("@leaseMs", (int)_settings.LeaseDuration.TotalMilliseconds), ("@id", taskId));
                var running = (await ReadAsync(t.Connection, t.Transaction, taskId, forUpdate: false, t.Token))!;
                await t.AuditForTaskAsync(new AuditEntry("processing", task.Status == ProcessingTaskStatus.Running ? "task.reclaimed" : "task.started",
                    $"{workerId} started attempt {running.Attempt} of {running.MaxAttempts}.", "Task", running.PublicId.ToString("D")),
                    taskId, running.ProducerId);
                return running;
            });

            if (claimed is not null) return claimed;
            skipped.Add(taskId);
        }

        return null;
    }

    /// <summary>
    /// A running worker's heartbeat: extends its lease and records progress. Not audited, because it is high-frequency
    /// liveness. Returns whether the worker should keep going; false means it lost the task or the task was cancelled.
    /// </summary>
    [NoAudit("A task heartbeat extends a lease and updates progress. It is high-frequency liveness, not a change to the job.")]
    public async Task<TaskHeartbeatResult> HeartbeatAsync(long taskId, string workerId, int progressDone, int? progressTotal,
        string? message, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE ductwork.ProcessingTask
            SET LeaseExpiresUtc = DATEADD(MILLISECOND, @leaseMs, SYSUTCDATETIME()),
                ProgressDone = CASE WHEN @done < 0 THEN 0 WHEN @total IS NOT NULL AND @done > @total THEN @total ELSE @done END,
                ProgressTotal = @total, ProgressMessage = @message
            OUTPUT inserted.CancelRequested
            WHERE ProcessingTaskId = @id AND Status = 'Running' AND LeaseOwner = @w AND LeaseExpiresUtc > SYSUTCDATETIME();
            """;
        command.Parameters.AddWithValue("@leaseMs", (int)_settings.LeaseDuration.TotalMilliseconds);
        command.Parameters.AddWithValue("@done", progressDone);
        JobRepository.P(command, "@total", progressTotal);
        JobRepository.P(command, "@message", message is null ? null : message[..Math.Min(message.Length, 400)]);
        command.Parameters.AddWithValue("@id", taskId);
        command.Parameters.AddWithValue("@w", workerId);
        var cancel = await command.ExecuteScalarAsync(cancellationToken);
        return cancel is null ? new TaskHeartbeatResult(false, false) : new TaskHeartbeatResult(!(bool)cancel, (bool)cancel);
    }

    public Task<bool> CompleteAsync(long taskId, AuditContext worker, string? resultManifestRef, CancellationToken cancellationToken = default) =>
        WorkerStepAsync(taskId, worker, cancellationToken, async (t, task) =>
        {
            if (task.CancelRequested)
            {
                await FinishAsync(t, task, ProcessingTaskStatus.Cancelled, "Cancelled before it finished.");
                await t.AuditForTaskAsync(new AuditEntry("processing", "task.cancelled", "Task cancelled.", "Task", task.PublicId.ToString("D")), task.ProcessingTaskId, task.ProducerId);
                return false;
            }

            await FinishAsync(t, task, ProcessingTaskStatus.Succeeded, null, resultManifestRef);
            await t.AuditForTaskAsync(new AuditEntry("processing", "task.finished", $"{task.TaskType} finished.", "Task", task.PublicId.ToString("D"), null,
                JobRepository.Json(new Dictionary<string, object?> { ["resultManifestRef"] = resultManifestRef, ["attempts"] = task.Attempt })),
                task.ProcessingTaskId, task.ProducerId);
            return true;
        });

    /// <summary>
    /// The worker reports a failure. A retryable one goes back in the queue after a growing delay while
    /// attempts remain. Otherwise the task fails for good.
    /// </summary>
    public Task<bool> FailAsync(long taskId, AuditContext worker, string error, bool retryable, CancellationToken cancellationToken = default) =>
        WorkerStepAsync(taskId, worker, cancellationToken, async (t, task) =>
        {
            var message = error.Length <= 2000 ? error : error[..2000];
            if (task.CancelRequested)
            {
                await FinishAsync(t, task, ProcessingTaskStatus.Cancelled, "Cancelled.");
                await t.AuditForTaskAsync(new AuditEntry("processing", "task.cancelled", "Task cancelled.", "Task", task.PublicId.ToString("D")), task.ProcessingTaskId, task.ProducerId);
                return false;
            }

            if (retryable && task.Attempt < task.MaxAttempts)
            {
                var delay = Backoff(task.Attempt);
                await JobRepository.ExecAsync(t.Connection, t.Transaction,
                    """
                    UPDATE ductwork.ProcessingTask
                    SET Status = 'Queued', LeaseOwner = NULL, LeaseExpiresUtc = NULL, ErrorMessage = @e,
                        NotBeforeUtc = DATEADD(MILLISECOND, @delayMs, SYSUTCDATETIME())
                    WHERE ProcessingTaskId = @id;
                    """, t.Token, ("@e", message), ("@delayMs", (int)delay.TotalMilliseconds), ("@id", task.ProcessingTaskId));
                await t.AuditForTaskAsync(new AuditEntry("processing", "task.retrying",
                    $"Attempt {task.Attempt} of {task.MaxAttempts} failed; trying again in {delay.TotalSeconds:0} s.", "Task",
                    task.PublicId.ToString("D"), null, JobRepository.Json(new Dictionary<string, object?> { ["error"] = message })),
                    task.ProcessingTaskId, task.ProducerId);
                return true;
            }

            await FinishAsync(t, task, ProcessingTaskStatus.Failed, message);
            await t.AuditForTaskAsync(new AuditEntry("processing", "task.failed", $"{task.TaskType} failed.", "Task", task.PublicId.ToString("D"), null,
                JobRepository.Json(new Dictionary<string, object?> { ["error"] = message, ["attempts"] = task.Attempt })),
                task.ProcessingTaskId, task.ProducerId);
            return true;
        });

    // ------------------------------------------------------------------ the user's side

    /// <summary>
    /// A queued task is cancelled at once. A running one is asked to stop: the worker sees it on its next
    /// heartbeat and the task becomes Cancelled when the worker reports back, or when its lease lapses.
    /// </summary>
    public Task<ProcessingTaskRecord> CancelAsync(long taskId, AuditContext context, CancellationToken cancellationToken = default) =>
        CancelCoreAsync(taskId, context, cancellationToken);

    private async Task<ProcessingTaskRecord> CancelCoreAsync(long taskId, AuditContext context, CancellationToken cancellationToken)
    {
        var existing = await GetAsync(taskId, cancellationToken) ?? throw new KeyNotFoundException($"Task {taskId} does not exist.");
        return await JobTxn.RunAsync(_open, existing.JobId, context, cancellationToken, async t =>
        {
            await t.RequireAsync(JobCapability.Edit);
            var task = (await ReadAsync(t.Connection, t.Transaction, taskId, forUpdate: true, t.Token))!;
            if (!task.IsActive) return task;                       // already finished: nothing to cancel
            if (task.CancelRequested) return task;                 // already asked

            if (task.Status == ProcessingTaskStatus.Queued)
            {
                await FinishAsync(t, task, ProcessingTaskStatus.Cancelled, "Cancelled by a user.");
                await t.AuditForTaskAsync(new AuditEntry("processing", "task.cancelled", "Cancelled a queued task.", "Task", task.PublicId.ToString("D")), taskId, task.ProducerId);
            }
            else
            {
                await JobRepository.ExecAsync(t.Connection, t.Transaction, "UPDATE ductwork.ProcessingTask SET CancelRequested = 1 WHERE ProcessingTaskId = @id;",
                    t.Token, ("@id", taskId));
                await t.AuditForTaskAsync(new AuditEntry("processing", "task.cancel-requested", "Asked a running task to stop.", "Task", task.PublicId.ToString("D")), taskId, task.ProducerId);
            }

            return (await ReadAsync(t.Connection, t.Transaction, taskId, forUpdate: false, t.Token))!;
        });
    }

    // ------------------------------------------------------------------ internals

    /// <summary>A task's worker is identified by its context. Only a worker context may act on the worker's side of the queue.</summary>
    private static string WorkerId(AuditContext worker)
    {
        if (worker.ActorType != ActorType.Worker || string.IsNullOrWhiteSpace(worker.ActorDisplayName))
            throw new ArgumentException("This operation belongs to a worker, so the context must be a worker's, with its id.", nameof(worker));
        return worker.ActorDisplayName;
    }

    private async Task<bool> WorkerStepAsync(long taskId, AuditContext worker, CancellationToken cancellationToken,
        Func<JobTxn, ProcessingTaskRecord, Task<bool>> step)
    {
        var workerId = WorkerId(worker);
        var existing = await GetAsync(taskId, cancellationToken);
        if (existing is null) return false;

        return await JobTxn.RunAsync(_open, existing.JobId, worker, cancellationToken, async t =>
        {
            var task = await ReadAsync(t.Connection, t.Transaction, taskId, forUpdate: true, t.Token);
            // Only the worker that currently holds the task may report on it. A worker whose lease was reclaimed is told no.
            if (task is null || task.Status != ProcessingTaskStatus.Running || task.LeaseOwner != workerId) return false;
            return await step(t, task);
        });
    }

    internal static async Task FinishAsync(JobTxn t, ProcessingTaskRecord task, ProcessingTaskStatus status, string? error, string? resultRef = null)
    {
        await JobRepository.ExecAsync(t.Connection, t.Transaction,
            """
            UPDATE ductwork.ProcessingTask
            SET Status = @s, LeaseOwner = NULL, LeaseExpiresUtc = NULL, FinishedUtc = SYSUTCDATETIME(), NotBeforeUtc = NULL,
                ErrorMessage = @e, ResultManifestRef = COALESCE(@r, ResultManifestRef),
                ProgressDone = CASE WHEN @s = 'Succeeded' AND ProgressTotal IS NOT NULL THEN ProgressTotal ELSE ProgressDone END
            WHERE ProcessingTaskId = @id;
            """, t.Token, ("@s", status.ToString()), ("@e", error), ("@r", resultRef), ("@id", task.ProcessingTaskId));
    }

    private TimeSpan Backoff(int attempt)
    {
        var factor = Math.Pow(2, Math.Max(0, attempt - 1));
        var ms = Math.Min(_settings.RetryBackoffBase.TotalMilliseconds * factor, _settings.RetryBackoffMax.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(ms);
    }

    private static async Task<bool> IsDueAsync(JobTxn t, ProcessingTaskRecord task)
    {
        await using var command = t.Connection.CreateCommand();
        command.Transaction = t.Transaction;
        command.CommandText =
            """
            SELECT CASE
                WHEN Status = 'Queued' AND (NotBeforeUtc IS NULL OR NotBeforeUtc <= SYSUTCDATETIME()) THEN 1
                WHEN Status = 'Running' AND LeaseExpiresUtc <= SYSUTCDATETIME() THEN 1
                ELSE 0 END
            FROM ductwork.ProcessingTask WHERE ProcessingTaskId = @id;
            """;
        command.Parameters.AddWithValue("@id", task.ProcessingTaskId);
        return (int)(await command.ExecuteScalarAsync(t.Token))! == 1;
    }

    private async Task<(long TaskId, long JobId)?> FindCandidateAsync(IReadOnlyCollection<string>? types, List<long> skipped, CancellationToken ct)
    {
        await using var connection = await _open(ct);
        await using var command = connection.CreateCommand();
        var filters = new List<string>();
        if (types is { Count: > 0 })
        {
            var names = types.Select((_, i) => $"@type{i}").ToList();
            filters.Add($"t.TaskType IN ({string.Join(", ", names)})");
            var i = 0;
            foreach (var type in types) command.Parameters.AddWithValue($"@type{i++}", type);
        }

        if (skipped.Count > 0)
        {
            var names = skipped.Select((_, i) => $"@skip{i}").ToList();
            filters.Add($"t.ProcessingTaskId NOT IN ({string.Join(", ", names)})");
            for (var i = 0; i < skipped.Count; i++) command.Parameters.AddWithValue($"@skip{i}", skipped[i]);
        }

        command.CommandText =
            $"""
            SELECT TOP 1 t.ProcessingTaskId, t.JobId
            FROM ductwork.ProcessingTask t WITH (READPAST)
            WHERE ((t.Status = 'Queued' AND (t.NotBeforeUtc IS NULL OR t.NotBeforeUtc <= SYSUTCDATETIME()))
                   OR (t.Status = 'Running' AND t.LeaseExpiresUtc <= SYSUTCDATETIME()))
              {(filters.Count == 0 ? "" : "AND " + string.Join(" AND ", filters))}
            ORDER BY t.ProcessingTaskId;
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetInt64(0), reader.GetInt64(1)) : null;
    }

    private const string Columns =
        "ProcessingTaskId, PublicId, ParentTaskId, JobId, TaskType, Status, CancelRequested, ProgressDone, ProgressTotal, ProgressMessage, Attempt, MaxAttempts, " +
        "NotBeforeUtc, LeaseOwner, LeaseExpiresUtc, ProducerId, InputManifestRef, ResultManifestRef, ContractVersion, SendsDrawingContentToHostedModel, " +
        "VisionApprovalId, CreatedUtc, CreatedBy, StartedUtc, FinishedUtc, ErrorMessage";

    internal static async Task<ProcessingTaskRecord?> ReadAsync(SqlConnection c, SqlTransaction? tx, long taskId, bool forUpdate, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT {Columns} FROM ductwork.ProcessingTask {(forUpdate ? "WITH (UPDLOCK, ROWLOCK)" : "")} WHERE ProcessingTaskId = @id;";
        command.Parameters.AddWithValue("@id", taskId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    private static ProcessingTaskRecord Map(SqlDataReader r) => new()
    {
        ProcessingTaskId = r.GetInt64(0),
        PublicId = r.GetGuid(1),
        ParentTaskId = r.IsDBNull(2) ? null : r.GetInt64(2),
        JobId = r.GetInt64(3),
        TaskType = r.GetString(4),
        Status = Enum.Parse<ProcessingTaskStatus>(r.GetString(5)),
        CancelRequested = r.GetBoolean(6),
        ProgressDone = r.GetInt32(7),
        ProgressTotal = r.IsDBNull(8) ? null : r.GetInt32(8),
        ProgressMessage = r.IsDBNull(9) ? null : r.GetString(9),
        Attempt = r.GetInt32(10),
        MaxAttempts = r.GetInt32(11),
        NotBeforeUtc = Utc(r, 12),
        LeaseOwner = r.IsDBNull(13) ? null : r.GetString(13),
        LeaseExpiresUtc = Utc(r, 14),
        ProducerId = r.GetInt64(15),
        InputManifestRef = r.IsDBNull(16) ? null : r.GetString(16),
        ResultManifestRef = r.IsDBNull(17) ? null : r.GetString(17),
        ContractVersion = r.GetString(18),
        SendsDrawingContentToHostedModel = r.GetBoolean(19),
        VisionApprovalId = r.IsDBNull(20) ? null : r.GetInt64(20),
        CreatedUtc = DateTime.SpecifyKind(r.GetDateTime(21), DateTimeKind.Utc),
        CreatedBy = r.GetString(22),
        StartedUtc = Utc(r, 23),
        FinishedUtc = Utc(r, 24),
        ErrorMessage = r.IsDBNull(25) ? null : r.GetString(25)
    };

    private static DateTime? Utc(SqlDataReader r, int i) => r.IsDBNull(i) ? null : DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc);
}
