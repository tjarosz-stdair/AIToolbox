using System.Globalization;
using System.Text.Json;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Tasks;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Drawings;

public enum SheetKind { Plan, Schedule, Legend, Notes, Detail, Riser, Other }

public enum ViewType { Plan, Section, Elevation, Detail, Schedule, Riser, Other }

public enum ScaleMethod { Stated, Calibrated }

public enum ViewExtraction { Geometry, LabelCount, Schedule, Vision, Manual }

public enum RegionKind { Include, Exclude }

/// <summary>One page of a drawing file, as found by whoever read the file.</summary>
public sealed record NewSheet
{
    public int PageNo { get; init; }
    public double WidthPts { get; init; }
    public double HeightPts { get; init; }
    public int Rotation { get; init; }
    public string? SheetNumber { get; init; }
    public string? Title { get; init; }
    public string? Discipline { get; init; }
    public SheetKind Kind { get; init; } = SheetKind.Other;
    /// <summary>SHA-256 of the page's content, as hex. Pages with the same content are read once.</summary>
    public string? ContentHashHex { get; init; }
    public bool HasTextLayer { get; init; } = true;
}

public sealed record SheetEdit
{
    public string? SheetNumber { get; init; }
    public string? Title { get; init; }
    public string? Discipline { get; init; }
    public SheetKind? Kind { get; init; }
    public bool? InScope { get; init; }
}

public sealed record NewView
{
    public required string Title { get; init; }
    public string? Floor { get; init; }
    public ViewType Type { get; init; } = ViewType.Plan;
    public long? BuildingTypeId { get; init; }
    /// <summary>Null means the whole page.</summary>
    public IReadOnlyList<double[]>? Polygon { get; init; }
}

public sealed record ViewEdit
{
    public string? Title { get; init; }
    public string? Floor { get; init; }
    public ViewType? Type { get; init; }
    public long? BuildingTypeId { get; init; }
    public bool ClearBuildingType { get; init; }
    public IReadOnlyList<double[]>? Polygon { get; init; }
    public bool ClearPolygon { get; init; }
}

public sealed record SheetViewRecord
{
    public long SheetViewId { get; init; }
    public Guid PublicId { get; init; }
    public long JobId { get; init; }
    public long SheetId { get; init; }
    public required string Title { get; init; }
    public string? PolygonJson { get; init; }
    public double? ScaleRatio { get; init; }
    public ScaleMethod? ScaleMethod { get; init; }
    public string? ScaleCalibrationJson { get; init; }
    public string? ScaleSetBy { get; init; }
    public DateTime? ScaleSetUtc { get; init; }
    public string? Floor { get; init; }
    public ViewType Type { get; init; }
    public ViewExtraction ExtractionMethod { get; init; }
    public long? BuildingTypeId { get; init; }
    public required string CreatedBy { get; init; }
    public DateTime CreatedUtc { get; init; }
}

public sealed record SheetRegionRecord
{
    public long SheetRegionId { get; init; }
    public Guid PublicId { get; init; }
    public long JobId { get; init; }
    public long SheetId { get; init; }
    public RegionKind Kind { get; init; }
    public string? Reason { get; init; }
    public required string PolygonJson { get; init; }
    public required string CreatedBy { get; init; }
    public DateTime CreatedUtc { get; init; }
}

public sealed record SheetRecord
{
    public long SheetId { get; init; }
    public Guid PublicId { get; init; }
    public long JobId { get; init; }
    public long RevisionId { get; init; }
    public long SourceFileId { get; init; }
    public Guid SourceFilePublicId { get; init; }
    public int PageNo { get; init; }
    public string? SheetNumber { get; init; }
    public string? Title { get; init; }
    public string? Discipline { get; init; }
    public SheetKind Kind { get; init; }
    public string? ContentHash { get; init; }
    public Guid? DuplicateOfPublicId { get; init; }
    public bool HasTextLayer { get; init; }
    public double WidthPts { get; init; }
    public double HeightPts { get; init; }
    public int Rotation { get; init; }
    public bool InScope { get; init; }
    public long? ProcessingTaskId { get; init; }
    public required string CreatedBy { get; init; }
    public DateTime CreatedUtc { get; init; }
    public IReadOnlyList<SheetViewRecord> Views { get; init; } = [];
    public IReadOnlyList<SheetRegionRecord> Regions { get; init; } = [];
}

/// <summary>
/// Sheets, their views and regions (plan sections 6.3 and 7). Which pages a file holds is a fact someone read from
/// it: a person registers them, or the worker does under the task it holds. What a view is, and above all what its
/// scale is, is decided by a person: a scale is never guessed, so a view with none says "not set" and nothing is measured from it.
/// </summary>
public sealed class SheetRepository
{
    private const int MaxPagesPerRegistration = 2000;
    private const int MaxPageNo = 10_000;
    private const int MaxPolygonPoints = 500;

    private readonly Func<CancellationToken, Task<SqlConnection>> _open;

    public SheetRepository(Func<CancellationToken, Task<SqlConnection>> openConnection) => _open = openConnection;

    // ------------------------------------------------------------------ reads

    public async Task<IReadOnlyList<SheetRecord>> ListByJobAsync(long jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return await ListAsync(connection, null, "WHERE s.JobId = @j", cancellationToken, ("@j", jobId));
    }

    public async Task<SheetRecord?> GetAsync(long sheetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return (await ListAsync(connection, null, "WHERE s.SheetId = @id", cancellationToken, ("@id", sheetId))).SingleOrDefault();
    }

    public async Task<SheetRecord?> GetByPublicIdAsync(long jobId, Guid publicId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return (await ListAsync(connection, null, "WHERE s.JobId = @j AND s.PublicId = @p", cancellationToken, ("@j", jobId), ("@p", publicId))).SingleOrDefault();
    }

    public async Task<SheetViewRecord?> GetViewAsync(long jobId, Guid publicId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return (await ReadViewsAsync(connection, null, "WHERE v.JobId = @j AND v.PublicId = @p", cancellationToken, ("@j", jobId), ("@p", publicId))).SingleOrDefault();
    }

    public async Task<SheetRegionRecord?> GetRegionAsync(long jobId, Guid publicId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return (await ReadRegionsAsync(connection, null, "WHERE r.JobId = @j AND r.PublicId = @p", cancellationToken, ("@j", jobId), ("@p", publicId))).SingleOrDefault();
    }

    // ------------------------------------------------------------------ registering pages

    /// <summary>A person records the pages of a file.</summary>
    public Task<IReadOnlyList<SheetRecord>> RegisterAsync(long jobId, Guid sourceFilePublicId, IReadOnlyList<NewSheet> sheets, EditToken editToken, AuditContext context,
        CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context, cancellationToken,
            t => RegisterCoreAsync(t, sourceFilePublicId, sheets, null), editToken, requires: JobCapability.Edit);

    /// <summary>The worker records the pages of a file it was asked to read, under the task it holds.</summary>
    public async Task<IReadOnlyList<SheetRecord>> RegisterFromTaskAsync(long taskId, Guid sourceFilePublicId, IReadOnlyList<NewSheet> sheets, AuditContext worker,
        CancellationToken cancellationToken = default)
    {
        if (worker.ActorType != ActorType.Worker || string.IsNullOrWhiteSpace(worker.ActorDisplayName))
            throw new ArgumentException("Pages are reported by a worker, so the context must be a worker's, with its id.", nameof(worker));

        var existing = await new ProcessingTaskRepository(_open).GetAsync(taskId, cancellationToken)
            ?? throw new JobRuleViolationException($"Task {taskId} does not exist.");
        return await JobTxn.RunAsync(_open, existing.JobId, worker, cancellationToken, async t =>
        {
            var task = await ProcessingTaskRepository.ReadAsync(t.Connection, t.Transaction, taskId, forUpdate: false, t.Token)
                ?? throw new JobRuleViolationException($"Task {taskId} does not exist.");
            if (task.Status != ProcessingTaskStatus.Running || task.LeaseOwner != worker.ActorDisplayName)
                throw new JobRuleViolationException("This task is not running under this worker, so its pages cannot be accepted.");
            if (task.CancelRequested) throw new JobRuleViolationException("This task was cancelled, so its pages were not stored.");
            return await RegisterCoreAsync(t, sourceFilePublicId, sheets, taskId);
        });
    }

    private static async Task<IReadOnlyList<SheetRecord>> RegisterCoreAsync(JobTxn t, Guid sourceFilePublicId, IReadOnlyList<NewSheet> sheets, long? taskId)
    {
        t.RequireEditable();
        if (sheets.Count == 0) throw new JobRuleViolationException("There are no pages to record.");
        if (sheets.Count > MaxPagesPerRegistration) throw new JobRuleViolationException($"At most {MaxPagesPerRegistration:N0} pages can be recorded at once.");

        var file = await ReadFileRowAsync(t, sourceFilePublicId)
            ?? throw new KeyNotFoundException("That file does not exist on this job.");
        if (file.IsDuplicate) throw new JobRuleViolationException("That file is a copy of one already on this job. Record the pages of the original file.");

        var problems = new List<string>();
        foreach (var page in sheets.GroupBy(s => s.PageNo).Where(g => g.Count() > 1)) problems.Add($"Page {page.Key} is listed more than once.");
        foreach (var s in sheets)
        {
            if (s.PageNo < 1 || s.PageNo > MaxPageNo) problems.Add($"Page number {s.PageNo} is not valid.");
            if (!(s.WidthPts > 0 && s.HeightPts > 0) || double.IsInfinity(s.WidthPts) || double.IsInfinity(s.HeightPts)) problems.Add($"Page {s.PageNo} has no usable size.");
            if (s.Rotation is not (0 or 90 or 180 or 270)) problems.Add($"Page {s.PageNo} has a rotation of {s.Rotation}, which is not 0, 90, 180 or 270.");
            if (s.ContentHashHex is { } h && (h.Length != 64 || !h.All(Uri.IsHexDigit))) problems.Add($"Page {s.PageNo} has a content hash that is not SHA-256 hex.");
        }

        if (problems.Count > 0) throw new JobRuleViolationException(string.Join(" ", problems.Distinct()));

        var taken = await ScalarListAsync<int>(t, "SELECT PageNo FROM ductwork.Sheet WHERE SourceFileId = @f;", ("@f", file.SourceFileId));
        var already = sheets.Where(s => taken.Contains(s.PageNo)).Select(s => s.PageNo).ToList();
        if (already.Count > 0) throw new JobRuleViolationException($"Page(s) {string.Join(", ", already)} of this file are already recorded.");

        var by = JobRepository.Actor(t.Context);
        var duplicates = 0;
        foreach (var s in sheets.OrderBy(s => s.PageNo))
        {
            var hash = s.ContentHashHex is null ? null : Convert.FromHexString(s.ContentHashHex);
            long? duplicateOf = null;
            if (hash is not null)
                duplicateOf = await ScalarOrNullAsync(t, "SELECT TOP 1 SheetId FROM ductwork.Sheet WHERE JobId = @j AND ContentHash = @h AND DuplicateOfSheetId IS NULL ORDER BY SheetId;",
                    ("@j", t.Job.JobId), ("@h", hash));
            if (duplicateOf is not null) duplicates++;

            await JobRepository.ExecAsync(t.Connection, t.Transaction,
                """
                INSERT INTO ductwork.Sheet (JobId, RevisionId, SourceFileId, PageNo, SheetNumber, Title, Discipline, SheetKind, ContentHash, DuplicateOfSheetId,
                                            HasTextLayer, WidthPts, HeightPts, Rotation, InScope, ProcessingTaskId, CreatedBy)
                VALUES (@j, @r, @f, @p, @num, @title, @disc, @kind, @h, @dup, @text, @w, @ht, @rot, @scope, @task, @by);
                """, t.Token,
                ("@j", t.Job.JobId), ("@r", file.RevisionId), ("@f", file.SourceFileId), ("@p", s.PageNo), ("@num", Clean(s.SheetNumber, 40)), ("@title", Clean(s.Title, 200)),
                ("@disc", Clean(s.Discipline, 20)), ("@kind", s.Kind.ToString()), ("@h", (object?)hash ?? System.Data.SqlTypes.SqlBinary.Null), ("@dup", duplicateOf),
                ("@text", s.HasTextLayer), ("@w", s.WidthPts), ("@ht", s.HeightPts), ("@rot", s.Rotation),
                // A page the job already holds is left out of scope, so it is read once. A person can bring it back.
                ("@scope", duplicateOf is null), ("@task", taskId), ("@by", by));
        }

        await t.AuditAtCurrentVersionAsync(new AuditEntry("sheets", "sheets.registered",
            $"Recorded {sheets.Count} page(s) of \"{file.OriginalName}\"" + (duplicates > 0 ? $", {duplicates} of them identical to a page already on this job." : "."),
            "SourceFile", sourceFilePublicId.ToString("D"), null,
            JobRepository.Json(new() { ["pages"] = sheets.Count, ["duplicates"] = duplicates, ["file"] = file.OriginalName })));

        return await ListAsync(t.Connection, t.Transaction, "WHERE s.SourceFileId = @f ORDER BY s.PageNo", t.Token, ("@f", file.SourceFileId));
    }

    // ------------------------------------------------------------------ sheet details

    public Task<SheetRecord> UpdateSheetAsync(long sheetId, SheetEdit edit, EditToken editToken, AuditContext context, CancellationToken cancellationToken = default) =>
        InSheetAsync(sheetId, context, cancellationToken, editToken, async (t, current) =>
        {
            t.RequireEditable();
            var sets = new List<string>();
            var args = new List<(string, object?)> { ("@id", sheetId) };
            var before = new Dictionary<string, object?>();
            var after = new Dictionary<string, object?>();

            void Change(string column, string key, object? from, object? to, object? value)
            {
                if (Equals(from, to)) return;
                sets.Add($"{column} = @{key}");
                args.Add(("@" + key, value));
                before[key] = from;
                after[key] = to;
            }

            if (edit.SheetNumber is not null) Change("SheetNumber", "number", current.SheetNumber, Clean(edit.SheetNumber, 40), Clean(edit.SheetNumber, 40));
            if (edit.Title is not null) Change("Title", "title", current.Title, Clean(edit.Title, 200), Clean(edit.Title, 200));
            if (edit.Discipline is not null) Change("Discipline", "discipline", current.Discipline, Clean(edit.Discipline, 20), Clean(edit.Discipline, 20));
            if (edit.Kind is { } kind) Change("SheetKind", "kind", current.Kind.ToString(), kind.ToString(), kind.ToString());
            if (edit.InScope is { } scope) Change("InScope", "scope", current.InScope, scope, scope);

            if (sets.Count == 0) return current;
            await JobRepository.ExecAsync(t.Connection, t.Transaction, $"UPDATE ductwork.Sheet SET {string.Join(", ", sets)} WHERE SheetId = @id;", t.Token, args.ToArray());
            await t.AuditAtCurrentVersionAsync(new AuditEntry("sheets", "sheet.edited", $"Edited sheet {Label(current)}.", "Sheet", current.PublicId.ToString("D"),
                JobRepository.Json(before), JobRepository.Json(after)));
            return (await ListAsync(t.Connection, t.Transaction, "WHERE s.SheetId = @id", t.Token, ("@id", sheetId))).Single();
        });

    // ------------------------------------------------------------------ views

    public Task<SheetViewRecord> CreateViewAsync(long sheetId, NewView view, EditToken editToken, AuditContext context, CancellationToken cancellationToken = default) =>
        InSheetAsync(sheetId, context, cancellationToken, editToken, async (t, sheet) =>
        {
            t.RequireEditable();
            var title = Clean(view.Title, 200) ?? throw new JobRuleViolationException("A view needs a title.");
            var polygon = view.Polygon is null ? null : PolygonJson(view.Polygon, sheet);
            if (view.BuildingTypeId is { } bt) await RequireBuildingTypeAsync(t, bt);

            var id = await ScalarAsync(t,
                """
                INSERT INTO ductwork.SheetView (JobId, SheetId, Title, RegionPolygonJson, Floor, ViewType, BuildingTypeId, CreatedBy)
                OUTPUT INSERTED.SheetViewId
                VALUES (@j, @s, @title, @poly, @floor, @type, @bt, @by);
                """,
                ("@j", sheet.JobId), ("@s", sheetId), ("@title", title), ("@poly", polygon), ("@floor", Clean(view.Floor, 50)), ("@type", view.Type.ToString()),
                ("@bt", view.BuildingTypeId), ("@by", JobRepository.Actor(context)));
            var created = (await ReadViewsAsync(t.Connection, t.Transaction, "WHERE v.SheetViewId = @id", t.Token, ("@id", id))).Single();
            await t.AuditAtCurrentVersionAsync(new AuditEntry("sheets", "view.created", $"Added the view \"{title}\" on sheet {Label(sheet)}.", "SheetView", created.PublicId.ToString("D"), null,
                JobRepository.Json(new() { ["title"] = title, ["floor"] = created.Floor, ["type"] = created.Type.ToString(), ["buildingTypeId"] = created.BuildingTypeId })));
            return created;
        });

    public Task<SheetViewRecord> UpdateViewAsync(long viewId, ViewEdit edit, EditToken editToken, AuditContext context, CancellationToken cancellationToken = default) =>
        InViewAsync(viewId, context, cancellationToken, editToken, async (t, view, sheet) =>
        {
            t.RequireEditable();
            var sets = new List<string>();
            var args = new List<(string, object?)> { ("@id", viewId) };
            var before = new Dictionary<string, object?>();
            var after = new Dictionary<string, object?>();

            if (edit.Title is not null)
            {
                var title = Clean(edit.Title, 200) ?? throw new JobRuleViolationException("A view needs a title.");
                if (title != view.Title) { sets.Add("Title = @title"); args.Add(("@title", title)); before["title"] = view.Title; after["title"] = title; }
            }

            if (edit.Floor is not null && Clean(edit.Floor, 50) != view.Floor)
            {
                sets.Add("Floor = @floor"); args.Add(("@floor", Clean(edit.Floor, 50))); before["floor"] = view.Floor; after["floor"] = Clean(edit.Floor, 50);
            }

            if (edit.Type is { } type && type != view.Type)
            {
                sets.Add("ViewType = @type"); args.Add(("@type", type.ToString())); before["type"] = view.Type.ToString(); after["type"] = type.ToString();
            }

            if (edit.ClearBuildingType && view.BuildingTypeId is not null)
            {
                sets.Add("BuildingTypeId = NULL"); before["buildingTypeId"] = view.BuildingTypeId; after["buildingTypeId"] = null;
            }
            else if (edit.BuildingTypeId is { } bt && bt != view.BuildingTypeId)
            {
                await RequireBuildingTypeAsync(t, bt);
                sets.Add("BuildingTypeId = @bt"); args.Add(("@bt", bt)); before["buildingTypeId"] = view.BuildingTypeId; after["buildingTypeId"] = bt;
            }

            if (edit.ClearPolygon && view.PolygonJson is not null)
            {
                sets.Add("RegionPolygonJson = NULL"); before["region"] = "set"; after["region"] = "whole page";
            }
            else if (edit.Polygon is not null)
            {
                sets.Add("RegionPolygonJson = @poly"); args.Add(("@poly", PolygonJson(edit.Polygon, sheet))); before["region"] = view.PolygonJson is null ? "whole page" : "set"; after["region"] = "set";
            }

            if (sets.Count == 0) return view;
            await JobRepository.ExecAsync(t.Connection, t.Transaction, $"UPDATE ductwork.SheetView SET {string.Join(", ", sets)} WHERE SheetViewId = @id;", t.Token, args.ToArray());
            await t.AuditAtCurrentVersionAsync(new AuditEntry("sheets", "view.edited", $"Edited the view \"{view.Title}\" on sheet {Label(sheet)}.", "SheetView", view.PublicId.ToString("D"),
                JobRepository.Json(before), JobRepository.Json(after)));
            return (await ReadViewsAsync(t.Connection, t.Transaction, "WHERE v.SheetViewId = @id", t.Token, ("@id", viewId))).Single();
        });

    public Task<bool> DeleteViewAsync(long viewId, EditToken editToken, AuditContext context, CancellationToken cancellationToken = default) =>
        InViewAsync(viewId, context, cancellationToken, editToken, async (t, view, sheet) =>
        {
            t.RequireEditable();
            await JobRepository.ExecAsync(t.Connection, t.Transaction, "DELETE FROM ductwork.SheetView WHERE SheetViewId = @id;", t.Token, ("@id", viewId));
            await t.AuditAtCurrentVersionAsync(new AuditEntry("sheets", "view.deleted", $"Removed the view \"{view.Title}\" from sheet {Label(sheet)}.", "SheetView", view.PublicId.ToString("D"),
                JobRepository.Json(new() { ["title"] = view.Title, ["scale"] = view.ScaleRatio }), null));
            return true;
        });

    /// <summary>
    /// A person states the scale (read off the drawing) or calibrates it (measured against a known length). A calibration
    /// must carry its measurement. A detector or worker may not set one: a wrong scale would silently skew every length.
    /// </summary>
    public Task<SheetViewRecord> SetScaleAsync(long viewId, double ratio, ScaleMethod method, string? calibrationJson, EditToken editToken, AuditContext context,
        CancellationToken cancellationToken = default) =>
        InViewAsync(viewId, context, cancellationToken, editToken, async (t, view, sheet) =>
        {
            t.RequireEditable();
            if (context.ActorType != ActorType.User) throw new JobRuleViolationException("Only a person can set a scale. A detector may suggest one.");
            if (!(ratio > 0) || double.IsInfinity(ratio) || double.IsNaN(ratio)) throw new JobRuleViolationException("A scale must be a positive number.");
            if (method == ScaleMethod.Calibrated)
            {
                if (string.IsNullOrWhiteSpace(calibrationJson)) throw new JobRuleViolationException("A calibrated scale needs the measurement it was calibrated from.");
                try { using var _ = JsonDocument.Parse(calibrationJson); }
                catch (JsonException) { throw new JobRuleViolationException("The calibration measurement is not valid JSON."); }
            }

            await JobRepository.ExecAsync(t.Connection, t.Transaction,
                "UPDATE ductwork.SheetView SET ScaleRatio = @r, ScaleMethod = @m, ScaleCalibrationJson = @c, ScaleSetBy = @by, ScaleSetUtc = SYSUTCDATETIME() WHERE SheetViewId = @id;",
                t.Token, ("@r", ratio), ("@m", method.ToString()), ("@c", method == ScaleMethod.Calibrated ? calibrationJson : null), ("@by", JobRepository.Actor(context)), ("@id", viewId));
            await t.AuditAtCurrentVersionAsync(new AuditEntry("sheets", "view.scale-set", $"Set the scale of \"{view.Title}\" to {ratio.ToString("0.####", CultureInfo.InvariantCulture)} ({method.ToString().ToLowerInvariant()}).",
                "SheetView", view.PublicId.ToString("D"),
                JobRepository.Json(new() { ["scale"] = view.ScaleRatio, ["method"] = view.ScaleMethod?.ToString() }),
                JobRepository.Json(new() { ["scale"] = ratio, ["method"] = method.ToString(), ["calibration"] = calibrationJson })));
            return (await ReadViewsAsync(t.Connection, t.Transaction, "WHERE v.SheetViewId = @id", t.Token, ("@id", viewId))).Single();
        });

    public Task<SheetViewRecord> ClearScaleAsync(long viewId, EditToken editToken, AuditContext context, CancellationToken cancellationToken = default) =>
        InViewAsync(viewId, context, cancellationToken, editToken, async (t, view, sheet) =>
        {
            t.RequireEditable();
            if (view.ScaleRatio is null) return view;
            await JobRepository.ExecAsync(t.Connection, t.Transaction,
                "UPDATE ductwork.SheetView SET ScaleRatio = NULL, ScaleMethod = NULL, ScaleCalibrationJson = NULL, ScaleSetBy = NULL, ScaleSetUtc = NULL WHERE SheetViewId = @id;",
                t.Token, ("@id", viewId));
            await t.AuditAtCurrentVersionAsync(new AuditEntry("sheets", "view.scale-cleared", $"Cleared the scale of \"{view.Title}\".", "SheetView", view.PublicId.ToString("D"),
                JobRepository.Json(new() { ["scale"] = view.ScaleRatio, ["method"] = view.ScaleMethod?.ToString() }), null));
            return (await ReadViewsAsync(t.Connection, t.Transaction, "WHERE v.SheetViewId = @id", t.Token, ("@id", viewId))).Single();
        });

    // ------------------------------------------------------------------ regions

    public Task<SheetRegionRecord> AddRegionAsync(long sheetId, RegionKind kind, string? reason, IReadOnlyList<double[]> polygon, EditToken editToken, AuditContext context,
        CancellationToken cancellationToken = default) =>
        InSheetAsync(sheetId, context, cancellationToken, editToken, async (t, sheet) =>
        {
            t.RequireEditable();
            var why = Clean(reason, 300);
            if (kind == RegionKind.Exclude && why is null) throw new JobRuleViolationException("Leaving an area out needs a reason, so it can be explained later.");

            var id = await ScalarAsync(t,
                "INSERT INTO ductwork.SheetRegion (JobId, SheetId, Kind, Reason, PolygonJson, CreatedBy) OUTPUT INSERTED.SheetRegionId VALUES (@j, @s, @k, @r, @p, @by);",
                ("@j", sheet.JobId), ("@s", sheetId), ("@k", kind.ToString()), ("@r", why), ("@p", PolygonJson(polygon, sheet)), ("@by", JobRepository.Actor(context)));
            var region = (await ReadRegionsAsync(t.Connection, t.Transaction, "WHERE r.SheetRegionId = @id", t.Token, ("@id", id))).Single();
            await t.AuditAtCurrentVersionAsync(new AuditEntry("sheets", "region.added", $"{(kind == RegionKind.Exclude ? "Excluded" : "Marked for inclusion")} an area on sheet {Label(sheet)}.",
                "SheetRegion", region.PublicId.ToString("D"), null, JobRepository.Json(new() { ["kind"] = kind.ToString(), ["reason"] = why })));
            return region;
        });

    public async Task<bool> RemoveRegionAsync(long regionId, EditToken editToken, AuditContext context, CancellationToken cancellationToken = default)
    {
        long sheetId;
        await using (var connection = await _open(cancellationToken))
        {
            var found = (await ReadRegionsAsync(connection, null, "WHERE r.SheetRegionId = @id", cancellationToken, ("@id", regionId))).SingleOrDefault()
                ?? throw new KeyNotFoundException($"Region {regionId} does not exist.");
            sheetId = found.SheetId;
        }

        return await InSheetAsync(sheetId, context, cancellationToken, editToken, async (t, sheet) =>
        {
            t.RequireEditable();
            var region = (await ReadRegionsAsync(t.Connection, t.Transaction, "WHERE r.SheetRegionId = @id", t.Token, ("@id", regionId))).SingleOrDefault();
            if (region is null) return false;
            await JobRepository.ExecAsync(t.Connection, t.Transaction, "DELETE FROM ductwork.SheetRegion WHERE SheetRegionId = @id;", t.Token, ("@id", regionId));
            await t.AuditAtCurrentVersionAsync(new AuditEntry("sheets", "region.removed", $"Removed an area from sheet {Label(sheet)}.", "SheetRegion", region.PublicId.ToString("D"),
                JobRepository.Json(new() { ["kind"] = region.Kind.ToString(), ["reason"] = region.Reason }), null));
            return true;
        });
    }

    // ------------------------------------------------------------------ helpers

    private async Task<T> InSheetAsync<T>(long sheetId, AuditContext context, CancellationToken ct, EditToken editToken, Func<JobTxn, SheetRecord, Task<T>> body)
    {
        var existing = await GetAsync(sheetId, ct) ?? throw new KeyNotFoundException($"Sheet {sheetId} does not exist.");
        return await JobTxn.RunAsync(_open, existing.JobId, context, ct, async t =>
        {
            var current = (await ListAsync(t.Connection, t.Transaction, "WHERE s.SheetId = @id", t.Token, ("@id", sheetId))).SingleOrDefault()
                ?? throw new KeyNotFoundException($"Sheet {sheetId} no longer exists.");
            return await body(t, current);
        }, editToken, requires: JobCapability.Edit);
    }

    private async Task<T> InViewAsync<T>(long viewId, AuditContext context, CancellationToken ct, EditToken editToken, Func<JobTxn, SheetViewRecord, SheetRecord, Task<T>> body)
    {
        await using var connection = await _open(ct);
        var found = (await ReadViewsAsync(connection, null, "WHERE v.SheetViewId = @id", ct, ("@id", viewId))).SingleOrDefault()
            ?? throw new KeyNotFoundException($"View {viewId} does not exist.");
        return await JobTxn.RunAsync(_open, found.JobId, context, ct, async t =>
        {
            var view = (await ReadViewsAsync(t.Connection, t.Transaction, "WHERE v.SheetViewId = @id", t.Token, ("@id", viewId))).SingleOrDefault()
                ?? throw new KeyNotFoundException($"View {viewId} no longer exists.");
            var sheet = (await ListAsync(t.Connection, t.Transaction, "WHERE s.SheetId = @id", t.Token, ("@id", view.SheetId))).Single();
            return await body(t, view, sheet);
        }, editToken, requires: JobCapability.Edit);
    }

    private static string Label(SheetRecord s) => string.IsNullOrWhiteSpace(s.SheetNumber) ? $"page {s.PageNo}" : s.SheetNumber!;

    private static string? Clean(string? text, int max)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }

    /// <summary>A polygon is three or more finite points inside the page, stored as <c>[[x,y],...]</c>.</summary>
    internal static string PolygonJson(IReadOnlyList<double[]> points, SheetRecord sheet)
    {
        if (points.Count < 3) throw new JobRuleViolationException("An area needs at least three points.");
        if (points.Count > MaxPolygonPoints) throw new JobRuleViolationException($"An area may have at most {MaxPolygonPoints} points.");
        foreach (var p in points)
        {
            if (p is null || p.Length != 2) throw new JobRuleViolationException("Each point must be an x and a y.");
            if (!double.IsFinite(p[0]) || !double.IsFinite(p[1])) throw new JobRuleViolationException("A point is not a number.");
            if (p[0] < 0 || p[1] < 0 || p[0] > sheet.WidthPts || p[1] > sheet.HeightPts)
                throw new JobRuleViolationException($"A point ({p[0]:0.##}, {p[1]:0.##}) is outside the page ({sheet.WidthPts:0.##} by {sheet.HeightPts:0.##}).");
        }

        return JsonSerializer.Serialize(points);
    }

    private static async Task RequireBuildingTypeAsync(JobTxn t, long buildingTypeId)
    {
        var jobOfType = await ScalarOrNullAsync(t, "SELECT JobId FROM ductwork.BuildingType WHERE BuildingTypeId = @b;", ("@b", buildingTypeId));
        if (jobOfType != t.Job.JobId) throw new JobRuleViolationException("That building type is not on this job.");
    }

    private sealed record FileRow(long SourceFileId, long RevisionId, string OriginalName, bool IsDuplicate);

    private static async Task<FileRow?> ReadFileRowAsync(JobTxn t, Guid publicId)
    {
        await using var command = t.Connection.CreateCommand();
        command.Transaction = t.Transaction;
        command.CommandText = "SELECT SourceFileId, RevisionId, OriginalName, CASE WHEN StorageKey IS NULL THEN 1 ELSE 0 END FROM ductwork.SourceFile WHERE PublicId = @p AND JobId = @j;";
        command.Parameters.AddWithValue("@p", publicId);
        command.Parameters.AddWithValue("@j", t.Job.JobId);
        await using var r = await command.ExecuteReaderAsync(t.Token);
        return await r.ReadAsync(t.Token) ? new FileRow(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt32(3) == 1) : null;
    }

    private static async Task<long?> ScalarOrNullAsync(JobTxn t, string sql, params (string Name, object? Value)[] args)
    {
        await using var command = t.Connection.CreateCommand();
        command.Transaction = t.Transaction;
        command.CommandText = sql;
        foreach (var (name, value) in args) JobRepository.P(command, name, value);
        var result = await command.ExecuteScalarAsync(t.Token);
        return result is null or DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<long> ScalarAsync(JobTxn t, string sql, params (string Name, object? Value)[] args) =>
        await ScalarOrNullAsync(t, sql, args) ?? throw new InvalidOperationException("The statement returned nothing.");

    private static async Task<List<T>> ScalarListAsync<T>(JobTxn t, string sql, params (string Name, object? Value)[] args)
    {
        await using var command = t.Connection.CreateCommand();
        command.Transaction = t.Transaction;
        command.CommandText = sql;
        foreach (var (name, value) in args) JobRepository.P(command, name, value);
        var list = new List<T>();
        await using var r = await command.ExecuteReaderAsync(t.Token);
        while (await r.ReadAsync(t.Token)) list.Add((T)Convert.ChangeType(r.GetValue(0), typeof(T), CultureInfo.InvariantCulture));
        return list;
    }

    private static async Task<List<SheetRecord>> ListAsync(SqlConnection c, SqlTransaction? tx, string where, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        var sheets = new List<SheetRecord>();
        await using (var command = c.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText =
                """
                SELECT s.SheetId, s.PublicId, s.JobId, s.RevisionId, s.SourceFileId, f.PublicId, s.PageNo, s.SheetNumber, s.Title, s.Discipline, s.SheetKind, s.ContentHash,
                       d.PublicId, s.HasTextLayer, s.WidthPts, s.HeightPts, s.Rotation, s.InScope, s.ProcessingTaskId, s.CreatedBy, s.CreatedUtc
                FROM ductwork.Sheet s
                JOIN ductwork.SourceFile f ON f.SourceFileId = s.SourceFileId
                LEFT JOIN ductwork.Sheet d ON d.SheetId = s.DuplicateOfSheetId
                """ + " " + where + (where.Contains("ORDER BY") ? "" : " ORDER BY s.SourceFileId, s.PageNo");
            foreach (var (name, value) in args) JobRepository.P(command, name, value);
            await using var r = await command.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                sheets.Add(new SheetRecord
                {
                    SheetId = r.GetInt64(0), PublicId = r.GetGuid(1), JobId = r.GetInt64(2), RevisionId = r.GetInt64(3), SourceFileId = r.GetInt64(4), SourceFilePublicId = r.GetGuid(5),
                    PageNo = r.GetInt32(6), SheetNumber = r.IsDBNull(7) ? null : r.GetString(7), Title = r.IsDBNull(8) ? null : r.GetString(8),
                    Discipline = r.IsDBNull(9) ? null : r.GetString(9), Kind = Enum.Parse<SheetKind>(r.GetString(10)),
                    ContentHash = r.IsDBNull(11) ? null : Convert.ToHexString((byte[])r[11]).ToLowerInvariant(),
                    DuplicateOfPublicId = r.IsDBNull(12) ? null : r.GetGuid(12), HasTextLayer = r.GetBoolean(13), WidthPts = r.GetDouble(14), HeightPts = r.GetDouble(15),
                    Rotation = r.GetInt32(16), InScope = r.GetBoolean(17), ProcessingTaskId = r.IsDBNull(18) ? null : r.GetInt64(18), CreatedBy = r.GetString(19), CreatedUtc = r.GetDateTime(20)
                });
        }

        if (sheets.Count == 0) return sheets;

        var jobIds = sheets.Select(s => s.JobId).Distinct().ToList();
        var views = new List<SheetViewRecord>();
        var regions = new List<SheetRegionRecord>();
        foreach (var jobId in jobIds)
        {
            views.AddRange(await ReadViewsAsync(c, tx, "WHERE v.JobId = @jv", ct, ("@jv", jobId)));
            regions.AddRange(await ReadRegionsAsync(c, tx, "WHERE r.JobId = @jr", ct, ("@jr", jobId)));
        }

        var viewsBySheet = views.ToLookup(v => v.SheetId);
        var regionsBySheet = regions.ToLookup(r => r.SheetId);
        return sheets.Select(s => s with { Views = viewsBySheet[s.SheetId].OrderBy(v => v.SheetViewId).ToList(), Regions = regionsBySheet[s.SheetId].OrderBy(r => r.SheetRegionId).ToList() }).ToList();
    }

    private static async Task<List<SheetViewRecord>> ReadViewsAsync(SqlConnection c, SqlTransaction? tx, string where, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            """
            SELECT v.SheetViewId, v.PublicId, v.JobId, v.SheetId, v.Title, v.RegionPolygonJson, v.ScaleRatio, v.ScaleMethod, v.ScaleCalibrationJson, v.ScaleSetBy, v.ScaleSetUtc,
                   v.Floor, v.ViewType, v.ExtractionMethod, v.BuildingTypeId, v.CreatedBy, v.CreatedUtc
            FROM ductwork.SheetView v
            """ + " " + where;
        foreach (var (name, value) in args) JobRepository.P(command, name, value);
        var list = new List<SheetViewRecord>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new SheetViewRecord
            {
                SheetViewId = r.GetInt64(0), PublicId = r.GetGuid(1), JobId = r.GetInt64(2), SheetId = r.GetInt64(3), Title = r.GetString(4), PolygonJson = r.IsDBNull(5) ? null : r.GetString(5),
                ScaleRatio = r.IsDBNull(6) ? null : r.GetDouble(6), ScaleMethod = r.IsDBNull(7) ? null : Enum.Parse<ScaleMethod>(r.GetString(7)),
                ScaleCalibrationJson = r.IsDBNull(8) ? null : r.GetString(8), ScaleSetBy = r.IsDBNull(9) ? null : r.GetString(9), ScaleSetUtc = r.IsDBNull(10) ? null : r.GetDateTime(10),
                Floor = r.IsDBNull(11) ? null : r.GetString(11), Type = Enum.Parse<ViewType>(r.GetString(12)), ExtractionMethod = Enum.Parse<ViewExtraction>(r.GetString(13)),
                BuildingTypeId = r.IsDBNull(14) ? null : r.GetInt64(14), CreatedBy = r.GetString(15), CreatedUtc = r.GetDateTime(16)
            });
        return list;
    }

    private static async Task<List<SheetRegionRecord>> ReadRegionsAsync(SqlConnection c, SqlTransaction? tx, string where, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            "SELECT r.SheetRegionId, r.PublicId, r.JobId, r.SheetId, r.Kind, r.Reason, r.PolygonJson, r.CreatedBy, r.CreatedUtc FROM ductwork.SheetRegion r " + where;
        foreach (var (name, value) in args) JobRepository.P(command, name, value);
        var list = new List<SheetRegionRecord>();
        await using var r2 = await command.ExecuteReaderAsync(ct);
        while (await r2.ReadAsync(ct))
            list.Add(new SheetRegionRecord
            {
                SheetRegionId = r2.GetInt64(0), PublicId = r2.GetGuid(1), JobId = r2.GetInt64(2), SheetId = r2.GetInt64(3), Kind = Enum.Parse<RegionKind>(r2.GetString(4)),
                Reason = r2.IsDBNull(5) ? null : r2.GetString(5), PolygonJson = r2.GetString(6), CreatedBy = r2.GetString(7), CreatedUtc = r2.GetDateTime(8)
            });
        return list;
    }
}
