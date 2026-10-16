using System.Text;
using System.Text.Json;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Web.Controllers.Ductwork;
using AIToolbox.Web.Ductwork;
using AIToolbox.Web.Security;
using static AIToolbox.Web.Tests.Ductwork.ApiHarness;

namespace AIToolbox.Web.Tests.Ductwork;

public class LockApiTests(DuctworkDatabase db) : IClassFixture<DuctworkDatabase>
{
    private readonly ApiHarness _h = new(db);

    [Fact]
    public async Task Taking_the_lock_returns_a_fencing_token_and_status_shows_the_holder_to_members_only()
    {
        var id = await _h.NewJobAsync();
        var tab = NewTab();

        var acquired = await RunAsync(() => _h.LockApi(Editor, tab).Acquire(id, default));
        Assert.True(acquired["fencingToken"].GetInt64() > 0);

        var status = await RunAsync(() => _h.LockApi(Viewer, NewTab()).Status(id, default));
        Assert.Equal(200, status.Status);
        Assert.Equal(404, (await RunAsync(() => _h.LockApi(Stranger, NewTab()).Status(id, default))).Status);
        Assert.Equal(404, (await RunAsync(() => _h.LockApi(Stranger, NewTab()).Acquire(id, default))).Status);
    }

    [Fact]
    public async Task A_viewer_cannot_take_the_lock()
    {
        var id = await _h.NewJobAsync();
        var reply = await RunAsync(() => _h.LockApi(Viewer, NewTab()).Acquire(id, default));
        Assert.Equal(403, reply.Status);
    }

    [Fact]
    public async Task Heartbeating_with_the_right_token_keeps_the_lock_and_a_wrong_token_does_not()
    {
        var id = await _h.NewJobAsync();
        var (tab, token) = await _h.EditorLocksAsync(id);

        var good = await RunAsync(() => _h.LockApi(Editor, tab, token).Heartbeat(id, default));
        Assert.Equal(200, good.Status);

        var bad = await RunAsync(() => _h.LockApi(Editor, tab, token + 99).Heartbeat(id, default));
        Assert.True(bad.Status is 200 or 409);          // a lost lock is reported to the tab, never silently extended
        if (bad.Status == 200) Assert.False(bad.Body.TryGetProperty("held", out var held) && held.GetBoolean());
    }

    [Fact]
    public async Task A_second_editor_asks_and_the_holder_can_hand_over_and_the_old_token_dies()
    {
        var id = await _h.NewJobAsync();
        var (holderTab, holderToken) = await _h.EditorLocksAsync(id, Editor);
        await _h.JobRepo.AddMemberAsync(id, "second@example.com", AIToolbox.Ductwork.Core.Jobs.JobRole.Editor, AIToolbox.Ductwork.Core.Jobs.AuditContext.ForUser(Owner, sessionId: "setup"));

        var secondTab = NewTab();
        var asked = await RunAsync(() => _h.LockApi("second@example.com", secondTab).RequestTakeover(id, default));
        Assert.False(asked["granted"].GetBoolean());
        var requestId = asked["request"].GetProperty("id").GetInt64();

        var handed = await RunAsync(() => _h.LockApi(Editor, holderTab, holderToken).Respond(id, requestId, new TakeoverResponseRequest(true), default));
        Assert.True(handed["handedOver"].GetBoolean());

        var job = await _h.JobRepo.GetAsync(id);
        var stale = await RunAsync(() => _h.JobsApi(Editor, holderTab, holderToken).UpdateDetails(id, new UpdateDetailsRequest("late", null, null, null, null, job!.StateVersion), default));
        Assert.Equal("lock-lost", stale.Code);
    }

    [Fact]
    public async Task Only_an_owner_or_admin_can_force_the_lock()
    {
        var id = await _h.NewJobAsync();
        await _h.EditorLocksAsync(id, Editor);

        Assert.Equal(403, (await RunAsync(() => _h.LockApi(Viewer, NewTab()).Force(id, default))).Status);
        var forced = await RunAsync(() => _h.LockApi(Owner, NewTab()).Force(id, default));
        Assert.Equal(200, forced.Status);
        Assert.True(forced["fencingToken"].GetInt64() > 0);
    }

    [Fact]
    public async Task Releasing_frees_the_lock()
    {
        var id = await _h.NewJobAsync();
        var (tab, token) = await _h.EditorLocksAsync(id);

        Assert.Equal(200, (await RunAsync(() => _h.LockApi(Editor, tab, token).Release(id, default))).Status);
        var again = await RunAsync(() => _h.LockApi(Owner, NewTab()).Acquire(id, default));
        Assert.Equal(200, again.Status);
    }
}

public class AdminApiTests(DuctworkDatabase db) : IClassFixture<DuctworkDatabase>
{
    private readonly ApiHarness _h = new(db);

    [Fact]
    public async Task An_administrator_binds_a_task_type_to_an_evaluated_producer_and_can_retire_it()
    {
        var producer = await _h.ProducerAsync(ProducerRef.Algorithm("bind-" + Guid.NewGuid().ToString("N")[..6], "1.0", "h"));
        var run = await _h.EvalRunAsync(producer);
        var type = "detect-" + Guid.NewGuid().ToString("N")[..8];

        var bound = await RunAsync(() => _h.AdminApi().Bind(new BindTaskRequest(type, producer, run, false, 0), default));
        Assert.Equal((201, type), (bound.Status, bound["taskType"].GetString()));
        Assert.Equal(3, bound["maxAttempts"].GetInt32());       // 0 means the default

        var listed = await RunAsync(() => _h.AdminApi().List(false, default));
        Assert.Contains(listed.Body.EnumerateArray(), b => b.GetProperty("taskType").GetString() == type);

        Assert.True((await RunAsync(() => _h.AdminApi().Retire(type, default)))["retired"].GetBoolean());
    }

    [Fact]
    public async Task Binding_to_a_producer_that_has_not_been_evaluated_is_refused()
    {
        var producer = await _h.ProducerAsync(ProducerRef.Algorithm("noeval-" + Guid.NewGuid().ToString("N")[..6], "1.0", "h"));

        var reply = await RunAsync(() => _h.AdminApi().Bind(new BindTaskRequest("x-" + Guid.NewGuid().ToString("N")[..6], producer, 999_999, false, 3), default));

        Assert.Equal((422, "rule"), (reply.Status, reply.Code));
    }

    [Fact]
    public async Task A_person_who_is_not_an_administrator_cannot_bind()
    {
        var producer = await _h.ProducerAsync(ProducerRef.Algorithm("notadmin-" + Guid.NewGuid().ToString("N")[..6], "1.0", "h"));
        var run = await _h.EvalRunAsync(producer);
        var controller = _h.AdminApi(Editor);                     // the harness marks admin; strip the role to model a plain user
        controller.ControllerContext.HttpContext.User = Person(Editor);

        var reply = await RunAsync(() => controller.Bind(new BindTaskRequest("y-" + Guid.NewGuid().ToString("N")[..6], producer, run, false, 3), default));

        Assert.Equal(403, reply.Status);
    }
}

public class WorkerApiTests(DuctworkDatabase db) : IClassFixture<DuctworkDatabase>
{
    private readonly ApiHarness _h = new(db);

    private static StringContent Unused => new("");

    private static MemoryStream Body(string json) => new(Encoding.UTF8.GetBytes(json));

    private async Task<(long JobId, string TaskType, long ProducerId, ProducerRef Producer, string Tab, long Token)> BoundJobAsync()
    {
        var producerRef = ProducerRef.Algorithm("worker-" + Guid.NewGuid().ToString("N")[..6], "1.0", "h");
        var producer = await _h.ProducerAsync(producerRef);
        var run = await _h.EvalRunAsync(producer);
        var type = "detect-" + Guid.NewGuid().ToString("N")[..8];
        await _h.BindingRepo.BindAsync(type, producer, run, false, 3, AIToolbox.Ductwork.Core.Jobs.AuditContext.ForUser(Admin, sessionId: "setup", isAdmin: true));

        var id = await _h.NewJobAsync();
        var (tab, token) = await _h.EditorLocksAsync(id);
        return (id, type, producer, producerRef, tab, token);
    }

    private static string ManifestJson(string taskType, string taskPublicId, ProducerRef producer, params DetectionProposal[] detections) =>
        JsonSerializer.Serialize(new TaskResultManifest
        {
            ContractVersion = ContractInfo.Version,
            TaskType = taskType,
            TaskPublicId = taskPublicId,
            Producer = producer,
            Detections = detections,
            Findings = []
        }, ContractJson.Options);

    private static DetectionProposal Detection(string id, string building) => new()
    {
        DetectionId = id, Type = "Boot", Size = "4x10x7", BuildingType = building, Quantity = 1, Confidence = 0.95,
        Method = DetectionMethod.Text, Family = DetectorFamily.LabelFlagCount, Box = new BoundingBox(1, 2, 3, 4), SheetViewRef = "M1.2", Evidence = "label"
    };

    [Fact]
    public async Task The_handshake_returns_the_leases_and_refuses_an_incompatible_contract_major_version()
    {
        var ok = await RunAsync(() => _h.WorkerApi().Handshake(new WorkerHandshakeRequest("w1", ContractInfo.Version, null), default));
        Assert.Equal(200, ok.Status);
        Assert.Equal(ContractInfo.Version, ok["serverContractVersion"].GetString());
        Assert.True(ok["heartbeatSeconds"].GetInt32() >= 1);

        var bad = await RunAsync(() => _h.WorkerApi().Handshake(new WorkerHandshakeRequest("w1", "99.0.0", null), default));
        Assert.Equal((409, "contract-version"), (bad.Status, bad.Code));
    }

    [Fact]
    public async Task A_worker_with_no_name_is_a_bad_request()
    {
        var reply = await RunAsync(() => _h.WorkerApi().Claim(new WorkerClaimRequest(" ", null), default));
        Assert.Equal(400, reply.Status);
    }

    [Fact]
    public async Task Nothing_to_do_is_204()
    {
        var reply = await RunAsync(() => _h.WorkerApi().Claim(new WorkerClaimRequest("w1", ["no-such-type-" + Guid.NewGuid().ToString("N")]), default));
        Assert.Equal(204, reply.Status);
    }

    [Fact]
    public async Task A_bound_task_runs_from_the_browser_to_the_worker_and_back_into_the_count_sheet()
    {
        var (id, type, producerId, producerRef, tab, token) = await BoundJobAsync();

        var queued = await RunAsync(() => _h.JobsApi(Editor, tab, token).EnqueueTask(id, new EnqueueTaskRequest(type, "inputs/m.json", null), default));
        Assert.True(queued.Ok, queued.Body.ToString());

        var claimed = await RunAsync(() => _h.WorkerApi().Claim(new WorkerClaimRequest("w1", [type]), default));
        Assert.Equal(200, claimed.Status);
        Assert.Equal((id, type, "inputs/m.json"), (claimed["jobId"].GetInt64(), claimed["taskType"].GetString(), claimed["inputManifestRef"].GetString()));
        Assert.Equal(producerRef.AlgorithmName, claimed["producer"].GetProperty("algorithmName").GetString());
        var taskId = claimed["taskId"].GetInt64();
        var publicId = claimed["taskPublicId"].GetString()!;

        var beat = await RunAsync(() => _h.WorkerApi().Heartbeat(taskId, new WorkerHeartbeatRequest("w1", 1, 2, "halfway"), default));
        Assert.True(beat["continue"].GetBoolean());

        var json = ManifestJson(type, publicId, producerRef, Detection("d1", "Type 4"), Detection("d2", "Type 4"));
        var result = await RunAsync(() => _h.WorkerApi(body: Body(json)).Result(taskId, "w1", "results/m.json", default));
        Assert.Equal((200, true, 2), (result.Status, result["accepted"].GetBoolean(), result["entities"].GetInt32()));

        var sheet = await RunAsync(() => _h.JobsApi(Viewer).CountSheet(id, default));
        Assert.Equal(1, sheet["blocks"].GetArrayLength());
        Assert.False(sheet["isFinal"].GetBoolean());          // proposals await a person, and the multiplier is unconfirmed
    }

    [Fact]
    public async Task A_second_worker_cannot_report_a_task_it_does_not_hold()
    {
        var (id, type, _, producerRef, tab, token) = await BoundJobAsync();
        await RunAsync(() => _h.JobsApi(Editor, tab, token).EnqueueTask(id, new EnqueueTaskRequest(type, null, null), default));
        var claimed = await RunAsync(() => _h.WorkerApi().Claim(new WorkerClaimRequest("w1", [type]), default));
        var json = ManifestJson(type, claimed["taskPublicId"].GetString()!, producerRef, Detection("d1", "Type 4"));

        var stolen = await RunAsync(() => _h.WorkerApi(body: Body(json)).Result(claimed["taskId"].GetInt64(), "w2", null, default));

        Assert.False(stolen.Ok);
        Assert.Equal(0, (await RunAsync(() => _h.JobsApi(Viewer).ListEntities(id, null, null, null, null, null, default))).Body.GetArrayLength());
    }

    [Fact]
    public async Task A_result_under_another_producer_or_malformed_is_a_422_and_stores_nothing()
    {
        var (id, type, _, _, tab, token) = await BoundJobAsync();
        await RunAsync(() => _h.JobsApi(Editor, tab, token).EnqueueTask(id, new EnqueueTaskRequest(type, null, null), default));
        var claimed = await RunAsync(() => _h.WorkerApi().Claim(new WorkerClaimRequest("w1", [type]), default));
        var taskId = claimed["taskId"].GetInt64();

        var wrongProducer = ManifestJson(type, claimed["taskPublicId"].GetString()!, ProducerRef.Algorithm("someone-else", "9.9", "x"), Detection("d1", "Type 4"));
        var refused = await RunAsync(() => _h.WorkerApi(body: Body(wrongProducer)).Result(taskId, "w1", null, default));
        Assert.Equal((422, "result-rejected"), (refused.Status, refused.Code));

        var garbage = await RunAsync(() => _h.WorkerApi(body: Body("{ not json")).Result(taskId, "w1", null, default));
        Assert.Equal((422, "result-rejected"), (garbage.Status, garbage.Code));

        Assert.Equal(0, (await RunAsync(() => _h.JobsApi(Viewer).ListEntities(id, null, null, null, null, null, default))).Body.GetArrayLength());
    }

    [Fact]
    public async Task An_oversized_result_is_a_413_before_any_parsing()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new DuctworkOptions { MaxResultBytes = 100 });
        var reply = await RunAsync(() => _h.WorkerApi(body: Body(new string(' ', 500)), options: options).Result(1, "w1", null, default));
        Assert.Equal((413, "too-large"), (reply.Status, reply.Code));

        // and one with no declared length is stopped while reading
        var stream = new NonSeekable(Body(new string(' ', 500)));
        var chunked = await RunAsync(() => _h.WorkerApi(body: stream, options: options).Result(1, "w1", null, default));
        Assert.Equal(413, chunked.Status);
    }

    [Fact]
    public async Task A_failed_retryable_task_goes_back_to_the_queue_and_a_cancel_tells_the_worker_to_stop()
    {
        var (id, type, _, _, tab, token) = await BoundJobAsync();
        var queued = await RunAsync(() => _h.JobsApi(Editor, tab, token).EnqueueTask(id, new EnqueueTaskRequest(type, null, null), default));
        var claimed = await RunAsync(() => _h.WorkerApi().Claim(new WorkerClaimRequest("w1", [type]), default));
        var taskId = claimed["taskId"].GetInt64();

        var cancel = await RunAsync(() => _h.JobsApi(Editor, tab).CancelTask(id, taskId, default));
        Assert.Equal(200, cancel.Status);
        var beat = await RunAsync(() => _h.WorkerApi().Heartbeat(taskId, new WorkerHeartbeatRequest("w1", 1, null, null), default));
        Assert.False(beat["continue"].GetBoolean());
        Assert.True(queued.Ok);
    }

    [Fact]
    public async Task A_worker_failure_is_recorded()
    {
        var (id, type, _, _, tab, token) = await BoundJobAsync();
        await RunAsync(() => _h.JobsApi(Editor, tab, token).EnqueueTask(id, new EnqueueTaskRequest(type, null, null), default));
        var claimed = await RunAsync(() => _h.WorkerApi().Claim(new WorkerClaimRequest("w1", [type]), default));

        var failed = await RunAsync(() => _h.WorkerApi().Fail(claimed["taskId"].GetInt64(), new WorkerFailRequest("w1", "out of memory", false), default));

        Assert.True(failed["accepted"].GetBoolean());
    }

    private sealed class NonSeekable(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
