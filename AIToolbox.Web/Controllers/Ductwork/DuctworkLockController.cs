using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Web.Ductwork;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers.Ductwork;

/// <summary>
/// The single-editor lock (plan section 9). Only one browser tab edits a job at a time. The tab that holds
/// the lock receives a fencing token and sends it with every edit; the lock moves between tabs by
/// expiry, by an answered takeover request, by timeout, or by an owner or administrator forcing it.
/// </summary>
[Route("ductwork/api/jobs/{jobId:long}/lock")]
public sealed class DuctworkLockController(JobRepository jobs, EditLockService locks) : DuctworkUserApiController
{
    /// <summary>Who holds the lock, any waiting takeover request, and, for the holder's own tab, the fencing token. Also grants a request whose time ran out.</summary>
    [HttpGet]
    public async Task<IActionResult> Status(long jobId, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var tab = DuctworkAuditContexts.SessionFor(Email, DuctworkAuditContexts.TabId(HttpContext));

        // An unanswered request is granted when its time is up. Whoever looks first does it, as the system.
        await locks.AdvanceAsync(jobId, AuditContext.ForSystem("takeover timeout"), ct);
        return Json(StatusView(await locks.GetStatusAsync(jobId, tab, ct)));
    }

    [HttpPost]
    public async Task<IActionResult> Acquire(long jobId, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var result = await locks.AcquireAsync(jobId, Audit(), ct);
        return Json(new
        {
            outcome = result.Outcome,
            fencingToken = result.Token?.FencingToken,
            holder = result.Holder is null ? null : Holder(result.Holder)
        }, result.Outcome == LockOutcome.HeldByOther ? 409 : 200);
    }

    [HttpDelete]
    public async Task<IActionResult> Release(long jobId, CancellationToken ct)
    {
        var token = await HeldTokenOrNullAsync(jobId, ct);
        return Json(new { released = token is not null && await locks.ReleaseAsync(token, Audit(), ct) });
    }

    /// <summary>Sent every few seconds by the editing tab. Tells it whether it still holds the lock and whether someone is asking for it.</summary>
    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat(long jobId, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var token = await HeldTokenOrNullAsync(jobId, ct);
        if (token is null) return Json(new { stillHolder = false, message = "You no longer hold the editing lock.", pendingRequest = (object?)null });

        var beat = await locks.HeartbeatAsync(token, ct);
        return Json(new { stillHolder = beat.StillHolder, message = beat.LostMessage, pendingRequest = beat.PendingRequest is null ? null : RequestView(beat.PendingRequest) });
    }

    /// <summary>Asks to take over editing. A free or expired lock is taken at once; a live one is asked.</summary>
    [HttpPost("takeover")]
    public async Task<IActionResult> RequestTakeover(long jobId, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var result = await locks.RequestTakeoverAsync(jobId, Audit(), ct);
        return Json(new
        {
            granted = result.Granted, message = result.Message, fencingToken = result.Token?.FencingToken,
            request = result.Request is null ? null : RequestView(result.Request),
            holder = result.Holder is null ? null : Holder(result.Holder)
        });
    }

    [HttpDelete("takeover/{requestId:long}")]
    public async Task<IActionResult> CancelTakeover(long jobId, long requestId, CancellationToken ct) =>
        Json(new { cancelled = await locks.CancelTakeoverAsync(jobId, requestId, Audit(), ct) });

    /// <summary>The current editor answers: hand over now, or keep editing.</summary>
    [HttpPost("takeover/{requestId:long}/respond")]
    public async Task<IActionResult> Respond(long jobId, long requestId, [FromBody] TakeoverResponseRequest body, CancellationToken ct)
    {
        var audit = Audit();
        var token = await locks.ResolveTokenAsync(jobId, FencingToken(), audit, ct);
        var result = await locks.RespondToTakeoverAsync(token, requestId, body.HandOver, audit, ct);
        return Json(new { handedOver = result.Granted, message = result.Message });
    }

    /// <summary>An owner or an administrator takes the lock regardless of who holds it.</summary>
    [HttpPost("force")]
    public async Task<IActionResult> Force(long jobId, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var token = await locks.ForceTakeoverAsync(jobId, Audit(), ct);
        return Json(new { fencingToken = token?.FencingToken, freed = token is null });
    }

    // ------------------------------------------------------------------ views

    /// <summary>
    /// The token this tab holds, if it still holds the lock under the fencing token it sent. A read, so a stale
    /// tab that keeps heartbeating until it notices does not write an audit event every few seconds.
    /// </summary>
    private async Task<EditToken?> HeldTokenOrNullAsync(long jobId, CancellationToken ct)
    {
        var status = await locks.GetStatusAsync(jobId, DuctworkAuditContexts.SessionFor(Email, DuctworkAuditContexts.TabId(HttpContext)), ct);
        return status.YourToken is { } mine && mine.FencingToken == FencingToken() ? mine : null;
    }

    // The tab's own token is shown to that tab only. Other people see who is editing, never anyone's token or session.
    private static object StatusView(LockStatus status) => new
    {
        holder = status.Lock is null ? null : Holder(status.Lock),
        youHoldIt = status.YouHoldIt,
        fencingToken = status.YourToken?.FencingToken,
        pendingRequest = status.PendingRequest is null ? null : RequestView(status.PendingRequest)
    };

    private static object Holder(EditLockInfo lockInfo) => new
    {
        email = lockInfo.HolderEmail, name = lockInfo.HolderDisplayName, acquiredUtc = lockInfo.AcquiredUtc,
        lastHeartbeatUtc = lockInfo.LastHeartbeatUtc, expiresUtc = lockInfo.ExpiresUtc, isExpired = lockInfo.IsExpired
    };

    private static object RequestView(PreemptRequestRecord r) => new
    {
        id = r.PreemptRequestId, requesterEmail = r.RequesterEmail, requesterName = r.RequesterDisplayName, requestedUtc = r.RequestedUtc,
        expiresUtc = r.ExpiresUtc, status = r.Status
    };
}
