using System.Text.Json;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Tasks;
using AIToolbox.Web.Data;
using AIToolbox.Web.Ductwork;
using AIToolbox.Web.Models;
using AIToolbox.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Web.Controllers.Ductwork;

/// <summary>
/// The workers that serve this instance: who has connected, whether they are alive, what each is running, and the controls an
/// administrator needs: ask one to finish its work and stop (before an upgrade), forget one that is gone, and create the API key
/// a new worker needs. A worker holds no database access or provider keys; its key can do only what the worker API allows.
/// </summary>
[ApiController]
[Authorize(Policy = "AdminOnly")]
[AutoValidateAntiforgeryToken]
[DuctworkApiException]
[Route("ductwork/api/admin/workers")]
public sealed class DuctworkWorkersAdminController(WorkerRegistry registry, IApiConsumerRepository consumers, IApiKeyService apiKeys) : ControllerBase
{
    private static readonly string[] WorkerScopes = [KnownApiScopes.DuctworkWorker, $"tool:{DuctworkTool.ToolKey}"];

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var workers = await registry.ListAsync(cancellationToken: ct);
        var names = (await consumers.GetConsumersAsync(ct)).ToDictionary(c => c.Id, c => c);
        return DuctworkJson.Result(workers.Select(w => new
        {
            workerInstanceId = w.WorkerInstanceId, workerName = w.WorkerName, workerId = w.WorkerId, apiConsumerId = w.ApiConsumerId,
            keyName = names.TryGetValue(w.ApiConsumerId, out var c) ? c.Name : null, keyEnabled = names.TryGetValue(w.ApiConsumerId, out var e) ? e.IsEnabled : (bool?)null,
            hostName = w.HostName, workerVersion = w.WorkerVersion, contractVersion = w.ContractVersion, taskTypes = w.TaskTypes, maxConcurrency = w.MaxConcurrency,
            firstSeenUtc = w.FirstSeenUtc, lastSeenUtc = w.LastSeenUtc, lastClaimUtc = w.LastClaimUtc, secondsSinceSeen = (int)w.SecondsSinceSeen, runningTasks = w.RunningTasks,
            status = w.Status, drainRequested = w.DrainRequested, drainRequestedBy = w.DrainRequestedBy, drainRequestedUtc = w.DrainRequestedUtc
        }));
    }

    /// <summary>Asks a worker to finish what it is doing and take no more, or lets it carry on. Takes effect the next time it asks for work.</summary>
    [HttpPost("{workerInstanceId:int}/drain")]
    public async Task<IActionResult> Drain(int workerInstanceId, [FromBody] DrainWorkerRequest request, CancellationToken ct) =>
        DuctworkJson.Result(await registry.SetDrainAsync(workerInstanceId, request.Drain, DuctworkAuditContexts.ForUser(HttpContext), ct));

    [HttpDelete("{workerInstanceId:int}")]
    public async Task<IActionResult> Forget(int workerInstanceId, CancellationToken ct)
    {
        await registry.ForgetAsync(workerInstanceId, DuctworkAuditContexts.ForUser(HttpContext), ct);
        return DuctworkJson.Result(new { forgotten = true });
    }

    /// <summary>
    /// Creates the API key for a new worker, holding exactly the two scopes a worker needs and nothing else. The key is shown once,
    /// here, and cannot be read again; the answer also carries the settings the worker needs, ready to paste.
    /// </summary>
    [HttpPost("keys")]
    public async Task<IActionResult> CreateKey([FromBody] CreateWorkerKeyRequest request, CancellationToken ct)
    {
        var name = (request.WorkerName ?? "").Trim();
        if (name.Length is 0 or > 60 || !name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
            throw new DuctworkBadRequestException("A worker name is 1 to 60 letters, digits, dashes, underscores or dots.");

        var audit = DuctworkAuditContexts.ForUser(HttpContext);
        int consumerId;
        try
        {
            consumerId = await consumers.CreateConsumerAsync(new ApiConsumer
            {
                Name = $"Ductwork worker: {name}", Description = "A central Ductwork worker. Its key holds only the worker and Ductwork tool scopes.", OwnerEmail = audit.ActorEmail, IsEnabled = true
            }, ct);
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            throw new JobRuleViolationException($"A worker key named “{name}” already exists. Choose another name, or manage the existing one under API Consumers.");
        }

        var raw = apiKeys.GenerateRawKey();
        var keyId = await consumers.CreateKeyAsync(new ApiConsumerKey
        {
            ApiConsumerId = consumerId, Name = $"{name} key", KeyPrefix = apiKeys.GetPrefix(raw), KeyHash = apiKeys.HashKey(raw),
            ScopesJson = JsonSerializer.Serialize(WorkerScopes, new JsonSerializerOptions(JsonSerializerDefaults.Web)), ExpiresUtc = null
        }, ct);
        await registry.RecordKeyIssuedAsync(consumerId, name, audit, ct);

        var serverUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        var settings = JsonSerializer.Serialize(new { Worker = new { ServerUrl = serverUrl, ApiKey = raw, WorkerName = name } }, new JsonSerializerOptions { WriteIndented = true });
        return DuctworkJson.Result(new { apiConsumerId = consumerId, keyId, workerName = name, apiKey = raw, scopes = WorkerScopes, serverUrl, settings }, 201);
    }
}
