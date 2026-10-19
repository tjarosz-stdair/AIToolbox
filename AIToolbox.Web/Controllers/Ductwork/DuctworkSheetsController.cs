using System.Text.Json;
using AIToolbox.Ductwork.Core.Drawings;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Web.Ductwork;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers.Ductwork;

/// <summary>
/// Sheets, the views on them, and include/exclude regions. Pages and views are addressed by their public ids inside
/// a job, so an id from another job is simply not found. Every change needs the edit lock and its fencing token.
/// </summary>
[Route("ductwork/api/jobs/{jobId:long}")]
public sealed class DuctworkSheetsController(JobRepository jobs, EditLockService locks, SheetRepository sheets) : DuctworkUserApiController
{
    [HttpGet("sheets")]
    public async Task<IActionResult> List(long jobId, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        return Json((await sheets.ListByJobAsync(jobId, ct)).Select(SheetOf));
    }

    /// <summary>A person records the pages of an uploaded file. The worker does the same for a task it holds, through its own API.</summary>
    [HttpPost("files/{fileId:guid}/sheets")]
    public async Task<IActionResult> Register(long jobId, Guid fileId, [FromBody] RegisterSheetsRequest request, CancellationToken ct)
    {
        var audit = Audit();
        var token = await locks.ResolveTokenAsync(jobId, FencingToken(), audit, ct);
        var created = await sheets.RegisterAsync(jobId, fileId, ToNew(request.Sheets), token, audit, ct);
        return Json(created.Select(SheetOf), 201);
    }

    [HttpPatch("sheets/{sheetId:guid}")]
    public async Task<IActionResult> Update(long jobId, Guid sheetId, [FromBody] UpdateSheetRequest request, CancellationToken ct)
    {
        var (audit, token, sheet) = await EditAsync(jobId, sheetId, ct);
        var edit = new SheetEdit
        {
            SheetNumber = request.SheetNumber, Title = request.Title, Discipline = request.Discipline,
            Kind = DuctworkParse.OptionalEnum<SheetKind>(request.Kind, "sheet kind"), InScope = request.InScope
        };
        return Json(SheetOf(await sheets.UpdateSheetAsync(sheet.SheetId, edit, token, audit, ct)));
    }

    // ------------------------------------------------------------------ views

    [HttpPost("sheets/{sheetId:guid}/views")]
    public async Task<IActionResult> CreateView(long jobId, Guid sheetId, [FromBody] CreateViewRequest request, CancellationToken ct)
    {
        var (audit, token, sheet) = await EditAsync(jobId, sheetId, ct);
        var view = new NewView
        {
            Title = request.Title ?? "", Floor = request.Floor, BuildingTypeId = request.BuildingTypeId, Polygon = request.Polygon,
            Type = DuctworkParse.OptionalEnum<ViewType>(request.Type, "view type") ?? ViewType.Plan
        };
        return Json(ViewOf(await sheets.CreateViewAsync(sheet.SheetId, view, token, audit, ct)), 201);
    }

    [HttpPatch("views/{viewId:guid}")]
    public async Task<IActionResult> UpdateView(long jobId, Guid viewId, [FromBody] UpdateViewRequest request, CancellationToken ct)
    {
        var (audit, token, view) = await EditViewAsync(jobId, viewId, ct);
        var edit = new ViewEdit
        {
            Title = request.Title, Floor = request.Floor, Type = DuctworkParse.OptionalEnum<ViewType>(request.Type, "view type"), BuildingTypeId = request.BuildingTypeId,
            ClearBuildingType = request.ClearBuildingType, Polygon = request.Polygon, ClearPolygon = request.ClearPolygon
        };
        return Json(ViewOf(await sheets.UpdateViewAsync(view.SheetViewId, edit, token, audit, ct)));
    }

    [HttpDelete("views/{viewId:guid}")]
    public async Task<IActionResult> DeleteView(long jobId, Guid viewId, CancellationToken ct)
    {
        var (audit, token, view) = await EditViewAsync(jobId, viewId, ct);
        return Json(new { deleted = await sheets.DeleteViewAsync(view.SheetViewId, token, audit, ct) });
    }

    /// <summary>A person states or calibrates the scale of a view. A calibration carries the measurement it came from.</summary>
    [HttpPut("views/{viewId:guid}/scale")]
    public async Task<IActionResult> SetScale(long jobId, Guid viewId, [FromBody] SetScaleRequest request, CancellationToken ct)
    {
        var (audit, token, view) = await EditViewAsync(jobId, viewId, ct);
        var method = DuctworkParse.Enum<ScaleMethod>(request.Method, "scale method");
        var calibration = request.Calibration is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } c ? c.GetRawText() : null;
        return Json(ViewOf(await sheets.SetScaleAsync(view.SheetViewId, request.Ratio, method, calibration, token, audit, ct)));
    }

    [HttpDelete("views/{viewId:guid}/scale")]
    public async Task<IActionResult> ClearScale(long jobId, Guid viewId, CancellationToken ct)
    {
        var (audit, token, view) = await EditViewAsync(jobId, viewId, ct);
        return Json(ViewOf(await sheets.ClearScaleAsync(view.SheetViewId, token, audit, ct)));
    }

    // ------------------------------------------------------------------ regions

    [HttpPost("sheets/{sheetId:guid}/regions")]
    public async Task<IActionResult> AddRegion(long jobId, Guid sheetId, [FromBody] AddRegionRequest request, CancellationToken ct)
    {
        var (audit, token, sheet) = await EditAsync(jobId, sheetId, ct);
        var kind = DuctworkParse.Enum<RegionKind>(request.Kind, "region kind");
        return Json(RegionOf(await sheets.AddRegionAsync(sheet.SheetId, kind, request.Reason, request.Polygon ?? [], token, audit, ct)), 201);
    }

    [HttpDelete("regions/{regionId:guid}")]
    public async Task<IActionResult> RemoveRegion(long jobId, Guid regionId, CancellationToken ct)
    {
        var audit = Audit();
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var region = await sheets.GetRegionAsync(jobId, regionId, ct) ?? throw new KeyNotFoundException("That region does not exist.");
        var token = await locks.ResolveTokenAsync(jobId, FencingToken(), audit, ct);
        return Json(new { removed = await sheets.RemoveRegionAsync(region.SheetRegionId, token, audit, ct) });
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(AuditContext Audit, EditToken Token, SheetRecord Sheet)> EditAsync(long jobId, Guid sheetId, CancellationToken ct)
    {
        var audit = Audit();
        // A stranger learns nothing: the lookup is by job, so a sheet of another job is not found.
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var sheet = await sheets.GetByPublicIdAsync(jobId, sheetId, ct) ?? throw new KeyNotFoundException("That sheet does not exist.");
        var token = await locks.ResolveTokenAsync(jobId, FencingToken(), audit, ct);
        return (audit, token, sheet);
    }

    private async Task<(AuditContext Audit, EditToken Token, SheetViewRecord View)> EditViewAsync(long jobId, Guid viewId, CancellationToken ct)
    {
        var audit = Audit();
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var view = await sheets.GetViewAsync(jobId, viewId, ct) ?? throw new KeyNotFoundException("That view does not exist.");
        var token = await locks.ResolveTokenAsync(jobId, FencingToken(), audit, ct);
        return (audit, token, view);
    }

    internal static IReadOnlyList<NewSheet> ToNew(SheetBody[]? bodies) =>
        (bodies ?? []).Select(b => new NewSheet
        {
            PageNo = b.PageNo, WidthPts = b.WidthPts, HeightPts = b.HeightPts, Rotation = b.Rotation, SheetNumber = b.SheetNumber, Title = b.Title, Discipline = b.Discipline,
            Kind = DuctworkParse.OptionalEnum<SheetKind>(b.Kind, "sheet kind") ?? SheetKind.Other, ContentHashHex = b.ContentHash, HasTextLayer = b.HasTextLayer ?? true,
            Views = (b.Views ?? []).Select(v => new NewDetectedView
            {
                // A view's number is part of how a person refers to it ("detail 3"), so it leads the title.
                Title = string.IsNullOrWhiteSpace(v.Number) ? v.Title ?? "" : $"{v.Number.Trim()}. {v.Title}".Trim(),
                Type = DuctworkParse.OptionalEnum<ViewType>(v.Type, "view type") ?? ViewType.Plan, ScaleRatio = v.ScaleRatio, Polygon = v.Polygon
            }).ToList()
        }).ToList();

    private static JsonElement? Parse(string? json) => json is null ? null : JsonDocument.Parse(json).RootElement.Clone();

    private static object SheetOf(SheetRecord s) => new
    {
        sheetId = s.PublicId, fileId = s.SourceFilePublicId, pageNo = s.PageNo, sheetNumber = s.SheetNumber, title = s.Title, discipline = s.Discipline, kind = s.Kind,
        widthPts = s.WidthPts, heightPts = s.HeightPts, rotation = s.Rotation, hasTextLayer = s.HasTextLayer, inScope = s.InScope, contentHash = s.ContentHash,
        duplicateOf = s.DuplicateOfPublicId, views = s.Views.Select(ViewOf), regions = s.Regions.Select(RegionOf)
    };

    private static object ViewOf(SheetViewRecord v) => new
    {
        viewId = v.PublicId, title = v.Title, floor = v.Floor, type = v.Type, buildingTypeId = v.BuildingTypeId, polygon = Parse(v.PolygonJson),
        scaleRatio = v.ScaleRatio, scaleMethod = v.ScaleMethod, scaleCalibration = Parse(v.ScaleCalibrationJson), scaleSetBy = v.ScaleSetBy, scaleSetUtc = v.ScaleSetUtc,
        extractionMethod = v.ExtractionMethod
    };

    private static object RegionOf(SheetRegionRecord r) => new { regionId = r.PublicId, kind = r.Kind, reason = r.Reason, polygon = Parse(r.PolygonJson) };
}
