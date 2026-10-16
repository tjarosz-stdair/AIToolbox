using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Data;
using AIToolbox.Ductwork.Core.Jobs;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Counting;

/// <summary>Another person changed the item after this one loaded it.</summary>
public sealed class EntityConflictException(int expected, int actual)
    : InvalidOperationException($"This item was changed by someone else (revision {actual} found, {expected} expected). Reload it and try again.")
{
    public int Actual { get; } = actual;
}

public sealed record EntityEdit
{
    public string? Type { get; init; }
    public string? Category { get; init; }
    public string? Tag { get; init; }
    public string? Size { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? CustomerPartCode { get; init; }
    public decimal? Quantity { get; init; }
    public QuantityUnit? Unit { get; init; }
    public long? BuildingTypeId { get; init; }
    public bool ClearBuildingType { get; init; }
    public long? SheetViewId { get; init; }
    public bool ClearSheetView { get; init; }
}

public sealed record NewManualEntity
{
    public required string Type { get; init; }
    public string? Category { get; init; }
    public string? Tag { get; init; }
    public string? Size { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? CustomerPartCode { get; init; }
    public decimal Quantity { get; init; } = 1;
    public QuantityUnit Unit { get; init; } = QuantityUnit.EA;
    public long? BuildingTypeId { get; init; }
    public long? SheetViewId { get; init; }
    public Disposition Disposition { get; init; } = Disposition.Included;
    public string? DispositionReason { get; init; }
    public BoundingBox? Box { get; init; }
    public string? Evidence { get; init; }
}

public sealed record EntityRevisionRecord
{
    public long EntityRevisionId { get; init; }
    public long EntityId { get; init; }
    public int RevisionNo { get; init; }
    public required string Action { get; init; }
    public required string ChangedBy { get; init; }
    public DateTime ChangedUtc { get; init; }
    public string? Reason { get; init; }
    public string? BeforeJson { get; init; }
    public required string AfterJson { get; init; }
    public int? UndoOfRevisionNo { get; init; }
}

/// <summary>
/// Review of detected items by a person (plan sections 6.11 and 7): verify or reject, decide what to do about an item,
/// correct its details, add one the detector missed, and take any of it back. Every change is a new revision that
/// keeps what the item was and what it became, so nothing a person did is ever lost and undo is just another change.
/// Review status and disposition are separate questions and changing one never answers the other. Only a person
/// can do any of this: a detector proposes, a person decides.
/// </summary>
public sealed class EntityReviewRepository
{
    private const int MaxBulk = 5000;

    private static readonly JsonSerializerOptions StateJson = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly Func<CancellationToken, Task<SqlConnection>> _open;

    public EntityReviewRepository(Func<CancellationToken, Task<SqlConnection>> openConnection) => _open = openConnection;

    // ------------------------------------------------------------------ reads

    public async Task<EntityRecord?> GetAsync(long entityId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return await ReadAsync(connection, null, "WHERE EntityId = @id", cancellationToken, ("@id", entityId));
    }

    public async Task<EntityRecord?> GetByPublicIdAsync(long jobId, Guid publicId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return await ReadAsync(connection, null, "WHERE JobId = @j AND PublicId = @p", cancellationToken, ("@j", jobId), ("@p", publicId));
    }

    /// <summary>Turns public ids into internal ones, only for items on this job. An id that is not on the job is simply missing from the answer.</summary>
    public async Task<IReadOnlyDictionary<Guid, long>> GetIdsAsync(long jobId, IReadOnlyCollection<Guid> publicIds, CancellationToken cancellationToken = default)
    {
        var map = new Dictionary<Guid, long>();
        if (publicIds.Count == 0) return map;
        await using var connection = await _open(cancellationToken);
        foreach (var chunk in publicIds.Distinct().Chunk(1000))
        {
            await using var command = connection.CreateCommand();
            var names = chunk.Select((_, i) => "@g" + i).ToList();
            command.CommandText = $"SELECT PublicId, EntityId FROM ductwork.Entity WHERE JobId = @j AND PublicId IN ({string.Join(",", names)});";
            command.Parameters.AddWithValue("@j", jobId);
            for (var i = 0; i < chunk.Length; i++) command.Parameters.AddWithValue(names[i], chunk[i]);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) map[reader.GetGuid(0)] = reader.GetInt64(1);
        }

        return map;
    }

    public async Task<IReadOnlyList<EntityRevisionRecord>> ListRevisionsAsync(long entityId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return await ReadRevisionsAsync(connection, null, "WHERE EntityId = @id ORDER BY RevisionNo", cancellationToken, ("@id", entityId));
    }

    // ------------------------------------------------------------------ review status

    public Task<EntityRecord> SetReviewStatusAsync(long entityId, ReviewStatus status, int expectedRevision, string? reason, EditToken editToken, AuditContext context,
        CancellationToken cancellationToken = default) =>
        InEntityAsync(entityId, context, cancellationToken, editToken, async (t, current) =>
        {
            RequirePerson(context, "review");
            RequireRevision(current, expectedRevision);
            var state = Review(current, status);
            if (state is null) return current;
            return await ApplyAsync(t, current, state, "Reviewed", Clean(reason), null,
                $"{Verb(state.ReviewStatus)} {Describe(current)}.", "review.status");
        });

    /// <summary>Verifies, rejects or reopens many items in one step, for working through a batch. Items already in that state are left alone.</summary>
    public Task<int> SetReviewStatusManyAsync(long jobId, IReadOnlyList<long> entityIds, ReviewStatus status, string? reason, EditToken editToken, AuditContext context,
        CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context, cancellationToken, async t =>
        {
            RequirePerson(context, "review");
            t.RequireEditable();
            if (entityIds.Count == 0) throw new JobRuleViolationException("No items were chosen.");
            if (entityIds.Count > MaxBulk) throw new JobRuleViolationException($"At most {MaxBulk:N0} items can be changed at once.");

            var changed = 0;
            foreach (var id in entityIds.Distinct())
            {
                var current = await ReadAsync(t.Connection, t.Transaction, "WITH (UPDLOCK, ROWLOCK) WHERE EntityId = @id AND JobId = @j", t.Token, ("@id", id), ("@j", jobId))
                    ?? throw new KeyNotFoundException($"Item {id} is not on this job.");
                var state = Review(current, status);
                if (state is null) continue;
                await ApplyAsync(t, current, state, "Reviewed", Clean(reason), null, null, null);
                changed++;
            }

            await t.AuditAtCurrentVersionAsync(new AuditEntry("review", "review.status-many", $"{Verb(status)} {changed} item(s).", "Job", t.Job.PublicId.ToString("D"), null,
                JobRepository.Json(new() { ["status"] = status.ToString(), ["changed"] = changed, ["requested"] = entityIds.Count, ["reason"] = Clean(reason) })));
            return changed;
        }, editToken, requires: JobCapability.Edit);

    // ------------------------------------------------------------------ disposition

    /// <summary>What to do about the item: count it, leave it out, treat it as furnished by the factory, or leave the decision open. Leaving it out needs a reason.</summary>
    public Task<EntityRecord> SetDispositionAsync(long entityId, Disposition disposition, string? reason, int expectedRevision, EditToken editToken, AuditContext context,
        CancellationToken cancellationToken = default) =>
        InEntityAsync(entityId, context, cancellationToken, editToken, async (t, current) =>
        {
            RequirePerson(context, "decide");
            RequireRevision(current, expectedRevision);
            var why = Clean(reason);
            if (disposition is Disposition.Excluded or Disposition.FactoryFurnished && why is null)
                throw new JobRuleViolationException($"Marking an item {disposition} needs a reason, so it can be explained later.");
            if (disposition == current.Disposition && why == current.DispositionReason) return current;

            var state = StateOf(current) with { Disposition = disposition, DispositionReason = disposition is Disposition.Excluded or Disposition.FactoryFurnished ? why : null };
            return await ApplyAsync(t, current, state, "Disposition", why, null,
                $"Set {Describe(current)} to {disposition}.", "review.disposition");
        });

    // ------------------------------------------------------------------ corrections

    public Task<EntityRecord> EditAsync(long entityId, EntityEdit edit, int expectedRevision, string? reason, EditToken editToken, AuditContext context,
        CancellationToken cancellationToken = default) =>
        InEntityAsync(entityId, context, cancellationToken, editToken, async (t, current) =>
        {
            RequirePerson(context, "edit");
            RequireRevision(current, expectedRevision);

            var state = StateOf(current);
            var contentChanged = false;

            string? Text(string? value, string? existing, string name, bool required, ref bool changed)
            {
                if (value is null) return existing;
                var clean = Clean(value, 100);
                if (clean is null && required) throw new JobRuleViolationException($"An item needs a {name}.");
                if (clean != existing) changed = true;
                return clean;
            }

            state = state with
            {
                Type = Text(edit.Type, current.Type, "type", true, ref contentChanged)!,
                Category = Text(edit.Category, current.Category, "category", false, ref contentChanged),
                Tag = Text(edit.Tag, current.Tag, "tag", false, ref contentChanged),
                Size = Text(edit.Size, current.Size, "size", false, ref contentChanged),
                Manufacturer = Text(edit.Manufacturer, current.Manufacturer, "manufacturer", false, ref contentChanged),
                Model = Text(edit.Model, current.Model, "model", false, ref contentChanged),
                CustomerPartCode = Text(edit.CustomerPartCode, current.CustomerPartCode, "part code", false, ref contentChanged)
            };

            if (edit.Quantity is { } quantity)
            {
                if (quantity < 0) throw new JobRuleViolationException("A quantity cannot be negative.");
                if (quantity != current.Quantity) { state = state with { Quantity = quantity }; contentChanged = true; }
            }

            if (edit.Unit is { } unit && unit != current.Unit) { state = state with { Unit = unit }; contentChanged = true; }

            if (edit.ClearBuildingType) state = state with { BuildingTypeId = null };
            else if (edit.BuildingTypeId is { } bt) { await RequireOnJobAsync(t, "BuildingType", "BuildingTypeId", bt, "building type"); state = state with { BuildingTypeId = bt }; }

            if (edit.ClearSheetView) state = state with { SheetViewId = null };
            else if (edit.SheetViewId is { } sv) { await RequireOnJobAsync(t, "SheetView", "SheetViewId", sv, "view"); state = state with { SheetViewId = sv }; }

            // Changing what an item is, or how many, is an override of what the detector said. Placing it is not.
            if (contentChanged && current.ResolutionSource == ResolutionSource.Detector) state = state with { ResolutionSource = ResolutionSource.Override };

            if (state == StateOf(current)) return current;
            return await ApplyAsync(t, current, state, "Edited", Clean(reason), null, $"Edited {Describe(current)}.", "review.edited");
        });

    // ------------------------------------------------------------------ adding what the detector missed

    /// <summary>
    /// A person adds an item the detector missed. It is attributed to a manual producer for that person and to a manual
    /// task, so it carries provenance like everything else, and it needs no verifying because the person is the source.
    /// </summary>
    public async Task<EntityRecord> AddAsync(long jobId, NewManualEntity item, EditToken editToken, AuditContext context, CancellationToken cancellationToken = default)
    {
        RequirePerson(context, "add items");
        var producerId = await EnsureManualProducerAsync(context.ActorEmail!, cancellationToken);

        return await JobTxn.RunAsync(_open, jobId, context, cancellationToken, async t =>
        {
            t.RequireEditable();
            var type = Clean(item.Type, 100) ?? throw new JobRuleViolationException("An item needs a type.");
            if (item.Quantity < 0) throw new JobRuleViolationException("A quantity cannot be negative.");
            var why = Clean(item.DispositionReason);
            if (item.Disposition is Disposition.Excluded or Disposition.FactoryFurnished && why is null)
                throw new JobRuleViolationException($"Marking an item {item.Disposition} needs a reason.");
            if (item.BuildingTypeId is { } bt) await RequireOnJobAsync(t, "BuildingType", "BuildingTypeId", bt, "building type");
            if (item.SheetViewId is { } sv) await RequireOnJobAsync(t, "SheetView", "SheetViewId", sv, "view");

            var taskId = await ManualTaskAsync(t, producerId, context.ActorEmail!);
            await using var command = t.Connection.CreateCommand();
            command.Transaction = t.Transaction;
            command.CommandText =
                """
                INSERT INTO ductwork.Entity (JobId, BuildingTypeId, ProducerId, ProcessingTaskId, DetectionId, Type, Category, Tag, Size, Manufacturer, Model, CustomerPartCode,
                    Quantity, Unit, Method, Family, Confidence, SurfacedAs, ReviewLevelAtCreation, ReviewStatus, Disposition, DispositionReason, ResolutionSource, SheetViewId,
                    BoxX0, BoxY0, BoxX1, BoxY1, Evidence)
                OUTPUT INSERTED.EntityId
                VALUES (@j, @bt, @p, @task, @det, @type, @cat, @tag, @size, @man, @model, @part, @qty, @unit, 'Manual', NULL, 1, 'Batch', @level, 'Manual', @disp, @why, 'UserAdded', @sv,
                    @x0, @y0, @x1, @y1, @ev);
                """;
            void P(string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            P("@j", jobId); P("@bt", item.BuildingTypeId); P("@p", producerId); P("@task", taskId); P("@det", "manual-" + Guid.NewGuid().ToString("N"));
            P("@type", type); P("@cat", Clean(item.Category, 100)); P("@tag", Clean(item.Tag, 100)); P("@size", Clean(item.Size, 100));
            P("@man", Clean(item.Manufacturer, 100)); P("@model", Clean(item.Model, 100)); P("@part", Clean(item.CustomerPartCode, 100));
            P("@qty", item.Quantity); P("@unit", item.Unit.ToString()); P("@level", t.Job.ReviewLevel.ToString()); P("@disp", item.Disposition.ToString());
            P("@why", item.Disposition is Disposition.Excluded or Disposition.FactoryFurnished ? why : null); P("@sv", item.SheetViewId);
            P("@x0", item.Box?.X0); P("@y0", item.Box?.Y0); P("@x1", item.Box?.X1); P("@y1", item.Box?.Y1); P("@ev", Clean(item.Evidence, 1000));
            var id = Convert.ToInt64(await command.ExecuteScalarAsync(t.Token), CultureInfo.InvariantCulture);

            var created = (await ReadAsync(t.Connection, t.Transaction, "WHERE EntityId = @id", t.Token, ("@id", id)))!;
            await JobRepository.ExecAsync(t.Connection, t.Transaction,
                "INSERT INTO ductwork.EntityRevision (JobId, EntityId, RevisionNo, Action, ChangedBy, AfterJson) VALUES (@j, @e, 1, 'Added', @by, @after);", t.Token,
                ("@j", jobId), ("@e", id), ("@by", JobRepository.Actor(context)), ("@after", JsonSerializer.Serialize(StateOf(created), StateJson)));
            await t.AuditForTaskAsync(new AuditEntry("review", "entity.added", $"Added {Describe(created)} by hand.", "Entity", created.PublicId.ToString("D"), null,
                JobRepository.Json(new() { ["type"] = created.Type, ["size"] = created.Size, ["quantity"] = created.Quantity, ["unit"] = created.Unit.ToString() })), taskId, producerId);
            return created;
        }, editToken, requires: JobCapability.Edit);
    }

    // ------------------------------------------------------------------ undo

    /// <summary>Takes back the latest change to an item by restoring what it was before. The undo is itself a revision, so it can be undone too.</summary>
    public Task<EntityRecord> UndoAsync(long entityId, EditToken editToken, AuditContext context, CancellationToken cancellationToken = default) =>
        InEntityAsync(entityId, context, cancellationToken, editToken, async (t, current) =>
        {
            RequirePerson(context, "undo");
            return await UndoCoreAsync(t, current);
        });

    /// <summary>Takes back the last thing this person changed on the job that is still the latest change to its item.</summary>
    public Task<EntityRecord> UndoLastAsync(long jobId, EditToken editToken, AuditContext context, CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context, cancellationToken, async t =>
        {
            RequirePerson(context, "undo");
            t.RequireEditable();
            var entityId = await FindUndoableAsync(t, JobRepository.Actor(context))
                ?? throw new JobRuleViolationException("There is nothing of yours to undo.");
            var current = (await ReadAsync(t.Connection, t.Transaction, "WITH (UPDLOCK, ROWLOCK) WHERE EntityId = @id", t.Token, ("@id", entityId)))!;
            return await UndoCoreAsync(t, current);
        }, editToken, requires: JobCapability.Edit);

    private static async Task<long?> FindUndoableAsync(JobTxn t, string by)
    {
        await using var command = t.Connection.CreateCommand();
        command.Transaction = t.Transaction;
        command.CommandText =
            """
            SELECT TOP 1 r.EntityId
            FROM ductwork.EntityRevision r
            JOIN ductwork.Entity e ON e.EntityId = r.EntityId AND e.Revision = r.RevisionNo
            WHERE r.JobId = @j AND r.ChangedBy = @by AND r.Action <> 'Added'
            ORDER BY r.EntityRevisionId DESC;
            """;
        command.Parameters.AddWithValue("@j", t.Job.JobId);
        command.Parameters.AddWithValue("@by", by);
        var result = await command.ExecuteScalarAsync(t.Token);
        return result is null or DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private async Task<EntityRecord> UndoCoreAsync(JobTxn t, EntityRecord current)
    {
        t.RequireEditable();
        var latest = (await ReadRevisionsAsync(t.Connection, t.Transaction, "WHERE EntityId = @id AND RevisionNo = @n", t.Token, ("@id", current.EntityId), ("@n", current.Revision))).SingleOrDefault();
        if (latest is null || latest.Action == "Added" || latest.BeforeJson is null)
            throw new JobRuleViolationException("There is nothing to undo on this item. Reject it instead if it should not be counted.");

        var before = JsonSerializer.Deserialize<EntityState>(latest.BeforeJson, StateJson)!;
        await RequireStillValidAsync(t, before);
        return await ApplyAsync(t, current, before, "Undone", null, latest.RevisionNo, $"Undid the last change to {Describe(current)}.", "review.undone");
    }

    // ------------------------------------------------------------------ the one place an item changes

    private sealed record EntityState
    {
        public long? BuildingTypeId { get; init; }
        public long? SheetViewId { get; init; }
        public required string Type { get; init; }
        public string? Category { get; init; }
        public string? Tag { get; init; }
        public string? Size { get; init; }
        public string? Manufacturer { get; init; }
        public string? Model { get; init; }
        public string? CustomerPartCode { get; init; }
        public decimal Quantity { get; init; }
        public QuantityUnit Unit { get; init; }
        public ReviewStatus ReviewStatus { get; init; }
        public Disposition Disposition { get; init; }
        public string? DispositionReason { get; init; }
        public ResolutionSource ResolutionSource { get; init; }
    }

    private static EntityState StateOf(EntityRecord e) => new()
    {
        BuildingTypeId = e.BuildingTypeId, SheetViewId = e.SheetViewId, Type = e.Type, Category = e.Category, Tag = e.Tag, Size = e.Size, Manufacturer = e.Manufacturer,
        Model = e.Model, CustomerPartCode = e.CustomerPartCode, Quantity = e.Quantity, Unit = e.Unit, ReviewStatus = e.ReviewStatus, Disposition = e.Disposition,
        DispositionReason = e.DispositionReason, ResolutionSource = e.ResolutionSource
    };

    private static async Task<EntityRecord> ApplyAsync(JobTxn t, EntityRecord current, EntityState after, string action, string? reason, int? undoOf, string? summary, string? auditAction)
    {
        var next = current.Revision + 1;
        var moved = await JobRepository.ExecAsync(t.Connection, t.Transaction,
            """
            UPDATE ductwork.Entity
            SET BuildingTypeId = @bt, SheetViewId = @sv, Type = @type, Category = @cat, Tag = @tag, Size = @size, Manufacturer = @man, Model = @model, CustomerPartCode = @part,
                Quantity = @qty, Unit = @unit, ReviewStatus = @rs, Disposition = @disp, DispositionReason = @why, ResolutionSource = @res, Revision = @next
            WHERE EntityId = @id AND Revision = @cur;
            """, t.Token,
            ("@bt", after.BuildingTypeId), ("@sv", after.SheetViewId), ("@type", after.Type), ("@cat", after.Category), ("@tag", after.Tag), ("@size", after.Size),
            ("@man", after.Manufacturer), ("@model", after.Model), ("@part", after.CustomerPartCode), ("@qty", after.Quantity), ("@unit", after.Unit.ToString()),
            ("@rs", after.ReviewStatus.ToString()), ("@disp", after.Disposition.ToString()), ("@why", after.DispositionReason), ("@res", after.ResolutionSource.ToString()),
            ("@next", next), ("@id", current.EntityId), ("@cur", current.Revision));
        if (moved == 0) throw new EntityConflictException(current.Revision, current.Revision + 1);

        await JobRepository.ExecAsync(t.Connection, t.Transaction,
            """
            INSERT INTO ductwork.EntityRevision (JobId, EntityId, RevisionNo, Action, ChangedBy, Reason, BeforeJson, AfterJson, UndoOfRevisionNo)
            VALUES (@j, @e, @n, @a, @by, @why, @before, @after, @undo);
            """, t.Token,
            ("@j", current.JobId), ("@e", current.EntityId), ("@n", next), ("@a", action), ("@by", JobRepository.Actor(t.Context)), ("@why", reason),
            ("@before", JsonSerializer.Serialize(StateOf(current), StateJson)), ("@after", JsonSerializer.Serialize(after, StateJson)), ("@undo", undoOf));

        if (summary is not null && auditAction is not null)
            await t.AuditForTaskAsync(new AuditEntry("review", auditAction, summary, "Entity", current.PublicId.ToString("D"),
                JsonSerializer.Serialize(StateOf(current), StateJson), JsonSerializer.Serialize(after, StateJson)), current.ProcessingTaskId, current.ProducerId);

        return (await ReadAsync(t.Connection, t.Transaction, "WHERE EntityId = @id", t.Token, ("@id", current.EntityId)))!;
    }

    // ------------------------------------------------------------------ helpers

    private async Task<T> InEntityAsync<T>(long entityId, AuditContext context, CancellationToken ct, EditToken editToken, Func<JobTxn, EntityRecord, Task<T>> body)
    {
        var existing = await GetAsync(entityId, ct) ?? throw new KeyNotFoundException($"Item {entityId} does not exist.");
        return await JobTxn.RunAsync(_open, existing.JobId, context, ct, async t =>
        {
            t.RequireEditable();
            var current = await ReadAsync(t.Connection, t.Transaction, "WITH (UPDLOCK, ROWLOCK) WHERE EntityId = @id", t.Token, ("@id", entityId))
                ?? throw new KeyNotFoundException($"Item {entityId} no longer exists.");
            return await body(t, current);
        }, editToken, requires: JobCapability.Edit);
    }

    private static void RequirePerson(AuditContext context, string what)
    {
        if (context.ActorType != ActorType.User) throw new JobRuleViolationException($"Only a person can {what}. A detector proposes; a person decides.");
    }

    private static void RequireRevision(EntityRecord current, int expected)
    {
        if (current.Revision != expected) throw new EntityConflictException(expected, current.Revision);
    }

    /// <summary>The state an item moves to when its review status is set, or null if it is already there or the move makes no sense.</summary>
    private static EntityState? Review(EntityRecord current, ReviewStatus target)
    {
        var handAdded = current.ResolutionSource == ResolutionSource.UserAdded;
        var next = target switch
        {
            ReviewStatus.Verified when handAdded => throw new JobRuleViolationException("An item you added by hand has nothing to verify. Edit it, or reject it if it should not be counted."),
            ReviewStatus.Verified or ReviewStatus.Rejected => target,
            // Reopening puts a detected item back for review, and a hand-added one back as it was added.
            ReviewStatus.Proposed => handAdded ? ReviewStatus.Manual : ReviewStatus.Proposed,
            _ => throw new JobRuleViolationException("An item cannot be set to Manual; that is only for items added by hand.")
        };
        return next == current.ReviewStatus ? null : StateOf(current) with { ReviewStatus = next };
    }

    private static string Verb(ReviewStatus s) => s switch
    {
        ReviewStatus.Verified => "Verified",
        ReviewStatus.Rejected => "Rejected",
        _ => "Reopened"
    };

    private static string Describe(EntityRecord e) => $"{e.Type}{(string.IsNullOrWhiteSpace(e.Size) ? "" : " " + e.Size)}{(string.IsNullOrWhiteSpace(e.Tag) ? "" : " (" + e.Tag + ")")}";

    private static string? Clean(string? text, int max = 400)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }

    private static async Task RequireOnJobAsync(JobTxn t, string table, string column, long id, string label)
    {
        await using var command = t.Connection.CreateCommand();
        command.Transaction = t.Transaction;
        command.CommandText = $"SELECT JobId FROM ductwork.{table} WHERE {column} = @id;";
        command.Parameters.AddWithValue("@id", id);
        var result = await command.ExecuteScalarAsync(t.Token);
        if (result is null or DBNull || Convert.ToInt64(result, CultureInfo.InvariantCulture) != t.Job.JobId)
            throw new JobRuleViolationException($"That {label} is not on this job.");
    }

    /// <summary>An undo must not point an item at a building type or view that has been deleted since.</summary>
    private static async Task RequireStillValidAsync(JobTxn t, EntityState state)
    {
        if (state.BuildingTypeId is { } bt) await RequireExistsAsync(t, "BuildingType", "BuildingTypeId", bt, "building type");
        if (state.SheetViewId is { } sv) await RequireExistsAsync(t, "SheetView", "SheetViewId", sv, "view");
    }

    private static async Task RequireExistsAsync(JobTxn t, string table, string column, long id, string label)
    {
        try { await RequireOnJobAsync(t, table, column, id, label); }
        catch (JobRuleViolationException) { throw new JobRuleViolationException($"This cannot be undone because the {label} it used no longer exists."); }
    }

    private async Task<long> EnsureManualProducerAsync(string email, CancellationToken ct)
    {
        await using var connection = await _open(ct);
        return await ProducerStore.EnsureProducerAsync(connection, ProducerRef.Manual(email), ct);
    }

    /// <summary>One completed "manual entry" task per person per job, so every item has a task and nobody has to create one.</summary>
    private static async Task<long> ManualTaskAsync(JobTxn t, long producerId, string email)
    {
        await using (var find = t.Connection.CreateCommand())
        {
            find.Transaction = t.Transaction;
            find.CommandText = "SELECT TOP 1 ProcessingTaskId FROM ductwork.ProcessingTask WHERE JobId = @j AND TaskType = 'manual-entry' AND ProducerId = @p ORDER BY ProcessingTaskId;";
            find.Parameters.AddWithValue("@j", t.Job.JobId);
            find.Parameters.AddWithValue("@p", producerId);
            var existing = await find.ExecuteScalarAsync(t.Token);
            if (existing is not null and not DBNull) return Convert.ToInt64(existing, CultureInfo.InvariantCulture);
        }

        await using var insert = t.Connection.CreateCommand();
        insert.Transaction = t.Transaction;
        insert.CommandText =
            """
            INSERT INTO ductwork.ProcessingTask (JobId, TaskType, Status, MaxAttempts, ProducerId, ContractVersion, CreatedBy, StartedUtc, FinishedUtc)
            OUTPUT INSERTED.ProcessingTaskId
            VALUES (@j, 'manual-entry', 'Succeeded', 1, @p, @v, @by, SYSUTCDATETIME(), SYSUTCDATETIME());
            """;
        insert.Parameters.AddWithValue("@j", t.Job.JobId);
        insert.Parameters.AddWithValue("@p", producerId);
        insert.Parameters.AddWithValue("@v", ContractInfo.Version);
        insert.Parameters.AddWithValue("@by", email);
        return Convert.ToInt64(await insert.ExecuteScalarAsync(t.Token), CultureInfo.InvariantCulture);
    }

    private static async Task<EntityRecord?> ReadAsync(SqlConnection c, SqlTransaction? tx, string whereAndHint, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        // A locking hint belongs right after the table, before the WHERE.
        var hint = whereAndHint.StartsWith("WITH", StringComparison.Ordinal) ? whereAndHint[..(whereAndHint.IndexOf(')') + 1)] + " " : "";
        var where = whereAndHint[hint.Length..];
        command.CommandText = $"SELECT {EntityRepository.Columns} FROM ductwork.Entity {hint}{where};";
        foreach (var (name, value) in args) JobRepository.P(command, name, value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? EntityRepository.Map(reader) : null;
    }

    private static async Task<List<EntityRevisionRecord>> ReadRevisionsAsync(SqlConnection c, SqlTransaction? tx, string where, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            "SELECT EntityRevisionId, EntityId, RevisionNo, Action, ChangedBy, ChangedUtc, Reason, BeforeJson, AfterJson, UndoOfRevisionNo FROM ductwork.EntityRevision " + where;
        foreach (var (name, value) in args) JobRepository.P(command, name, value);
        var list = new List<EntityRevisionRecord>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new EntityRevisionRecord
            {
                EntityRevisionId = r.GetInt64(0), EntityId = r.GetInt64(1), RevisionNo = r.GetInt32(2), Action = r.GetString(3), ChangedBy = r.GetString(4), ChangedUtc = r.GetDateTime(5),
                Reason = r.IsDBNull(6) ? null : r.GetString(6), BeforeJson = r.IsDBNull(7) ? null : r.GetString(7), AfterJson = r.GetString(8), UndoOfRevisionNo = r.IsDBNull(9) ? null : r.GetInt32(9)
            });
        return list;
    }
}
