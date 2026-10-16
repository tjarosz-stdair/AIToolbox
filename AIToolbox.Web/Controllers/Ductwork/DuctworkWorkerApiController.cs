using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Counting;
using AIToolbox.Ductwork.Core.Data;
using AIToolbox.Ductwork.Core.Drawings;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Tasks;
using AIToolbox.Web.Ductwork;
using AIToolbox.Web.Security;
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
    SheetRepository sheets,
    UploadService uploads,
    BuildingTypeRepository buildingTypes,
    DuctworkVisionGateway vision,
    WorkerRegistry registry,
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
    public async Task<IActionResult> Handshake([FromBody] WorkerHandshakeRequest request, CancellationToken ct)
    {
        var worker = DuctworkAuditContexts.ForWorker(HttpContext, request.WorkerName ?? "");
        if (!ManifestRules.VersionCompatible(request.ContractVersion))
            return DuctworkJson.Result(new
            {
                code = "contract-version",
                error = $"This server speaks contract {ContractInfo.Version}. Worker version '{request.ContractVersion}' is not compatible. Upgrade the worker.",
                serverContractVersion = ContractInfo.Version
            }, 409);

        // An incompatible worker is refused above and never recorded: it is not a worker of this instance until it can do the work.
        var recorded = await registry.RecordHandshakeAsync(ConsumerId, request.WorkerName!, new WorkerHello(request.ContractVersion ?? "", request.WorkerVersion, request.HostName, request.MaxConcurrency,
            request.TaskTypes ?? []), worker, ct);

        return DuctworkJson.Result(new
        {
            drain = recorded.DrainRequested,
            serverContractVersion = ContractInfo.Version,
            leaseSeconds = (int)taskSettings.LeaseDuration.TotalSeconds,
            heartbeatSeconds = (int)Math.Max(1, taskSettings.LeaseDuration.TotalSeconds / 4),
            lockHeartbeatSeconds = (int)lockSettings.HeartbeatInterval.TotalSeconds,
            maxResultBytes = options.Value.MaxResultBytes
        });
    }

    /// <summary>Whether an administrator has asked this worker to finish what it has and take no more. A worker checks this between claims.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status([FromQuery] string workerName, CancellationToken ct)
    {
        _ = DuctworkAuditContexts.ForWorker(HttpContext, workerName);
        await registry.TouchAsync(ConsumerId, workerName, claimed: false, ct);
        return DuctworkJson.Result(new { drain = await registry.GetDrainAsync(ConsumerId, workerName, ct) });
    }

    private int ConsumerId => HttpContext.User.GetApiConsumerId() ?? throw new DuctworkBadRequestException("A worker must authenticate with an API key.");

    /// <summary>Hands the oldest runnable task of the named types to this worker for a lease. No content when there is nothing to do.</summary>
    [HttpPost("tasks/claim")]
    public async Task<IActionResult> Claim([FromBody] WorkerClaimRequest request, CancellationToken ct)
    {
        var worker = DuctworkAuditContexts.ForWorker(HttpContext, request.WorkerName ?? "");
        await registry.TouchAsync(ConsumerId, request.WorkerName!, claimed: false, ct);

        // A worker asked to drain is given nothing new, whatever it asks for, so the instruction holds even for a worker that ignores it.
        if (await registry.GetDrainAsync(ConsumerId, request.WorkerName!, ct)) return NoContent();

        var task = await tasks.ClaimNextAsync(worker, request.TaskTypes is { Length: > 0 } ? request.TaskTypes : null, ct);
        if (task is null) return NoContent();
        await registry.TouchAsync(ConsumerId, request.WorkerName!, claimed: true, ct);

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

    /// <summary>
    /// What the task is about: the job's drawing files, the sheets already recorded, and its building types. Only the worker that
    /// holds the task can ask, and only about that task's job, so a worker never learns about work it was not given.
    /// </summary>
    [HttpGet("tasks/{taskId:long}/input")]
    public async Task<IActionResult> Input(long taskId, [FromQuery] string workerName, CancellationToken ct)
    {
        var task = await RequireHeldAsync(taskId, workerName, ct);
        var files = await uploads.ListFilesAsync(task.JobId, ct);
        var recorded = await sheets.ListByJobAsync(task.JobId, ct);
        var types = await buildingTypes.ListByJobAsync(task.JobId, ct);
        return DuctworkJson.Result(new
        {
            taskId = task.ProcessingTaskId, jobId = task.JobId, taskType = task.TaskType, inputManifestRef = task.InputManifestRef,
            files = files.Select(f => new { fileId = f.PublicId, fileName = f.OriginalName, bytes = f.Bytes, sha256 = f.Sha256, revision = f.RevisionNo, duplicateOf = f.DuplicateOfPublicId }),
            sheets = recorded.Select(s => new
            {
                sheetId = s.PublicId, fileId = s.SourceFilePublicId, pageNo = s.PageNo, sheetNumber = s.SheetNumber, title = s.Title, kind = s.Kind, inScope = s.InScope,
                widthPts = s.WidthPts, heightPts = s.HeightPts, rotation = s.Rotation, duplicateOf = s.DuplicateOfPublicId,
                views = s.Views.Select(v => new { viewId = v.PublicId, title = v.Title, buildingTypeId = v.BuildingTypeId, scaleRatio = v.ScaleRatio })
            }),
            buildingTypes = types.Select(b => new { buildingTypeId = b.BuildingTypeId, name = b.Name })
        });
    }

    /// <summary>A drawing file of the task's job, for the worker that holds the task.</summary>
    [HttpGet("tasks/{taskId:long}/files/{fileId:guid}/content")]
    public async Task<IActionResult> FileContent(long taskId, Guid fileId, [FromQuery] string workerName, CancellationToken ct)
    {
        var task = await RequireHeldAsync(taskId, workerName, ct);
        var file = await uploads.GetFileAsync(task.JobId, fileId, ct) ?? throw new KeyNotFoundException("That file is not on this task's job.");
        var stream = await uploads.GetFileContentAsync(file, ct) ?? throw new KeyNotFoundException("That file is no longer available.");
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Cache-Control"] = "private, no-store";
        return new FileStreamResult(stream, "application/pdf") { EnableRangeProcessing = true };
    }

    private async Task<ProcessingTaskRecord> RequireHeldAsync(long taskId, string workerName, CancellationToken ct)
    {
        var worker = DuctworkAuditContexts.ForWorker(HttpContext, workerName);
        var task = await tasks.GetAsync(taskId, ct);
        if (task is null || task.Status != ProcessingTaskStatus.Running || task.LeaseOwner != worker.ActorDisplayName)
            throw new JobRuleViolationException("This task is not running under this worker.");
        return task;
    }

    /// <summary>Extends the lease and records progress. Tells the worker whether to continue: false means it lost the task or the task was cancelled.</summary>
    [HttpPost("tasks/{taskId:long}/heartbeat")]
    public async Task<IActionResult> Heartbeat(long taskId, [FromBody] WorkerHeartbeatRequest request, CancellationToken ct)
    {
        var worker = DuctworkAuditContexts.ForWorker(HttpContext, request.WorkerName ?? "");
        var beat = await tasks.HeartbeatAsync(taskId, worker.ActorDisplayName!, request.Done, request.Total, request.Message, ct);
        await registry.TouchAsync(ConsumerId, request.WorkerName!, claimed: false, ct);
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
        await registry.TouchAsync(ConsumerId, workerName, claimed: false, ct);
        return DuctworkJson.Result(new
        {
            accepted = result.Accepted, cancelled = result.Cancelled, entities = result.Entities, findings = result.Findings,
            buildingTypesCreated = result.BuildingTypesCreated, raisedAsFindings = result.RaisedAsFindings, batched = result.Batched
        });
    }

    /// <summary>The worker reports the pages it found in a drawing file, under the task it holds.</summary>
    [HttpPost("tasks/{taskId:long}/sheets")]
    public async Task<IActionResult> RegisterSheets(long taskId, [FromBody] WorkerSheetsRequest request, CancellationToken ct)
    {
        var worker = DuctworkAuditContexts.ForWorker(HttpContext, request.WorkerName ?? "");
        var created = await sheets.RegisterFromTaskAsync(taskId, request.FileId, DuctworkSheetsController.ToNew(request.Sheets), worker, ct);
        // The answer lists every page of the file, so count only the pages this report added.
        var added = created.Where(s => (request.Sheets ?? []).Any(p => p.PageNo == s.PageNo)).ToList();
        return DuctworkJson.Result(new { recorded = added.Count, duplicates = added.Count(s => s.DuplicateOfPublicId is not null) }, 201);
    }

    /// <summary>
    /// A worker asks the server to send images to the hosted model its task was approved for. The worker holds no provider keys and
    /// cannot call one: the gateway checks the policy, the approval, the sheets and the budget, reserves the images, and only then sends.
    /// </summary>
    [HttpPost("tasks/{taskId:long}/vision")]
    [RequestSizeLimit(DuctworkWorkerApiLimits.MaxVisionRequestBytes)]
    public async Task<IActionResult> Vision(long taskId, [FromBody] WorkerVisionRequest request, CancellationToken ct)
    {
        var worker = DuctworkAuditContexts.ForWorker(HttpContext, request.WorkerName ?? "");
        var result = await vision.SendAsync(taskId, worker, new VisionSendInput(request.Directive ?? "", request.SheetIds ?? [],
            (request.Images ?? []).Select(i => new VisionImageInput(i.Label, i.MediaType ?? "", i.DataBase64 ?? "")).ToList()), ct);
        return DuctworkJson.Result(new
        {
            text = result.Text, finishReason = result.FinishReason, inputTokens = result.InputTokens, outputTokens = result.OutputTokens,
            transmissionId = result.TransmissionId, imagesSent = result.ImagesSent, provider = result.Provider, model = result.Model
        });
    }

    /// <summary>The worker reports that it could not finish. A retryable failure is queued again after a delay while attempts remain.</summary>
    [HttpPost("tasks/{taskId:long}/fail")]
    public async Task<IActionResult> Fail(long taskId, [FromBody] WorkerFailRequest request, CancellationToken ct)
    {
        var worker = DuctworkAuditContexts.ForWorker(HttpContext, request.WorkerName ?? "");
        var accepted = await tasks.FailAsync(taskId, worker, request.Error ?? "The worker reported a failure without a message.", request.Retryable, ct);
        await registry.TouchAsync(ConsumerId, request.WorkerName!, claimed: false, ct);
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

    /// <summary>Images travel as base64, so this allows about 15 images of the largest size the providers accept. Crops are far smaller.</summary>
    public const int MaxVisionRequestBytes = 100 * 1024 * 1024;
}
