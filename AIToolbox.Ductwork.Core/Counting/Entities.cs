using System.Data;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Llm;
using AIToolbox.Ductwork.Core.Review;
using AIToolbox.Ductwork.Core.Tasks;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Counting;

public sealed record EntityRecord
{
    public long EntityId { get; init; }
    public Guid PublicId { get; init; }
    public long JobId { get; init; }
    public long? BuildingTypeId { get; init; }
    public long ProducerId { get; init; }
    public long ProcessingTaskId { get; init; }
    public required string DetectionId { get; init; }
    public required string Type { get; init; }
    public string? Category { get; init; }
    public string? Tag { get; init; }
    public string? Size { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? CustomerPartCode { get; init; }
    public decimal Quantity { get; init; }
    public QuantityUnit Unit { get; init; }
    public DetectionMethod Method { get; init; }
    /// <summary>Null for an item a person added.</summary>
    public DetectorFamily? Family { get; init; }
    public double Confidence { get; init; }
    public SurfacedAs SurfacedAs { get; init; }
    public ReviewLevel ReviewLevelAtCreation { get; init; }
    public ReviewStatus ReviewStatus { get; init; }
    public Disposition Disposition { get; init; }
    public ResolutionSource ResolutionSource { get; init; }
    public bool IsStale { get; init; }
    public string? SheetViewRef { get; init; }
    public BoundingBox? Box { get; init; }
    public string? Evidence { get; init; }
    /// <summary>Counts changes to the item. An edit says which revision it was made against, so two people cannot silently overwrite each other.</summary>
    public int Revision { get; init; }
    public long? SheetViewId { get; init; }
    public string? DispositionReason { get; init; }
}

public sealed record EntityFilter
{
    public long? BuildingTypeId { get; init; }
    public ReviewStatus? ReviewStatus { get; init; }
    public Disposition? Disposition { get; init; }
    public long? ProcessingTaskId { get; init; }
    public int Limit { get; init; } = 100_000;
}

public sealed class EntityRepository
{
    private readonly Func<CancellationToken, Task<SqlConnection>> _open;

    public EntityRepository(Func<CancellationToken, Task<SqlConnection>> openConnection) => _open = openConnection;

    public EntityRepository(string connectionString)
        : this(async ct =>
        {
            var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);
            return connection;
        })
    {
    }

    public async Task<IReadOnlyList<EntityRecord>> ListByJobAsync(long jobId, EntityFilter? filter = null, CancellationToken cancellationToken = default)
    {
        filter ??= new EntityFilter();
        await using var connection = await _open(cancellationToken);
        return await ListAsync(connection, null, jobId, filter, cancellationToken);
    }

    internal static async Task<IReadOnlyList<EntityRecord>> ListAsync(SqlConnection c, SqlTransaction? tx, long jobId, EntityFilter filter, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"""
            SELECT TOP (@limit) {Columns}
            FROM ductwork.Entity
            WHERE JobId = @j
              AND (@bt IS NULL OR BuildingTypeId = @bt)
              AND (@rs IS NULL OR ReviewStatus = @rs)
              AND (@disp IS NULL OR Disposition = @disp)
              AND (@task IS NULL OR ProcessingTaskId = @task)
            ORDER BY EntityId;
            """;
        command.Parameters.AddWithValue("@limit", Math.Clamp(filter.Limit, 1, 1_000_000));
        command.Parameters.AddWithValue("@j", jobId);
        command.Parameters.AddWithValue("@bt", (object?)filter.BuildingTypeId ?? DBNull.Value);
        command.Parameters.AddWithValue("@rs", (object?)filter.ReviewStatus?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("@disp", (object?)filter.Disposition?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("@task", (object?)filter.ProcessingTaskId ?? DBNull.Value);

        var list = new List<EntityRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(Map(reader));
        return list;
    }

    internal const string Columns =
        "EntityId, PublicId, JobId, BuildingTypeId, ProducerId, ProcessingTaskId, DetectionId, Type, Category, Tag, Size, Manufacturer, Model, CustomerPartCode, " +
        "Quantity, Unit, Method, Family, Confidence, SurfacedAs, ReviewLevelAtCreation, ReviewStatus, Disposition, ResolutionSource, IsStale, SheetViewRef, " +
        "BoxX0, BoxY0, BoxX1, BoxY1, Evidence, Revision, SheetViewId, DispositionReason";

    internal static EntityRecord Map(SqlDataReader r) => new()
    {
        EntityId = r.GetInt64(0),
        PublicId = r.GetGuid(1),
        JobId = r.GetInt64(2),
        BuildingTypeId = r.IsDBNull(3) ? null : r.GetInt64(3),
        ProducerId = r.GetInt64(4),
        ProcessingTaskId = r.GetInt64(5),
        DetectionId = r.GetString(6),
        Type = r.GetString(7),
        Category = S(r, 8),
        Tag = S(r, 9),
        Size = S(r, 10),
        Manufacturer = S(r, 11),
        Model = S(r, 12),
        CustomerPartCode = S(r, 13),
        Quantity = r.GetDecimal(14),
        Unit = Enum.Parse<QuantityUnit>(r.GetString(15)),
        Method = Enum.Parse<DetectionMethod>(r.GetString(16)),
        Family = r.IsDBNull(17) ? null : Enum.Parse<DetectorFamily>(r.GetString(17)),
        Confidence = r.GetDouble(18),
        SurfacedAs = Enum.Parse<SurfacedAs>(r.GetString(19)),
        ReviewLevelAtCreation = Enum.Parse<ReviewLevel>(r.GetString(20)),
        ReviewStatus = Enum.Parse<ReviewStatus>(r.GetString(21)),
        Disposition = Enum.Parse<Disposition>(r.GetString(22)),
        ResolutionSource = Enum.Parse<ResolutionSource>(r.GetString(23)),
        IsStale = r.GetBoolean(24),
        SheetViewRef = S(r, 25),
        Box = r.IsDBNull(26) ? null : new BoundingBox(r.GetDouble(26), r.GetDouble(27), r.GetDouble(28), r.GetDouble(29)),
        Evidence = S(r, 30),
        Revision = r.GetInt32(31),
        SheetViewId = r.IsDBNull(32) ? null : r.GetInt64(32),
        DispositionReason = S(r, 33)
    };

    private static string? S(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}

public sealed record IngestResult(bool Accepted, bool Cancelled, int Entities, int Findings, int BuildingTypesCreated, int RaisedAsFindings, int Batched);

/// <summary>A result was refused, and nothing from it was stored. The worker may fail the task or try again.</summary>
public sealed class ResultRejectedException(IReadOnlyList<string> reasons)
    : InvalidOperationException("The result was refused: " + string.Join(" ", reasons.Take(5)))
{
    public IReadOnlyList<string> Reasons { get; } = reasons;
}

/// <summary>
/// The only way an entity is created (plan sections 6.5 and 7). A worker hands over the result of a task it
/// is running, and the web app, as the single write path, validates it against the contract, stamps the
/// provenance (producer and task) from the task itself and never from the worker's say-so, decides for
/// each detection whether it is raised to the user or batched (review level), stores everything, and
/// finishes the task, all in one transaction. A result that fails any check stores nothing.
/// </summary>
public sealed class TaskResultIngestor
{
    private const int MaxText = 100;
    private const int MaxEvidence = 1000;

    private readonly Func<CancellationToken, Task<SqlConnection>> _open;
    private readonly ReviewLevelSettings _settings;
    private readonly Func<CancellationToken, Task<CalibrationSet>> _calibration;

    public TaskResultIngestor(Func<CancellationToken, Task<SqlConnection>> openConnection, ReviewLevelSettings? settings = null,
        Func<CancellationToken, Task<CalibrationSet>>? calibration = null)
    {
        _open = openConnection;
        _settings = settings ?? new ReviewLevelSettings();
        _calibration = calibration ?? (_ => Task.FromResult(CalibrationSet.Empty));
    }

    public TaskResultIngestor(string connectionString, ReviewLevelSettings? settings = null, Func<CancellationToken, Task<CalibrationSet>>? calibration = null)
        : this(async ct =>
        {
            var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);
            return connection;
        }, settings, calibration)
    {
    }

    /// <summary>For results that arrive as JSON: checks the schema first, then everything else.</summary>
    public Task<IngestResult> SubmitJsonAsync(long taskId, AuditContext worker, string json, string? resultManifestRef = null,
        CancellationToken cancellationToken = default)
    {
        var parsed = StructuredOutput.Parse<TaskResultManifest>(json);
        if (!parsed.IsValid) throw new ResultRejectedException(parsed.Errors);
        return SubmitAsync(taskId, worker, parsed.Value!, resultManifestRef, cancellationToken);
    }

    public async Task<IngestResult> SubmitAsync(long taskId, AuditContext worker, TaskResultManifest manifest, string? resultManifestRef = null,
        CancellationToken cancellationToken = default)
    {
        if (worker.ActorType != ActorType.Worker || string.IsNullOrWhiteSpace(worker.ActorDisplayName))
            throw new ArgumentException("A result is submitted by a worker, so the context must be a worker's, with its id.", nameof(worker));

        var existing = await new ProcessingTaskRepository(_open).GetAsync(taskId, cancellationToken)
            ?? throw new ResultRejectedException([$"Task {taskId} does not exist."]);
        var calibration = await _calibration(cancellationToken);

        return await JobTxn.RunAsync(_open, existing.JobId, worker, cancellationToken, async t =>
        {
            var task = await ProcessingTaskRepository.ReadAsync(t.Connection, t.Transaction, taskId, forUpdate: true, t.Token)
                ?? throw new ResultRejectedException([$"Task {taskId} does not exist."]);

            // Only the worker that currently holds the task, and only while it is running.
            if (task.Status != ProcessingTaskStatus.Running || task.LeaseOwner != worker.ActorDisplayName)
                throw new ResultRejectedException(["This task is not running under this worker, so its result cannot be accepted."]);

            if (task.CancelRequested)
            {
                await ProcessingTaskRepository.FinishAsync(t, task, ProcessingTaskStatus.Cancelled, "Cancelled before its result was stored.");
                await t.AuditForTaskAsync(new AuditEntry("processing", "task.cancelled", "Task cancelled; its result was not stored.", "Task", task.PublicId.ToString("D")),
                    task.ProcessingTaskId, task.ProducerId);
                return new IngestResult(false, true, 0, 0, 0, 0, 0);
            }

            var problems = await ValidateAsync(t, task, manifest);
            if (problems.Count > 0) throw new ResultRejectedException(problems);

            // Provenance comes from the task, which the worker cannot choose.
            var provenance = new Provenance(task.ProducerId, task.ProcessingTaskId);
            provenance.EnsureValid();

            var buildingTypes = await BuildingTypeRepository.ListAsync(t.Connection, t.Transaction, t.Job.JobId, t.Token);
            var byName = buildingTypes.ToDictionary(b => BuildingTypeRepository.Normalize(b.Name).Normalized, b => b.BuildingTypeId);
            var created = 0;
            foreach (var name in manifest.Detections.Select(d => d.BuildingType).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct())
            {
                var (display, normalized) = BuildingTypeRepository.Normalize(name!);
                if (byName.ContainsKey(normalized)) continue;
                var type = await BuildingTypeRepository.InsertAsync(t, display, normalized, BuildingKind.Other, null, worker.ActorDisplayName!);
                byName[normalized] = type.BuildingTypeId;
                created++;
                await t.AuditForTaskAsync(new AuditEntry("setup", "building-type.created", $"Added the building type \"{display}\" from a detector's result.",
                    "BuildingType", type.PublicId.ToString("D")), task.ProcessingTaskId, task.ProducerId);
            }

            var flagged = manifest.Findings.Where(f => f.DetectionId is not null).Select(f => f.DetectionId!).ToHashSet(StringComparer.Ordinal);
            var rows = new DataTable();
            foreach (var (name, type) in EntityColumns) rows.Columns.Add(name, type);
            int raised = 0, batched = 0;

            foreach (var d in manifest.Detections)
            {
                var disposition = d.Disposition ?? Disposition.Included;
                var alwaysAsk = disposition == Disposition.TBD || flagged.Contains(d.DetectionId);
                var decision = ReviewLevelPolicy.Decide(t.Job.ReviewLevel, d, calibration, _settings, alwaysAsk);
                if (decision.Surface == SurfacedAs.Finding) raised++; else batched++;

                long? buildingTypeId = string.IsNullOrWhiteSpace(d.BuildingType)
                    ? null
                    : byName[BuildingTypeRepository.Normalize(d.BuildingType).Normalized];

                rows.Rows.Add(t.Job.JobId, (object?)buildingTypeId ?? DBNull.Value, provenance.ProducerId, provenance.ProcessingTaskId,
                    d.DetectionId, d.Type.Trim(), Nz(d.Category), Nz(d.Tag), Nz(d.Size), (decimal)d.Quantity, d.Unit.ToString(), d.Method.ToString(),
                    d.Family.ToString(), d.Confidence, decision.Surface.ToString(), t.Job.ReviewLevel.ToString(), disposition.ToString(),
                    Nz(d.SheetViewRef), (object?)d.Box?.X0 ?? DBNull.Value, (object?)d.Box?.Y0 ?? DBNull.Value,
                    (object?)d.Box?.X1 ?? DBNull.Value, (object?)d.Box?.Y1 ?? DBNull.Value, Nz(d.Evidence));
            }

            if (rows.Rows.Count > 0)
            {
                using var bulk = new SqlBulkCopy(t.Connection, SqlBulkCopyOptions.CheckConstraints, t.Transaction)
                {
                    DestinationTableName = "ductwork.Entity",
                    BulkCopyTimeout = 120
                };
                foreach (DataColumn column in rows.Columns) bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                await bulk.WriteToServerAsync(rows, t.Token);
            }

            var entityIds = await EntityIdsAsync(t, taskId);
            foreach (var f in manifest.Findings)
            {
                await JobRepository.ExecAsync(t.Connection, t.Transaction,
                    """
                    INSERT INTO ductwork.Finding (JobId, ProcessingTaskId, ProducerId, EntityId, SourceFindingId, Kind, Question, CandidatesJson, Evidence)
                    VALUES (@j, @t, @p, @e, @sid, @kind, @q, @c, @ev);
                    """, t.Token, ("@j", t.Job.JobId), ("@t", taskId), ("@p", task.ProducerId),
                    ("@e", f.DetectionId is not null && entityIds.TryGetValue(f.DetectionId, out var id) ? id : null),
                    ("@sid", f.FindingId), ("@kind", f.Kind.ToString()), ("@q", f.Question.Trim()),
                    ("@c", System.Text.Json.JsonSerializer.Serialize(f.Candidates)), ("@ev", Nz(f.Evidence)));
            }

            await ProcessingTaskRepository.FinishAsync(t, task, ProcessingTaskStatus.Succeeded, null, resultManifestRef);
            await t.AuditForTaskAsync(new AuditEntry("processing", "task.finished", $"{task.TaskType} finished and its result was stored.", "Task",
                task.PublicId.ToString("D"), null, JobRepository.Json(new Dictionary<string, object?>
                {
                    ["entities"] = manifest.Detections.Count, ["findings"] = manifest.Findings.Count, ["buildingTypesCreated"] = created,
                    ["raisedAsFindings"] = raised, ["batched"] = batched, ["attempts"] = task.Attempt, ["resultManifestRef"] = resultManifestRef
                })), task.ProcessingTaskId, task.ProducerId);

            return new IngestResult(true, false, manifest.Detections.Count, manifest.Findings.Count, created, raised, batched);
        });
    }

    // ------------------------------------------------------------------ checks

    private static async Task<List<string>> ValidateAsync(JobTxn t, ProcessingTaskRecord task, TaskResultManifest manifest)
    {
        var problems = new List<string>(ManifestRules.Validate(manifest));

        if (!string.Equals(manifest.TaskPublicId, task.PublicId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            problems.Add("The result is for a different task.");
        if (!string.Equals(manifest.TaskType, task.TaskType, StringComparison.Ordinal))
            problems.Add($"The result says it is a '{manifest.TaskType}' result, but this is a '{task.TaskType}' task.");

        // The worker cannot claim to be a different producer than the one the task was queued for.
        if (manifest.Producer.Validate().Count == 0)
        {
            await using var command = t.Connection.CreateCommand();
            command.Transaction = t.Transaction;
            command.CommandText = "SELECT ProducerKey FROM ductwork.Producer WHERE ProducerId = @p;";
            command.Parameters.AddWithValue("@p", task.ProducerId);
            var expected = (string?)await command.ExecuteScalarAsync(t.Token);
            if (!string.Equals(expected, manifest.Producer.Key(), StringComparison.Ordinal))
                problems.Add("The result names a different producer than the one this task was queued for.");
        }

        foreach (var d in manifest.Detections)
        {
            Length(problems, d.DetectionId, MaxText, "detection id", d.DetectionId);
            Length(problems, d.Type, MaxText, "type", d.DetectionId);
            Length(problems, d.Category, MaxText, "category", d.DetectionId);
            Length(problems, d.Tag, MaxText, "tag", d.DetectionId);
            Length(problems, d.Size, MaxText, "size", d.DetectionId);
            Length(problems, d.BuildingType, MaxText, "building type", d.DetectionId);
            Length(problems, d.SheetViewRef, 200, "sheet view", d.DetectionId);
            Length(problems, d.Evidence, MaxEvidence, "evidence", d.DetectionId);
        }

        foreach (var f in manifest.Findings)
        {
            Length(problems, f.FindingId, MaxText, "finding id", f.FindingId);
            Length(problems, f.Question, 1000, "question", f.FindingId);
        }

        return problems;
    }

    private static void Length(List<string> problems, string? value, int max, string what, string id)
    {
        if (value is not null && value.Length > max)
            problems.Add($"'{id}': the {what} is {value.Length} characters, over the {max} limit.");
    }

    private static async Task<Dictionary<string, long>> EntityIdsAsync(JobTxn t, long taskId)
    {
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var command = t.Connection.CreateCommand();
        command.Transaction = t.Transaction;
        command.CommandText = "SELECT DetectionId, EntityId FROM ductwork.Entity WHERE ProcessingTaskId = @t;";
        command.Parameters.AddWithValue("@t", taskId);
        await using var reader = await command.ExecuteReaderAsync(t.Token);
        while (await reader.ReadAsync(t.Token)) ids[reader.GetString(0)] = reader.GetInt64(1);
        return ids;
    }

    private static object Nz(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    // The order matches the row construction above. Columns not listed take their defaults (review status Proposed, resolution Detector).
    private static readonly (string Name, Type Type)[] EntityColumns =
    [
        ("JobId", typeof(long)), ("BuildingTypeId", typeof(long)), ("ProducerId", typeof(long)), ("ProcessingTaskId", typeof(long)),
        ("DetectionId", typeof(string)), ("Type", typeof(string)), ("Category", typeof(string)), ("Tag", typeof(string)), ("Size", typeof(string)),
        ("Quantity", typeof(decimal)), ("Unit", typeof(string)), ("Method", typeof(string)), ("Family", typeof(string)), ("Confidence", typeof(double)),
        ("SurfacedAs", typeof(string)), ("ReviewLevelAtCreation", typeof(string)), ("Disposition", typeof(string)),
        ("SheetViewRef", typeof(string)), ("BoxX0", typeof(double)), ("BoxY0", typeof(double)), ("BoxX1", typeof(double)), ("BoxY1", typeof(double)),
        ("Evidence", typeof(string))
    ];
}
