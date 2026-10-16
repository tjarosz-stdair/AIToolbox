using System.Text.Json;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Web.Controllers.Ductwork;
using AIToolbox.Web.Models;
using static AIToolbox.Web.Tests.Ductwork.ApiHarness;

namespace AIToolbox.Web.Tests.Ductwork;

public class WorkersApiTests(DuctworkDatabase db) : IClassFixture<DuctworkDatabase>
{
    private readonly ApiHarness _h = new(db);

    private static int NextConsumer = 2000;

    private async Task<Reply> HandshakeAsync(int consumer, string name, string contract = "", string? version = "1.4.0", string? host = "FARM-01") =>
        await RunAsync(() => _h.WorkerApi(consumer, "key-" + consumer).Handshake(new WorkerHandshakeRequest(name, contract.Length == 0 ? ContractInfo.Version : contract, ["detect"], version, host, 3), default));

    private async Task<JsonElement> ListedAsync(string name)
    {
        var list = await RunAsync(() => _h.WorkersAdminApi().List(default));
        Assert.True(list.Ok);
        return list.Body.EnumerateArray().Single(w => w.GetProperty("workerName").GetString() == name);
    }

    [Fact]
    public async Task A_worker_that_connects_appears_to_the_administrator_with_its_key_version_and_host()
    {
        var consumer = Interlocked.Increment(ref NextConsumer);
        _h.Consumers.Consumers.Add(new ApiConsumer { Id = consumer, Name = "Ductwork worker: farm", IsEnabled = true });
        var name = "farm-" + consumer;

        var hello = await HandshakeAsync(consumer, name);
        Assert.Equal((200, false), (hello.Status, hello["drain"].GetBoolean()));

        var w = await ListedAsync(name);
        Assert.Equal(("Online", "1.4.0", "FARM-01", 3, 0, "Ductwork worker: farm"), (w.GetProperty("status").GetString(), w.GetProperty("workerVersion").GetString(), w.GetProperty("hostName").GetString(),
            w.GetProperty("maxConcurrency").GetInt32(), w.GetProperty("runningTasks").GetInt32(), w.GetProperty("keyName").GetString()));
        Assert.Equal($"c{consumer}:{name}", w.GetProperty("workerId").GetString());
    }

    [Fact]
    public async Task A_worker_with_an_incompatible_contract_is_refused_and_is_not_listed_as_one_of_ours()
    {
        var consumer = Interlocked.Increment(ref NextConsumer);
        var name = "old-" + consumer;

        var refused = await HandshakeAsync(consumer, name, contract: "99.0.0");

        Assert.Equal((409, "contract-version"), (refused.Status, refused.Code));
        var list = await RunAsync(() => _h.WorkersAdminApi().List(default));
        Assert.DoesNotContain(list.Body.EnumerateArray(), x => x.GetProperty("workerName").GetString() == name);
    }

    [Fact]
    public async Task Asking_a_worker_to_drain_stops_it_being_given_work_even_if_it_asks_and_lifting_it_restores_that()
    {
        var consumer = Interlocked.Increment(ref NextConsumer);
        var name = "drain-" + consumer;
        await HandshakeAsync(consumer, name);

        // Work is waiting for it.
        var producerRef = ProducerRef.Algorithm("drain-" + Guid.NewGuid().ToString("N")[..6], "1.0", "h");
        var producer = await _h.ProducerAsync(producerRef);
        var type = "drain-" + Guid.NewGuid().ToString("N")[..8];
        await _h.BindingRepo.BindAsync(type, producer, await _h.EvalRunAsync(producer), false, 3, AIToolbox.Ductwork.Core.Jobs.AuditContext.ForUser(Admin, sessionId: "setup", isAdmin: true));
        var job = await _h.NewJobAsync();
        var (tab, token) = await _h.EditorLocksAsync(job);
        await RunAsync(() => _h.JobsApi(Editor, tab, token).EnqueueTask(job, new EnqueueTaskRequest(type, null, null), default));
        var id = (await ListedAsync(name)).GetProperty("workerInstanceId").GetInt32();

        var drained = await RunAsync(() => _h.WorkersAdminApi().Drain(id, new DrainWorkerRequest(true), default));
        Assert.Equal((true, "admin@example.com"), (drained["drainRequested"].GetBoolean(), drained["drainRequestedBy"].GetString()));

        Assert.Equal(204, (await RunAsync(() => _h.WorkerApi(consumer, "key").Claim(new WorkerClaimRequest(name, [type]), default))).Status);
        Assert.True((await RunAsync(() => _h.WorkerApi(consumer, "key").Status(name, default)))["drain"].GetBoolean());
        Assert.True((await HandshakeAsync(consumer, name))["drain"].GetBoolean());                  // reconnecting does not forget the request

        await RunAsync(() => _h.WorkersAdminApi().Drain(id, new DrainWorkerRequest(false), default));
        Assert.False((await RunAsync(() => _h.WorkerApi(consumer, "key").Status(name, default)))["drain"].GetBoolean());
        var claimed = await RunAsync(() => _h.WorkerApi(consumer, "key").Claim(new WorkerClaimRequest(name, [type]), default));
        Assert.Equal(200, claimed.Status);
        Assert.NotNull((await ListedAsync(name)).GetProperty("lastClaimUtc").GetString());
        Assert.Equal(1, (await ListedAsync(name)).GetProperty("runningTasks").GetInt32());
    }

    [Fact]
    public async Task Another_worker_is_not_affected_by_a_drain_and_a_worker_of_another_key_with_the_same_name_is_a_different_worker()
    {
        var a = Interlocked.Increment(ref NextConsumer);
        var b = Interlocked.Increment(ref NextConsumer);
        await HandshakeAsync(a, "same");
        await HandshakeAsync(b, "same");
        var list = await RunAsync(() => _h.WorkersAdminApi().List(default));
        var first = list.Body.EnumerateArray().Single(w => w.GetProperty("apiConsumerId").GetInt32() == a).GetProperty("workerInstanceId").GetInt32();

        await RunAsync(() => _h.WorkersAdminApi().Drain(first, new DrainWorkerRequest(true), default));

        Assert.True((await RunAsync(() => _h.WorkerApi(a, "k").Status("same", default)))["drain"].GetBoolean());
        Assert.False((await RunAsync(() => _h.WorkerApi(b, "k").Status("same", default)))["drain"].GetBoolean());
    }

    [Fact]
    public async Task Forgetting_a_connected_worker_is_refused_and_a_gone_one_can_be_forgotten()
    {
        var consumer = Interlocked.Increment(ref NextConsumer);
        var name = "forget-" + consumer;
        await HandshakeAsync(consumer, name);
        var id = (await ListedAsync(name)).GetProperty("workerInstanceId").GetInt32();

        var online = await RunAsync(() => _h.WorkersAdminApi().Forget(id, default));
        Assert.Equal((422, "rule"), (online.Status, online.Code));

        await db.ExecAsync("UPDATE ductwork.WorkerInstance SET LastSeenUtc = DATEADD(MINUTE, -10, SYSUTCDATETIME()) WHERE WorkerInstanceId = @i;", ("@i", id));
        Assert.Equal("Offline", (await ListedAsync(name)).GetProperty("status").GetString());
        Assert.True((await RunAsync(() => _h.WorkersAdminApi().Forget(id, default)))["forgotten"].GetBoolean());
        Assert.Equal(404, (await RunAsync(() => _h.WorkersAdminApi().Forget(id, default))).Status);
    }

    [Fact]
    public async Task Any_user_can_see_whether_workers_are_connected_but_not_who_they_are()
    {
        var consumer = Interlocked.Increment(ref NextConsumer);
        await HandshakeAsync(consumer, "summary-" + consumer);

        var summary = await RunAsync(() => _h.CatalogApi(Viewer).WorkerSummary(default));

        Assert.True(summary["online"].GetInt32() >= 1);
        Assert.True(summary["total"].GetInt32() >= summary["online"].GetInt32());
        Assert.DoesNotContain("summary-", summary.Body.GetRawText());
    }

    // ---- creating the key a new worker needs

    [Fact]
    public async Task A_worker_key_holds_exactly_the_worker_scopes_is_shown_once_and_comes_with_ready_settings()
    {
        var reply = await RunAsync(() => _h.WorkersAdminApi().CreateKey(new CreateWorkerKeyRequest("farm-2"), default));

        Assert.Equal(201, reply.Status);
        var raw = reply["apiKey"].GetString()!;
        Assert.StartsWith("aitb_", raw);
        Assert.Equal(["ductwork:worker", "tool:DuctworkTakeoff"], reply["scopes"].EnumerateArray().Select(s => s.GetString()));

        var stored = _h.Consumers.Keys.Single(k => k.ApiConsumerId == reply["apiConsumerId"].GetInt32());
        Assert.DoesNotContain(raw, stored.KeyHash);                                   // only a hash is kept
        Assert.Contains("ductwork:worker", stored.ScopesJson);
        Assert.Null(stored.ExpiresUtc);
        Assert.Equal("Ductwork worker: farm-2", _h.Consumers.Consumers.Single(c => c.Id == stored.ApiConsumerId).Name);

        using var settings = JsonDocument.Parse(reply["settings"].GetString()!);
        var worker = settings.RootElement.GetProperty("Worker");
        Assert.Equal(("https://tools.example.com/aicompare", raw, "farm-2"), (worker.GetProperty("ServerUrl").GetString(), worker.GetProperty("ApiKey").GetString(), worker.GetProperty("WorkerName").GetString()));

        // The key that was issued is one the key service accepts, with those scopes.
        var principal = await new AIToolbox.Web.Security.ApiKeyService(_h.Consumers).ValidateAsync(raw);
        Assert.Equal(["ductwork:worker", "tool:DuctworkTakeoff"], principal!.Scopes.Order());
        Assert.True(await db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.SettingsAuditEvent WHERE Action = 'worker.key-issued';") >= 1);
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.SettingsAuditEvent WHERE AfterJson LIKE @k;", ("@k", "%" + raw + "%")));   // the key itself is never logged
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    public async Task A_bad_worker_name_makes_no_key(string name)
    {
        var before = _h.Consumers.Keys.Count;

        var reply = await RunAsync(() => _h.WorkersAdminApi().CreateKey(new CreateWorkerKeyRequest(name), default));

        Assert.Equal(400, reply.Status);
        Assert.Equal(before, _h.Consumers.Keys.Count);
    }
}
