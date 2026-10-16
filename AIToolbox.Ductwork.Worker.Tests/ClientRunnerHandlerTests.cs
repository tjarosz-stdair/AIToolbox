using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Worker;
using AIToolbox.Ductwork.Worker.Handlers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AIToolbox.Ductwork.Worker.Tests;

/// <summary>Answers requests from a script and remembers what was asked, with the body read before the request is disposed.</summary>
internal sealed class StubHttp : HttpMessageHandler
{
    public Queue<Func<HttpRequestMessage, HttpResponseMessage>> Script { get; } = new();
    public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Seen.Add((request, body));
        return Script.Count > 0 ? Script.Dequeue()(request) : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public sealed class ApiClientTests
{
    private static (DuctworkApiClient Client, StubHttp Http) Build()
    {
        var http = new StubHttp();
        var client = new DuctworkApiClient(new HttpClient(http) { BaseAddress = new Uri("https://tools.example.com/aicompare/") },
            Options.Create(new WorkerOptions { ServerUrl = "https://tools.example.com/aicompare", ApiKey = "aitb_secret", WorkerName = "w-1" }),
            [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)]);
        return (client, http);
    }

    [Fact]
    public async Task Every_request_carries_the_api_key_the_worker_name_and_goes_under_the_instance_path()
    {
        var (client, http) = Build();
        http.Script.Enqueue(_ => StubHttp.Json(HttpStatusCode.OK, """{"serverContractVersion":"0.1.0","leaseSeconds":120,"heartbeatSeconds":30,"lockHeartbeatSeconds":15,"maxResultBytes":1000}"""));

        var info = await client.HandshakeAsync(["a", "b"], default);

        Assert.Equal((120, 30), (info.LeaseSeconds, info.HeartbeatSeconds));
        var (request, body) = http.Seen.Single();
        Assert.Equal("https://tools.example.com/aicompare/api/v1/ductwork/worker/handshake", request.RequestUri!.ToString());
        Assert.Equal("aitb_secret", request.Headers.GetValues("X-AIToolbox-Api-Key").Single());
        using var json = JsonDocument.Parse(body);
        Assert.Equal(("w-1", ContractInfo.Version), (json.RootElement.GetProperty("workerName").GetString(), json.RootElement.GetProperty("contractVersion").GetString()));
    }

    [Fact]
    public async Task The_handshake_says_which_build_and_machine_this_is_and_the_drain_request_is_read_from_the_status_call()
    {
        var (client, http) = Build();
        http.Script.Enqueue(_ => StubHttp.Json(HttpStatusCode.OK, """{"serverContractVersion":"0.1.0","leaseSeconds":120,"heartbeatSeconds":30,"lockHeartbeatSeconds":15,"maxResultBytes":1000}"""));
        http.Script.Enqueue(_ => StubHttp.Json(HttpStatusCode.OK, """{"drain":true}"""));

        await client.HandshakeAsync(["a"], default);
        var drain = await client.GetDrainRequestedAsync(default);

        using var hello = JsonDocument.Parse(http.Seen[0].Body);
        Assert.False(string.IsNullOrWhiteSpace(hello.RootElement.GetProperty("workerVersion").GetString()));
        Assert.Equal(Environment.MachineName, hello.RootElement.GetProperty("hostName").GetString());
        Assert.True(hello.RootElement.GetProperty("maxConcurrency").GetInt32() >= 1);
        Assert.True(drain);
        Assert.EndsWith("/worker/status?workerName=w-1", http.Seen[1].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Nothing_to_do_is_null_and_a_task_is_read_with_its_producer()
    {
        var (client, http) = Build();
        http.Script.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        Assert.Null(await client.ClaimAsync(["a"], default));

        http.Script.Enqueue(_ => StubHttp.Json(HttpStatusCode.OK, """
            {"taskId":7,"taskPublicId":"6f1c2a52-0000-4000-8000-000000000001","jobId":3,"taskType":"a","attempt":1,"maxAttempts":3,"inputManifestRef":null,
             "contractVersion":"0.1.0","leaseExpiresUtc":"2026-10-16T12:00:00Z","producer":{"kind":"Algorithm","algorithmName":"counter","algorithmVersion":"1.0","paramsHash":"h"}}
            """));
        var task = (await client.ClaimAsync(["a"], default))!;

        Assert.Equal((7L, "a", ProducerKind.Algorithm, "counter"), (task.TaskId, task.TaskType, task.Producer.Kind, task.Producer.AlgorithmName));
    }

    [Fact]
    public async Task An_incompatible_contract_is_its_own_error_with_the_servers_version()
    {
        var (client, http) = Build();
        http.Script.Enqueue(_ => StubHttp.Json(HttpStatusCode.Conflict, """{"code":"contract-version","error":"Upgrade the worker.","serverContractVersion":"9.0.0"}"""));

        var error = await Assert.ThrowsAsync<ContractMismatchException>(() => client.HandshakeAsync([], default));

        Assert.Equal(("9.0.0", "Upgrade the worker."), (error.ServerVersion, error.Message));
    }

    [Fact]
    public async Task A_refused_result_carries_the_servers_reasons_and_is_not_retried()
    {
        var (client, http) = Build();
        http.Script.Enqueue(_ => StubHttp.Json((HttpStatusCode)422, """{"code":"result-rejected","error":"Rejected.","reasons":["Bad size.","Unknown type."]}"""));

        var error = await Assert.ThrowsAsync<ResultRejectedApiException>(() => client.SubmitResultAsync(7, "{}", null, default));

        Assert.Equal(["Bad size.", "Unknown type."], error.Reasons);
        Assert.Single(http.Seen);
    }

    [Fact]
    public async Task A_busy_server_is_asked_again_but_a_refusal_never_is()
    {
        var (client, http) = Build();
        http.Script.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        http.Script.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        http.Script.Enqueue(_ => StubHttp.Json(HttpStatusCode.OK, """{"continue":true,"cancelRequested":false}"""));
        Assert.True((await client.HeartbeatAsync(7, 1, 2, "x", default)).Continue);
        Assert.Equal(3, http.Seen.Count);

        http.Seen.Clear();
        http.Script.Enqueue(_ => StubHttp.Json(HttpStatusCode.BadRequest, """{"code":"bad-request","error":"No."}"""));
        var error = await Assert.ThrowsAsync<ApiException>(() => client.HeartbeatAsync(7, 1, 2, "x", default));
        Assert.Equal((400, false), (error.Status, error.IsTransient));
        Assert.Single(http.Seen);
    }

    [Fact]
    public async Task Giving_up_after_the_retries_surfaces_a_transient_error()
    {
        var (client, http) = Build();
        for (var i = 0; i < 5; i++) http.Script.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var error = await Assert.ThrowsAsync<ApiException>(() => client.ClaimAsync(["a"], default));

        Assert.True(error.IsTransient);
        Assert.Equal(3, http.Seen.Count);                                // the first try and two retries
    }

    [Fact]
    public async Task The_result_is_sent_as_the_raw_manifest_with_the_worker_name_in_the_query()
    {
        var (client, http) = Build();
        http.Script.Enqueue(_ => StubHttp.Json(HttpStatusCode.OK, """{"accepted":true,"cancelled":false,"entities":2,"findings":1,"buildingTypesCreated":0,"raisedAsFindings":1,"batched":1}"""));

        var reply = await client.SubmitResultAsync(7, """{"x":1}""", "results/a.json", default);

        Assert.Equal((true, 2), (reply.Accepted, reply.Entities));
        var (request, body) = http.Seen.Single();
        Assert.Equal("""{"x":1}""", body);
        Assert.Contains("workerName=w-1", request.RequestUri!.Query);
        Assert.Contains("resultRef=results%2Fa.json", request.RequestUri.Query);
    }

    [Fact]
    public async Task Images_go_to_the_server_as_base64_and_a_refusal_carries_its_reason()
    {
        var (client, http) = Build();
        http.Script.Enqueue(_ => StubHttp.Json((HttpStatusCode)403, """{"code":"vision-refused","reason":"expired","error":"The approval has expired."}"""));
        var sheet = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<VisionRefusedApiException>(() => client.SendVisionAsync(7, "classify", [sheet], [new VisionImage("crop", "image/png", [1, 2, 3])], default));

        Assert.Equal(("expired", false), (error.Reason, error.IsTransient));
        var (request, body) = http.Seen.Single();
        Assert.EndsWith("tasks/7/vision", request.RequestUri!.AbsolutePath);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), json.RootElement.GetProperty("images")[0].GetProperty("dataBase64").GetString());
        Assert.Equal(sheet, json.RootElement.GetProperty("sheetIds")[0].GetGuid());
    }

    [Fact]
    public async Task A_vision_send_is_never_repeated_on_a_server_error_because_each_send_spends_the_approved_budget()
    {
        var (client, http) = Build();
        http.Script.Enqueue(_ => StubHttp.Json(HttpStatusCode.BadGateway, """{"code":"provider-error","error":"The provider timed out."}"""));
        http.Script.Enqueue(_ => StubHttp.Json(HttpStatusCode.OK, """{"text":"ok","transmissionId":1,"imagesSent":1,"provider":"p","model":"m"}"""));

        var error = await Assert.ThrowsAsync<ApiException>(() => client.SendVisionAsync(7, "classify", [Guid.NewGuid()], [new VisionImage(null, "image/png", [1])], default));

        Assert.Equal(502, error.Status);
        Assert.Single(http.Seen);
    }

    [Fact]
    public async Task A_successful_vision_answer_is_read()
    {
        var (client, http) = Build();
        http.Script.Enqueue(_ => StubHttp.Json(HttpStatusCode.OK, """{"text":"{\"type\":\"Boot\"}","finishReason":"stop","inputTokens":5,"outputTokens":2,"transmissionId":9,"imagesSent":1,"provider":"Foundry","model":"vision-1"}"""));

        var reply = await client.SendVisionAsync(7, "classify", [Guid.NewGuid()], [new VisionImage(null, "image/png", [1])], default);

        Assert.Equal(("{\"type\":\"Boot\"}", 9L, "vision-1"), (reply.Text, reply.TransmissionId, reply.Model));
    }

    [Fact]
    public async Task A_file_is_streamed_to_the_destination()
    {
        var (client, http) = Build();
        http.Script.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) });
        await using var destination = new MemoryStream();

        await client.DownloadFileAsync(7, Guid.NewGuid(), destination, default);

        Assert.Equal([1, 2, 3, 4], destination.ToArray());
    }
}

public sealed class ProcessRunnerTests
{
    private static string Cmd => Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";

    [Fact]
    public async Task A_child_gets_only_what_it_is_given_and_never_the_workers_secrets()
    {
        if (!OperatingSystem.IsWindows()) return;
        Environment.SetEnvironmentVariable("DUCTWORK_TEST_SECRET", "hunter2");
        try
        {
            var result = await new ChildProcessRunner().RunAsync(Cmd, ["/c", "set"], Path.GetTempPath(), new Dictionary<string, string> { ["GIVEN"] = "yes" }, TimeSpan.FromSeconds(20), default);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("GIVEN=yes", result.StandardOutput);
            Assert.DoesNotContain("hunter2", result.StandardOutput);
            Assert.DoesNotContain("DUCTWORK_TEST_SECRET", result.StandardOutput);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DUCTWORK_TEST_SECRET", null);
        }
    }

    [Fact]
    public async Task Output_exit_code_and_errors_are_captured()
    {
        if (!OperatingSystem.IsWindows()) return;

        var result = await new ChildProcessRunner().RunAsync(Cmd, ["/c", "echo out& echo err 1>&2& exit 3"], Path.GetTempPath(), new Dictionary<string, string>(), TimeSpan.FromSeconds(20), default);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("out", result.StandardOutput);
        Assert.Contains("err", result.StandardError);
    }

    [Fact]
    public async Task A_child_that_runs_too_long_is_killed_and_reported_as_retryable()
    {
        if (!OperatingSystem.IsWindows()) return;

        var error = await Assert.ThrowsAsync<TaskFailedException>(() =>
            new ChildProcessRunner().RunAsync("ping", ["-n", "30", "127.0.0.1"], Path.GetTempPath(), new Dictionary<string, string>(), TimeSpan.FromMilliseconds(400), default));

        Assert.True(error.Retryable);
        Assert.Contains("did not finish", error.Message);
    }

    [Fact]
    public async Task Cancelling_kills_the_child()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ChildProcessRunner().RunAsync("ping", ["-n", "30", "127.0.0.1"], Path.GetTempPath(), new Dictionary<string, string>(), TimeSpan.FromMinutes(1), cts.Token));
    }
}

public sealed class ResidentialHandlerTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "ductwork-handler-" + Guid.NewGuid().ToString("N"));

    public ResidentialHandlerTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); }
        catch (IOException) { }
    }

    private sealed class FakeRunner(Func<IReadOnlyList<string>, ProcessResult> onRun) : IProcessRunner
    {
        public List<(string File, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env)> Runs { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string> environment,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            Runs.Add((fileName, arguments, environment));
            return Task.FromResult(onRun(arguments));
        }
    }

    private static string Value(IReadOnlyList<string> args, string flag) => args[args.ToList().IndexOf(flag) + 1];

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static readonly ProducerRef Algorithm = ProducerRef.Algorithm("residential-label-counter", "0.1.0", "params-1");

    private (TaskContext Context, FakeApi Api, Guid File) Setup(IReadOnlyList<InputSheet>? sheets = null, ProducerRef? producer = null, byte[]? served = null, string? hash = null)
    {
        var data = served ?? [37, 80, 68, 70];
        var file = Guid.NewGuid();
        var api = new FakeApi();
        api.Files[file] = data;
        api.Input = new TaskInput(0, 1, "detect-residential-labels", null, [new InputFile(file, "plan.pdf", data.Length, hash ?? Sha(data), 1, null)],
            sheets ?? [Sheet(file, 1, "M1.2"), Sheet(file, 2, "M1.3")], [new InputBuildingType(9, "Type 4")]);
        var task = api.NewTask("detect-residential-labels") with { Producer = producer ?? Algorithm };
        return (new TaskContext(task, "w", _scratch, api, NullLogger.Instance), api, file);
    }

    private static InputSheet Sheet(Guid file, int page, string number, string kind = "Plan", bool inScope = true, Guid? duplicateOf = null, long? buildingType = null) =>
        new(Guid.NewGuid(), file, page, number, "Plan", kind, inScope, 2592, 1728, 0, duplicateOf, buildingType is null ? [] : [new InputView(Guid.NewGuid(), "Plan", buildingType, null)]);

    private static ProcessResult WriteManifest(IReadOnlyList<string> args, ProducerRef? producer = null)
    {
        var detection = new DetectionProposal
        {
            DetectionId = "d1", Type = "Boot", Size = "4x10x7", Quantity = 1, Method = DetectionMethod.Text, Family = DetectorFamily.LabelFlagCount, Confidence = 0.9,
            SheetViewRef = Value(args, "--sheet-ref")
        };
        var finding = new FindingProposal { FindingId = "f1", Kind = FindingKind.GrammarVariant, Question = "Depth?", DetectionId = "d1" };
        File.WriteAllText(Value(args, "--out"), JsonSerializer.Serialize(new TaskResultManifest
        {
            ContractVersion = ContractInfo.Version, TaskType = "x", TaskPublicId = Value(args, "--task-id"), Producer = producer ?? Algorithm, Detections = [detection], Findings = [finding]
        }, ContractJson.Options));
        return new ProcessResult(0, "", "");
    }

    private ResidentialLabelsHandler Handler(FakeRunner runner, string directory = "C:\\sidecar") =>
        new(runner, Options.Create(new WorkerOptions { Sidecar = new SidecarOptions { PythonPath = "python", Directory = directory } }));

    [Fact]
    public async Task Each_in_scope_plan_sheet_is_run_in_a_child_and_the_results_are_merged_with_unique_ids()
    {
        var (context, _, _) = Setup();
        var runner = new FakeRunner(args => WriteManifest(args));

        var json = await Handler(runner).RunAsync(context, default);

        var manifest = JsonSerializer.Deserialize<TaskResultManifest>(json, ContractJson.Options)!;
        Assert.Equal(["s1-d1", "s2-d1"], manifest.Detections.Select(d => d.DetectionId));
        Assert.Equal(["s1-f1", "s2-f1"], manifest.Findings.Select(f => f.FindingId));
        Assert.Equal(["s1-d1", "s2-d1"], manifest.Findings.Select(f => f.DetectionId));
        Assert.Equal((context.Task.TaskPublicId.ToString("D"), "detect-residential-labels", Algorithm.Key()), (manifest.TaskPublicId, manifest.TaskType, manifest.Producer.Key()));

        Assert.Equal(2, runner.Runs.Count);
        Assert.Equal(["0", "1"], runner.Runs.Select(r => Value(r.Args, "--page")));               // the sidecar counts pages from zero
        Assert.Equal(["M1.2", "M1.3"], runner.Runs.Select(r => Value(r.Args, "--sheet-ref")));
        Assert.All(runner.Runs, r => Assert.Equal(["PYTHONDONTWRITEBYTECODE", "PYTHONIOENCODING", "PYTHONPATH"], r.Env.Keys.Order()));   // nothing else reaches the child
        Assert.Equal((2, 2), (context.Progress.Done, context.Progress.Total));
    }

    [Fact]
    public async Task A_sheets_building_type_is_passed_to_the_detector()
    {
        var file = Guid.NewGuid();
        var (context, _, _) = Setup([Sheet(file, 1, "M1.2", buildingType: 9)]);
        var runner = new FakeRunner(args => WriteManifest(args));
        // The sheet must point at the served file.
        context.Api.GetType();
        var api = (FakeApi)context.Api;
        api.Input = api.Input with { Files = [new InputFile(file, "plan.pdf", 4, Sha([37, 80, 68, 70]), 1, null)] };
        api.Files[file] = [37, 80, 68, 70];

        await Handler(runner).RunAsync(context, default);

        Assert.Equal("Type 4", Value(runner.Runs.Single().Args, "--building"));
    }

    [Fact]
    public async Task Sheets_that_are_out_of_scope_duplicates_or_not_plans_are_not_run()
    {
        var file = Guid.NewGuid();
        var (context, api, _) = Setup([Sheet(file, 1, "M0.1", kind: "Schedule"), Sheet(file, 2, "M1.2", inScope: false), Sheet(file, 3, "M1.3", duplicateOf: Guid.NewGuid()), Sheet(file, 4, "M1.4")]);
        api.Files[file] = [37, 80, 68, 70];
        api.Input = api.Input with { Files = [new InputFile(file, "plan.pdf", 4, Sha([37, 80, 68, 70]), 1, null)] };
        var runner = new FakeRunner(args => WriteManifest(args));

        await Handler(runner).RunAsync(context, default);

        Assert.Equal(["M1.4"], runner.Runs.Select(r => Value(r.Args, "--sheet-ref")));
    }

    [Fact]
    public async Task No_eligible_sheets_is_a_clear_non_retryable_failure()
    {
        var file = Guid.NewGuid();
        var (context, _, _) = Setup([Sheet(file, 1, "M1.2", inScope: false)]);

        var error = await Assert.ThrowsAsync<TaskFailedException>(() => Handler(new FakeRunner(_ => new ProcessResult(0, "", ""))).RunAsync(context, default));

        Assert.False(error.Retryable);
        Assert.Contains("no in-scope plan sheets", error.Message);
    }

    [Fact]
    public async Task A_detector_that_identifies_as_another_producer_is_caught_before_the_server_would_refuse_everything()
    {
        var (context, _, _) = Setup();
        var runner = new FakeRunner(args => WriteManifest(args, ProducerRef.Algorithm("some-other-detector", "9.9", "x")));

        var error = await Assert.ThrowsAsync<TaskFailedException>(() => Handler(runner).RunAsync(context, default));

        Assert.False(error.Retryable);
        Assert.Contains("identifies itself differently", error.Message);
    }

    [Fact]
    public async Task A_detector_that_exits_badly_or_writes_rubbish_fails_with_what_it_said()
    {
        var (context, _, _) = Setup();
        var bad = await Assert.ThrowsAsync<TaskFailedException>(() => Handler(new FakeRunner(_ => new ProcessResult(1, "", "Traceback: boom"))).RunAsync(context, default));
        Assert.Contains("Traceback: boom", bad.Message);

        var (context2, _, _) = Setup();
        var rubbish = new FakeRunner(args => { File.WriteAllText(Value(args, "--out"), "not json"); return new ProcessResult(0, "", ""); });
        var unreadable = await Assert.ThrowsAsync<TaskFailedException>(() => Handler(rubbish).RunAsync(context2, default));
        Assert.Contains("could not be read", unreadable.Message);
    }

    [Fact]
    public async Task A_missing_sidecar_directory_is_a_configuration_failure()
    {
        var (context, _, _) = Setup();

        var error = await Assert.ThrowsAsync<TaskFailedException>(() => Handler(new FakeRunner(_ => new ProcessResult(0, "", "")), directory: "").RunAsync(context, default));

        Assert.False(error.Retryable);
        Assert.Contains("Sidecar:Directory", error.Message);
    }

    [Fact]
    public async Task A_downloaded_file_that_does_not_match_its_checksum_is_discarded()
    {
        var file = Guid.NewGuid();
        var (context, api, _) = Setup([Sheet(file, 1, "M1.2")], served: [1, 2, 3], hash: new string('0', 64));
        api.Files[file] = [1, 2, 3];
        api.Input = api.Input with { Files = [new InputFile(file, "plan.pdf", 3, new string('0', 64), 1, null)] };

        var error = await Assert.ThrowsAsync<TaskFailedException>(() => Handler(new FakeRunner(_ => new ProcessResult(0, "", ""))).RunAsync(context, default));

        Assert.True(error.Retryable);
        Assert.Contains("checksum", error.Message);
        Assert.Empty(Directory.GetFiles(_scratch, "*.pdf"));
    }
}

public sealed class FakeHandlerTests
{
    [Fact]
    public async Task The_fake_handler_produces_a_manifest_attributed_to_the_tasks_own_producer()
    {
        var api = new FakeApi();
        var task = api.NewTask("fake-detect");
        var context = new TaskContext(task, "w", Path.GetTempPath(), api, NullLogger.Instance);

        var json = await new FakeDetectHandler().RunAsync(context, default);

        var manifest = JsonSerializer.Deserialize<TaskResultManifest>(json, ContractJson.Options)!;
        Assert.Equal(6, manifest.Detections.Count);
        Assert.Equal((task.TaskPublicId.ToString("D"), task.Producer.Key()), (manifest.TaskPublicId, manifest.Producer.Key()));
        Assert.Equal(6, context.Progress.Done);
    }
}
