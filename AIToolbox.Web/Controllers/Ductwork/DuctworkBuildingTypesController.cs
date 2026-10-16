using AIToolbox.Ductwork.Core.Counting;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Web.Ductwork;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers.Ductwork;

/// <summary>
/// Building types and their multipliers. Entering a multiplier here confirms it, because it is a person's
/// own statement. A detector's value arrives through the worker and is only ever a suggestion.
/// </summary>
[Route("ductwork/api/jobs/{jobId:long}/building-types")]
public sealed class DuctworkBuildingTypesController(JobRepository jobs, EditLockService locks, BuildingTypeRepository types) : DuctworkUserApiController
{
    [HttpGet]
    public async Task<IActionResult> List(long jobId, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        return Json(await types.ListByJobAsync(jobId, ct));
    }

    [HttpPost]
    public async Task<IActionResult> Create(long jobId, [FromBody] CreateBuildingTypeRequest request, CancellationToken ct)
    {
        var audit = Audit();
        var token = await locks.ResolveTokenAsync(jobId, FencingToken(), audit, ct);
        var created = await types.CreateAsync(jobId, new NewBuildingType
        {
            Name = request.Name ?? "", Kind = DuctworkParse.OptionalEnum<BuildingKind>(request.Kind, "kind") ?? BuildingKind.Other, Notes = request.Notes
        }, token, audit, ct);
        return Json(created, 201);
    }

    [HttpPut("{typeId:long}")]
    public async Task<IActionResult> Update(long jobId, long typeId, [FromBody] UpdateBuildingTypeRequest request, CancellationToken ct)
    {
        await RequireOwnedAsync(jobId, typeId, ct);
        var audit = Audit();
        var token = await locks.ResolveTokenAsync(jobId, FencingToken(), audit, ct);
        return Json(await types.UpdateAsync(typeId, request.Name, DuctworkParse.OptionalEnum<BuildingKind>(request.Kind, "kind"), request.Notes, token, audit, ct));
    }

    /// <summary>A person states how many identical buildings or units there are. This confirms it.</summary>
    [HttpPut("{typeId:long}/multiplier")]
    public async Task<IActionResult> SetMultiplier(long jobId, long typeId, [FromBody] SetMultiplierRequest request, CancellationToken ct)
    {
        await RequireOwnedAsync(jobId, typeId, ct);
        if (request.Multiplier is null) throw new DuctworkBadRequestException("Send the multiplier as a whole number, zero or more.");
        var audit = Audit();
        var token = await locks.ResolveTokenAsync(jobId, FencingToken(), audit, ct);
        var source = DuctworkParse.Enum<MultiplierSource>(request.Source ?? nameof(MultiplierSource.Manual), "source");
        return Json(await types.SetMultiplierAsync(typeId, request.Multiplier.Value, source, request.Evidence, token, audit, ct));
    }

    [HttpDelete("{typeId:long}/multiplier")]
    public async Task<IActionResult> ClearMultiplier(long jobId, long typeId, CancellationToken ct)
    {
        await RequireOwnedAsync(jobId, typeId, ct);
        var audit = Audit();
        var token = await locks.ResolveTokenAsync(jobId, FencingToken(), audit, ct);
        return Json(await types.ClearMultiplierAsync(typeId, token, audit, ct));
    }

    [HttpDelete("{typeId:long}")]
    public async Task<IActionResult> Delete(long jobId, long typeId, CancellationToken ct)
    {
        await RequireOwnedAsync(jobId, typeId, ct);
        var audit = Audit();
        var token = await locks.ResolveTokenAsync(jobId, FencingToken(), audit, ct);
        return Json(new { deleted = await types.DeleteAsync(typeId, token, audit, ct) });
    }

    /// <summary>The building type must belong to the job in the URL, and the caller must be on that job.</summary>
    private async Task RequireOwnedAsync(long jobId, long typeId, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var type = await types.GetAsync(typeId, ct);
        if (type is null || type.JobId != jobId) throw new KeyNotFoundException($"Building type {typeId} does not exist on this job.");
    }
}
