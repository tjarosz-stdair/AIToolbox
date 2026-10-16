using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Counting;
using AIToolbox.Ductwork.Core.Data;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Tasks;
using AIToolbox.Web.Ductwork;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace AIToolbox.Web.Controllers.Ductwork;

/// <summary>
/// The only surface the central worker has (plan sections 6.5 and 6.9). The worker holds no database access and
/// no provider keys: it asks for work, reports progress, and hands back results here, and the web app is the
/// single write path that validates and stores them. Authenticated by an API key that belongs to this
/// instance; the worker's identity is that key plus the name it gives itself.
/// </summary>
[ApiController]
[Authorize(Policy = DuctworkPolicies.Worker)]
[DuctworkApiException]
[Route("api/v1/ductwork/worker")]
public sealed class DuctworkWorkerApiController(
    ProcessingTaskRepository tasks,
    TaskResultIngestor ingestor,
    Func<CancellationToken, Task<SqlConnection>> open,
    LockSettings lockSettings,
    TaskSettings taskSettings,
    IOptions<DuctworkOptions> options) : ControllerBase
{
    /// <summary>
    /// The worker introduces itself and states the contract version it speaks. A worker on an incompatible
    /// major version is refused here, before it claims anything, rather than failing on its first result.
    /// </summary>
    [HttpPost("handshake")]
    public IActionResult Handshake([FromBody] WorkerHandshakeRequest request)
    {
        _ = DuctworkAuditContexts.ForWorker(HttpContext, request.WorkerName ?? "");
        if (!ManifestRules.VersionCompatible(request.ContractVersion))
            return DuctworkJson.Result(new
            {
                code = "contract-version",
                error = $"This server speaks contract {ContractInfo.Version}. Worker version '{request.ContractVersion}' is not compatible. Upgrade the worker.",
                serverContractVersion = ContractInfo.Version
            }, 409);

        return DuctworkJson.Result(new
        {
            serverContractVersion = ContractInfo.Version,
            leaseSeconds = (int)taskSettings.LeaseDuration.TotalSeconds,
            heartbeatSeconds = (int)Math.Max(1, taskSettings.LeaseDuration.TotalSeconds / 4),
            lockHeartbeatSeconds = (int)lockSettings.HeartbeatInterval.TotalSeconds,
            maxResultBytes = options.Value.MaxResultBytes
        });
    }

    /// <summary>Hands the oldest runnable task of the named types to this worker for a lease. No content when there is nothing to do.</summary>
    [HttpPost("tasks/claim")]
    public async Task<IActionResult> Claim([FromBody] WorkerClaimRequest request, CancellationToken ct)
    {
        var worker = DuctworkAuditContexts.ForWorker(HttpContext, request.WorkerName ?? "");
        var task = await tasks.ClaimNextAsync(worker, request.TaskTypes is { Length: > 0 } ? request.TaskTypes : null, ct);
        if (task is null) return NoContent();

        // Tell the worker exactly which algorithm or model the task expects, so a result under another producer would be refused.
        await using var connection = await open(ct);
        var producer = await ProducerStore.GetAsync(connection, task.ProducerId, ct);

        return DuctworkJson.Result(new
        {
            taskId = task.ProcessingTaskId,
            taskPublicId = task.PublicId,
            jobId = task.JobId,
            taskType = task.TaskType,
            attempt = task.Attempt,
            maxAttempts = task.MaxAttempts,
            inputManifestRef = task.InputManifestRef,
            contractVersion = task.ContractVersion,
            leaseExpiresUtc = task.LeaseExpiresUtc,
            producer
        });
    }

    /// <summary>Extends the lease and records progress. Tells the worker whether to continue: false means it lost the task or the task was cancelled.</summary>
    [HttpPost("tasks/{taskId:long}/heartbeat")]
    public async Task<IActionResult> Heartbeat(long taskId, [FromBody] WorkerHeartbeatRequest request, CancellationToken ct)
    {
        var worker = DuctworkAuditContexts.ForWorker(HttpContext, request.WorkerName ?? "");
        var beat = await tasks.HeartbeatAsync(taskId, worker.ActorDisplayName!, request.Done, request.Total, request.Message, ct);
        return DuctworkJson.Result(new { @continue = beat.Continue, cancelRequested = beat.CancelRequested });
    }

    /// <summary>
    /// The worker's result for a task, as a manifest. It is validated against the contract schema and the
    /// semantic rules, its provenance is taken from the task, and it is stored together with finishing the
    /// task, or not at all. The body is the manifest itself.
    /// </summary>
    [HttpPost("tasks/{taskId:long}/result")]
    [RequestSizeLimit(DuctworkWorkerApiLimits.MaxRequestBytes)]
    public async Task<IActionResult> Result(long taskId, [FromQuery] string workerName, [FromQuery] string? resultRef, CancellationToken ct)
    {
        var worker = DuctworkAuditContexts.ForWorker(HttpContext, workerName);
        var limit = Math.Min(options.Value.MaxResultBytes, DuctworkWorkerApiLimits.MaxRequestBytes);

        if (Request.ContentLength is { } declared && declared > limit)
            return DuctworkJson.Result(new { code = "too-large", error = $"The result is {declared:N0} bytes, over the {limit:N0} byte limit." }, 413);

        var json = await ReadBodyAsync(Request.Body, limit, ct);
        if (json is null)
            return DuctworkJson.Result(new { code = "too-large", error = $"The result is over the {limit:N0} byte limit." }, 413);

        var result = await ingestor.SubmitJsonAsync(taskId, worker, json, resultRef, ct);
        return DuctworkJson.Result(new
        {
            accepted = result.Accepted, cancelled = result.Cancelled, entities = result.Entities, findings = result.Findings,
            buildingTypesCreated = result.BuildingTypesCreated, raisedAsFindings = result.RaisedAsFindings, batched = result.Batched
        });
    }

    /// <summary>The worker reports that it could not finish. A retryable failure is queued again after a delay while attempts remain.</summary>
    [HttpPost("tasks/{taskId:long}/fail")]
    public async Task<IActionResult> Fail(long taskId, [FromBody] WorkerFailRequest request, CancellationToken ct)
    {
        var worker = DuctworkAuditContexts.ForWorker(HttpContext, request.WorkerName ?? "");
        var accepted = await tasks.FailAsync(taskId, worker, request.Error ?? "The worker reported a failure without a message.", request.Retryable, ct);
        return DuctworkJson.Result(new { accepted });
    }

    private static async Task<string?> ReadBodyAsync(Stream body, long limit, CancellationToken ct)
    {
        // Read at most limit + 1 bytes, so a body with no declared length cannot exceed the limit unnoticed.
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit) return null;
            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}

public static class DuctworkWorkerApiLimits
{
    /// <summary>The hard ceiling on a result request, enforced by the framework before the body is read. The configured limit can only be lower.</summary>
    public const int MaxRequestBytes = 200 * 1024 * 1024;
}
