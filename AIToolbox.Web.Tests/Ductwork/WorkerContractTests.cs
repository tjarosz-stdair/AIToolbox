using System.Text.Json;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Worker;
using AIToolbox.Web.Controllers.Ductwork;
using static AIToolbox.Web.Tests.Ductwork.ApiHarness;

namespace AIToolbox.Web.Tests.Ductwork;

/// <summary>The worker's reading of the server's answers, checked against the real controllers, so neither side can drift without a test failing.</summary>
public class WorkerContractTests(DuctworkDatabase db) : IClassFixture<DuctworkDatabase>
{
    private readonly ApiHarness _h = new(db);

    private static T Read<T>(Reply reply) => JsonSerializer.Deserialize<T>(reply.Body.GetRawText(), WorkerJson.Options)!;

    [Fact]
    public async Task The_workers_models_read_the_servers_handshake_claim_input_and_results()
    {
        var handshake = Read<HandshakeInfo>(await RunAsync(() => Task.FromResult(_h.WorkerApi().Handshake(new WorkerHandshakeRequest("w1", ContractInfo.Version, null)))));
        Assert.Equal((ContractInfo.Version, true), (handshake.ServerContractVersion, handshake.HeartbeatSeconds >= 1 && handshake.LeaseSeconds > handshake.HeartbeatSeconds));

        var producerRef = ProducerRef.Algorithm("contract-" + Guid.NewGuid().ToString("N")[..6], "1.0", "h");
        var producer = await _h.ProducerAsync(producerRef);
        var type = "detect-" + Guid.NewGuid().ToString("N")[..8];
        await _h.BindingRepo.BindAsync(type, producer, await _h.EvalRunAsync(producer), false, 3, AIToolbox.Ductwork.Core.Jobs.AuditContext.ForUser(Admin, sessionId: "setup", isAdmin: true));

        var job = await _h.NewJobAsync();
        var (tab, token) = await _h.EditorLocksAsync(job);
        var file = await _h.UploadedFileAsync(job, tab, token);
        await RunAsync(() => _h.SheetsApi(Editor, tab, token).Register(job, file, new RegisterSheetsRequest([new SheetBody(1, 2592, 1728, 0, "M1.2", "Plan", "Mechanical", "Plan", null, true)]), default));
        await RunAsync(() => _h.TypesApi(Editor, tab, token).Create(job, new CreateBuildingTypeRequest("Type 4", null, null), default));
        await RunAsync(() => _h.JobsApi(Editor, tab, token).EnqueueTask(job, new EnqueueTaskRequest(type, "inputs/x", null), default));

        var claimReply = await RunAsync(() => _h.WorkerApi().Claim(new WorkerClaimRequest("w1", [type]), default));
        var claimed = Read<ClaimedTask>(claimReply);
        Assert.Equal((job, type, 1, "inputs/x"), (claimed.JobId, claimed.TaskType, claimed.Attempt, claimed.InputManifestRef));
        Assert.Equal(producerRef.Key(), claimed.Producer.Key());                      // the worker can check the producer it was told to be

        var input = Read<TaskInput>(await RunAsync(() => _h.WorkerApi().Input(claimed.TaskId, "w1", default)));
        Assert.Equal(1, input.Files.Count);
        Assert.Equal(file, input.Files[0].FileId);
        Assert.Equal(64, input.Files[0].Sha256.Length);
        Assert.Equal(("M1.2", "Plan", true), (input.Sheets.Single().SheetNumber, input.Sheets.Single().Kind, input.Sheets.Single().InScope));
        Assert.Equal("Type 4", input.BuildingTypes.Single().Name);

        // The worker's own page report is accepted by the server as written.
        var pages = new[] { new PageReport(2, 2592, 1728, 0, "M1.3", "Second", "Mechanical", "Plan", new string('c', 64)) };
        var reported = await RunAsync(() => _h.WorkerApi().RegisterSheets(claimed.TaskId,
            new WorkerSheetsRequest("w1", file, [.. pages.Select(p => new SheetBody(p.PageNo, p.WidthPts, p.HeightPts, p.Rotation, p.SheetNumber, p.Title, p.Discipline, p.Kind, p.ContentHash, p.HasTextLayer))]), default));
        Assert.Equal(1, Read<RegisterSheetsReply>(reported).Recorded);

        var heartbeat = Read<HeartbeatReply>(await RunAsync(() => _h.WorkerApi().Heartbeat(claimed.TaskId, new WorkerHeartbeatRequest("w1", 1, 2, "x"), default)));
        Assert.True(heartbeat.Continue);

        // And a manifest the worker wrote with its own handler is accepted by the real ingestor.
        var manifest = await new AIToolbox.Ductwork.Worker.Handlers.FakeDetectHandler().RunAsync(
            new TaskContext(claimed, "w1", Path.GetTempPath(), new NullApi(input), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance), default);
        var submitted = await RunAsync(() => _h.WorkerApi(body: new MemoryStream(System.Text.Encoding.UTF8.GetBytes(manifest))).Result(claimed.TaskId, "w1", null, default));
        var reply = Read<SubmitReply>(submitted);
        Assert.Equal((true, 6), (reply.Accepted, reply.Entities));
    }

    /// <summary>Only the input call is needed by the fake handler.</summary>
    private sealed class NullApi(TaskInput input) : IDuctworkApi
    {
        public Task<TaskInput> GetInputAsync(long taskId, CancellationToken ct) => Task.FromResult(input);
        public Task<HandshakeInfo> HandshakeAsync(IReadOnlyCollection<string> taskTypes, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClaimedTask?> ClaimAsync(IReadOnlyCollection<string> taskTypes, CancellationToken ct) => throw new NotSupportedException();
        public Task<HeartbeatReply> HeartbeatAsync(long taskId, int done, int? total, string? message, CancellationToken ct) => throw new NotSupportedException();
        public Task DownloadFileAsync(long taskId, Guid fileId, Stream destination, CancellationToken ct) => throw new NotSupportedException();
        public Task<RegisterSheetsReply> RegisterSheetsAsync(long taskId, Guid fileId, IReadOnlyList<PageReport> pages, CancellationToken ct) => throw new NotSupportedException();
        public Task<SubmitReply> SubmitResultAsync(long taskId, string manifestJson, string? resultRef, CancellationToken ct) => throw new NotSupportedException();
        public Task FailAsync(long taskId, string error, bool retryable, CancellationToken ct) => throw new NotSupportedException();
    }
}
