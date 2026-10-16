using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Counting;
using AIToolbox.Ductwork.Core.Drawings;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Web.Ductwork;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers.Ductwork;

/// <summary>
/// Reviewing what the detector found: verify or reject, decide what to do about an item, correct it, add what was
/// missed, and undo. Items are addressed by public id inside a job. Every change needs the edit lock and its fencing
/// token and says which revision of the item it was made against, so two people cannot silently overwrite each other.
/// </summary>
[Route("ductwork/api/jobs/{jobId:long}")]
public sealed class DuctworkReviewController(JobRepository jobs, EditLockService locks, EntityReviewRepository review, SheetRepository sheets) : DuctworkUserApiController
{
    [HttpGet("entities/{entityId:guid}")]
    public async Task<IActionResult> Get(long jobId, Guid entityId, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        return Json(await review.GetByPublicIdAsync(jobId, entityId, ct) ?? throw new KeyNotFoundException("That item does not exist."));
    }

    [HttpGet("entities/{entityId:guid}/history")]
    public async Task<IActionResult> History(long jobId, Guid entityId, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var item = await review.GetByPublicIdAsync(jobId, entityId, ct) ?? throw new KeyNotFoundException("That item does not exist.");
        return Json(await review.ListRevisionsAsync(item.EntityId, ct));
    }

    [HttpPut("entities/{entityId:guid}/review")]
    public async Task<IActionResult> SetReview(long jobId, Guid entityId, [FromBody] SetReviewRequest request, CancellationToken ct)
    {
        var (audit, token, item) = await EditAsync(jobId, entityId, ct);
        var status = DuctworkParse.Enum<ReviewStatus>(request.Status, "review status");
        return Json(await review.SetReviewStatusAsync(item.EntityId, status, request.ExpectedRevision, request.Reason, token, audit, ct));
    }

    /// <summary>Verifies, rejects or reopens many items at once. Items already in that state are left alone, and one that is not on this job fails the whole request.</summary>
    [HttpPost("entities/review")]
    public async Task<IActionResult> SetReviewMany(long jobId, [FromBody] ReviewManyRequest request, CancellationToken ct)
    {
        var audit = Audit();
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var status = DuctworkParse.Enum<ReviewStatus>(request.Status, "review status");
        var wanted = request.EntityIds ?? [];
        var ids = await review.GetIdsAsync(jobId, wanted, ct);
        if (wanted.Distinct().Any(g => !ids.ContainsKey(g))) throw new KeyNotFoundException("One or more of those items are not on this job.");

        var token = await locks.ResolveTokenAsync(jobId, FencingToken(), audit, ct);
        var changed = await review.SetReviewStatusManyAsync(jobId, ids.Values.ToList(), status, request.Reason, token, audit, ct);
        return Json(new { changed, requested = ids.Count });
    }

    [HttpPut("entities/{entityId:guid}/disposition")]
    public async Task<IActionResult> SetDisposition(long jobId, Guid entityId, [FromBody] SetDispositionRequest request, CancellationToken ct)
    {
        var (audit, token, item) = await EditAsync(jobId, entityId, ct);
        var disposition = DuctworkParse.Enum<Disposition>(request.Disposition, "disposition");
        return Json(await review.SetDispositionAsync(item.EntityId, disposition, request.Reason, request.ExpectedRevision, token, audit, ct));
    }

    [HttpPatch("entities/{entityId:guid}")]
    public async Task<IActionResult> Edit(long jobId, Guid entityId, [FromBody] EditEntityRequest request, CancellationToken ct)
    {
        var (audit, token, item) = await EditAsync(jobId, entityId, ct);
        var edit = new EntityEdit
        {
            Type = request.Type, Category = request.Category, Tag = request.Tag, Size = request.Size, Manufacturer = request.Manufacturer, Model = request.Model,
            CustomerPartCode = request.CustomerPartCode, Quantity = request.Quantity, Unit = DuctworkParse.OptionalEnum<QuantityUnit>(request.Unit, "unit"),
            BuildingTypeId = request.BuildingTypeId, ClearBuildingType = request.ClearBuildingType, SheetViewId = await ViewIdAsync(jobId, request.ViewId, ct), ClearSheetView = request.ClearSheetView
        };
        return Json(await review.EditAsync(item.EntityId, edit, request.ExpectedRevision, request.Reason, token, audit, ct));
    }

    /// <summary>A person adds an item the detector missed.</summary>
    [HttpPost("entities")]
    public async Task<IActionResult> Add(long jobId, [FromBody] AddEntityRequest request, CancellationToken ct)
    {
        var audit = Audit();
        var token = await locks.ResolveTokenAsync(jobId, FencingToken(), audit, ct);
        if (request.Box is { Length: not 4 }) throw new DuctworkBadRequestException("A box is four numbers: x0, y0, x1, y1.");

        var item = new NewManualEntity
        {
            Type = request.Type ?? "", Category = request.Category, Tag = request.Tag, Size = request.Size, Manufacturer = request.Manufacturer, Model = request.Model,
            CustomerPartCode = request.CustomerPartCode, Quantity = request.Quantity ?? 1, Unit = DuctworkParse.OptionalEnum<QuantityUnit>(request.Unit, "unit") ?? QuantityUnit.EA,
            BuildingTypeId = request.BuildingTypeId, SheetViewId = await ViewIdAsync(jobId, request.ViewId, ct),
            Disposition = DuctworkParse.OptionalEnum<Disposition>(request.Disposition, "disposition") ?? Disposition.Included, DispositionReason = request.DispositionReason,
            Box = request.Box is { Length: 4 } b ? new BoundingBox(b[0], b[1], b[2], b[3]) : null, Evidence = request.Evidence
        };
        return Json(await review.AddAsync(jobId, item, token, audit, ct), 201);
    }

    [HttpPost("entities/{entityId:guid}/undo")]
    public async Task<IActionResult> Undo(long jobId, Guid entityId, CancellationToken ct)
    {
        var (audit, token, item) = await EditAsync(jobId, entityId, ct);
        return Json(await review.UndoAsync(item.EntityId, token, audit, ct));
    }

    /// <summary>Undoes the last thing the caller changed on this job.</summary>
    [HttpPost("undo")]
    public async Task<IActionResult> UndoLast(long jobId, CancellationToken ct)
    {
        var audit = Audit();
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var token = await locks.ResolveTokenAsync(jobId, FencingToken(), audit, ct);
        return Json(await review.UndoLastAsync(jobId, token, audit, ct));
    }

    /// <summary>A view is named by its public id inside the job. One from another job is simply not found.</summary>
    private async Task<long?> ViewIdAsync(long jobId, Guid? publicId, CancellationToken ct) =>
        publicId is { } id ? (await sheets.GetViewAsync(jobId, id, ct) ?? throw new KeyNotFoundException("That view does not exist.")).SheetViewId : null;

    private async Task<(AuditContext Audit, EditToken Token, EntityRecord Item)> EditAsync(long jobId, Guid entityId, CancellationToken ct)
    {
        var audit = Audit();
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var item = await review.GetByPublicIdAsync(jobId, entityId, ct) ?? throw new KeyNotFoundException("That item does not exist.");
        var token = await locks.ResolveTokenAsync(jobId, FencingToken(), audit, ct);
        return (audit, token, item);
    }
}
