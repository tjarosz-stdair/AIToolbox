using System.Collections.Concurrent;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Worker;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AIToolbox.Ductwork.Worker.Tests;

internal sealed class FakeApi : IDuctworkApi
{
    private long _next = 100;

    public ConcurrentQueue<ClaimedTask> Queue { get; } = new();
    public ConcurrentQueue<(long Id, string Json)> Results { get; } = new();
    public ConcurrentQueue<(long Id, string Error, bool Retryable)> Failures { get; } = new();
    public ConcurrentQueue<long> Heartbeats { get; } = new();
    public ConcurrentQueue<string> Claims { get; } = new();
    public Func<long, HeartbeatReply> OnHeartbeat { get; set; } = _ => new(true, false);
    public Exception? HandshakeError { get; set; }
    public Exception? SubmitError { get; set; }
    public TaskInput Input { get; set; } = new(0, 0, "x", null, [], [], []);
    public Dictionary<Guid, byte[]> Files { get; } = [];
    public int HandshakeCalls;
    public bool ServerDrain { get; set; }

    public Task<bool> GetDrainRequestedAsync(CancellationToken ct) => Task.FromResult(ServerDrain);

    public Task<HandshakeInfo> HandshakeAsync(IReadOnlyCollection<string> taskTypes, CancellationToken ct)
    {
        Interlocked.Increment(ref HandshakeCalls);
        if (HandshakeError is not null) throw HandshakeError;
        return Task.FromResult(new HandshakeInfo(ContractInfo.Version, 120, 1, 15, 100_000_000));
    }

    public Task<ClaimedTask?> ClaimAsync(IReadOnlyCollection<string> taskTypes, CancellationToken ct)
    {
        Claims.Enqueue(string.Join(",", taskTypes));
        return Task.FromResult(Queue.TryDequeue(out var task) ? task : null);
    }

    public Task<HeartbeatReply> HeartbeatAsync(long taskId, int done, int? total, string? message, CancellationToken ct)
    {
        Heartbeats.Enqueue(taskId);
        return Task.FromResult(OnHeartbeat(taskId));
    }

    public Task<TaskInput> GetInputAsync(long taskId, CancellationToken ct) => Task.FromResult(Input with { TaskId = taskId });

    public async Task DownloadFileAsync(long taskId, Guid fileId, Stream destination, CancellationToken ct) =>
        await destination.WriteAsync(Files[fileId], ct);

    public Task<RegisterSheetsReply> RegisterSheetsAsync(long taskId, Guid fileId, IReadOnlyList<PageReport> pages, CancellationToken ct) =>
        Task.FromResult(new RegisterSheetsReply(pages.Count, 0));

    public Func<IReadOnlyList<VisionImage>, VisionReply>? OnVision { get; set; }

    public Task<VisionReply> SendVisionAsync(long taskId, string directive, IReadOnlyList<Guid> sheetIds, IReadOnlyList<VisionImage> images, CancellationToken ct) =>
        Task.FromResult((OnVision ?? throw new VisionRefusedApiException("disabled", "Vision is off."))(images));

    public Task<SubmitReply> SubmitResultAsync(long taskId, string manifestJson, string? resultRef, CancellationToken ct)
    {
        if (SubmitError is not null) throw SubmitError;
        Results.Enqueue((taskId, manifestJson));
        return Task.FromResult(new SubmitReply(true, false, 1, 0, 0, 0, 1));
    }

    public Task FailAsync(long taskId, string error, bool retryable, CancellationToken ct)
    {
        Failures.Enqueue((taskId, error, retryable));
        return Task.CompletedTask;
    }

    public ClaimedTask NewTask(string type = "test", Guid? publicId = null) => new(Interlocked.Increment(ref _next), publicId ?? Guid.NewGuid(), 1, type, 1, 3, null,
        ContractInfo.Version, DateTime.UtcNow.AddMinutes(2), ProducerRef.Algorithm("test-algo", "1.0", "h"));
}

internal sealed class LambdaHandler(string type, Func<TaskContext, CancellationToken, Task<string>> run) : ITaskHandler
{
    public string TaskType => type;
    public Task<string> RunAsync(TaskContext context, CancellationToken cancellationToken) => run(context, cancellationToken);
}

internal sealed class TestLifetime : IHostApplicationLifetime
{
    private readonly CancellationTokenSource _stopped = new();
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => _stopped.Token;
    public CancellationToken ApplicationStopped => _stopped.Token;
    public bool StopRequested => _stopped.IsCancellationRequested;
    public void StopApplication() => _stopped.Cancel();
}

public sealed class WorkerLoopTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ductwork-worker-" + Guid.NewGuid().ToString("N"));
    private readonly List<WorkerService> _services = [];

    private string Scratch => Path.Combine(_root, "scratch");

    public void Dispose()
    {
        foreach (var service in _services) service.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private (WorkerService Service, TestLifetime Lifetime) Build(FakeApi api, ITaskHandler[] handlers, Action<WorkerOptions>? configure = null)
    {
        var options = new WorkerOptions { ServerUrl = "http://x", ApiKey = "k", WorkerName = "test-worker", ScratchRoot = Scratch, PollSeconds = 1, MaxConcurrency = 2 };
        configure?.Invoke(options);
        var lifetime = new TestLifetime();
        var service = new WorkerService(api, new HandlerRegistry(handlers), Options.Create(options), lifetime, NullLogger<WorkerService>.Instance)
        {
            HeartbeatOverride = TimeSpan.FromMilliseconds(40)
        };
        _services.Add(service);
        return (service, lifetime);
    }

    private static async Task EventuallyAsync(Func<bool> condition, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(20);
        }
    }

    private static Task<string> OkManifest(TaskContext c) => Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new TaskResultManifest
    {
        ContractVersion = ContractInfo.Version, TaskType = c.Task.TaskType, TaskPublicId = c.Task.TaskPublicId.ToString("D"), Producer = c.Task.Producer, Detections = [], Findings = []
    }, ContractJson.Options));

    // ---- the happy path

    [Fact]
    public async Task A_claimed_task_is_run_in_its_own_scratch_folder_reported_and_cleaned_up()
    {
        var api = new FakeApi();
        var task = api.NewTask();
        api.Queue.Enqueue(task);
        string? seenScratch = null;
        var handler = new LambdaHandler("test", async (c, ct) =>
        {
            seenScratch = c.ScratchDirectory;
            Assert.True(Directory.Exists(c.ScratchDirectory));
            await File.WriteAllTextAsync(Path.Combine(c.ScratchDirectory, "work.txt"), "x", ct);
            await Task.Delay(150, ct);                                   // long enough for heartbeats
            return await OkManifest(c);
        });
        var (service, _) = Build(api, [handler]);

        await service.StartAsync(CancellationToken.None);
        await EventuallyAsync(() => api.Results.Count == 1);
        await service.StopAsync(CancellationToken.None);

        var (id, json) = api.Results.Single();
        Assert.Equal(task.TaskId, id);
        Assert.Contains(task.TaskPublicId.ToString("D"), json);
        Assert.True(api.Heartbeats.Count >= 2);
        Assert.Empty(api.Failures);
        Assert.NotNull(seenScratch);
        await EventuallyAsync(() => !Directory.Exists(seenScratch!));
        Assert.Contains("test", api.Claims.First());
    }

    [Fact]
    public async Task Handlers_see_the_task_progress_is_reported_in_heartbeats()
    {
        var api = new FakeApi();
        api.Queue.Enqueue(api.NewTask());
        var handler = new LambdaHandler("test", async (c, ct) =>
        {
            c.ReportProgress(3, 10, "working");
            await Task.Delay(150, ct);
            return await OkManifest(c);
        });
        var (service, _) = Build(api, [handler]);

        await service.StartAsync(CancellationToken.None);
        await EventuallyAsync(() => api.Results.Count == 1);
        await service.StopAsync(CancellationToken.None);

        Assert.NotEmpty(api.Heartbeats);
    }

    // ---- failures

    [Fact]
    public async Task A_handler_that_throws_reports_a_retryable_failure_and_the_worker_carries_on()
    {
        var api = new FakeApi();
        api.Queue.Enqueue(api.NewTask());
        api.Queue.Enqueue(api.NewTask());
        var calls = 0;
        var handler = new LambdaHandler("test", (c, ct) => Interlocked.Increment(ref calls) == 1 ? throw new InvalidOperationException("boom") : OkManifest(c));
        var (service, _) = Build(api, [handler]);

        await service.StartAsync(CancellationToken.None);
        await EventuallyAsync(() => api.Results.Count == 1 && api.Failures.Count == 1);
        await service.StopAsync(CancellationToken.None);

        var failure = api.Failures.Single();
        Assert.True(failure.Retryable);
        Assert.Contains("boom", failure.Error);
    }

    [Fact]
    public async Task A_handler_can_say_a_failure_is_not_worth_retrying()
    {
        var api = new FakeApi();
        api.Queue.Enqueue(api.NewTask());
        var (service, _) = Build(api, [new LambdaHandler("test", (c, ct) => throw new TaskFailedException("The drawing is corrupt.", retryable: false))]);

        await service.StartAsync(CancellationToken.None);
        await EventuallyAsync(() => api.Failures.Count == 1);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(("The drawing is corrupt.", false), (api.Failures.Single().Error, api.Failures.Single().Retryable));
    }

    [Fact]
    public async Task A_task_type_with_no_handler_fails_without_retry()
    {
        var api = new FakeApi();
        api.Queue.Enqueue(api.NewTask("mystery"));
        var (service, _) = Build(api, [new LambdaHandler("test", (c, ct) => OkManifest(c))], o => o.TaskTypes = []);

        await service.StartAsync(CancellationToken.None);
        await EventuallyAsync(() => api.Failures.Count == 1);
        await service.StopAsync(CancellationToken.None);

        Assert.False(api.Failures.Single().Retryable);
        Assert.Contains("no handler", api.Failures.Single().Error);
    }

    [Fact]
    public async Task A_result_the_server_refuses_is_reported_as_a_failure_with_its_reasons_and_not_retried()
    {
        var api = new FakeApi { SubmitError = new ResultRejectedApiException(["Detection d1 has an unknown type."], "refused") };
        api.Queue.Enqueue(api.NewTask());
        var (service, _) = Build(api, [new LambdaHandler("test", (c, ct) => OkManifest(c))]);

        await service.StartAsync(CancellationToken.None);
        await EventuallyAsync(() => api.Failures.Count == 1);
        await service.StopAsync(CancellationToken.None);

        Assert.False(api.Failures.Single().Retryable);
        Assert.Contains("unknown type", api.Failures.Single().Error);
    }

    [Fact]
    public async Task A_task_in_progress_when_the_worker_stops_is_handed_back_for_retry()
    {
        var api = new FakeApi();
        api.Queue.Enqueue(api.NewTask());
        var handler = new LambdaHandler("test", async (c, ct) => { await Task.Delay(TimeSpan.FromMinutes(5), ct); return ""; });
        var (service, _) = Build(api, [handler], o => o.TaskTimeoutMinutes = 1);

        await service.StartAsync(CancellationToken.None);
        await EventuallyAsync(() => api.Claims.Count > 0);
        await service.StopAsync(CancellationToken.None);                  // stopping is the other way a long task ends

        var failure = api.Failures.Single();
        Assert.True(failure.Retryable);
        Assert.Contains("stopped", failure.Error);
    }

    // ---- the server ending a task

    [Fact]
    public async Task When_the_server_says_stop_the_handler_is_cancelled_and_no_result_is_sent()
    {
        var api = new FakeApi { OnHeartbeat = _ => new HeartbeatReply(false, true) };
        api.Queue.Enqueue(api.NewTask());
        var cancelled = new TaskCompletionSource();
        var handler = new LambdaHandler("test", async (c, ct) =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { cancelled.SetResult(); throw; }
            return "";
        });
        var (service, _) = Build(api, [handler]);

        await service.StartAsync(CancellationToken.None);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(api.Results);
        Assert.Empty(api.Failures);
    }

    [Fact]
    public async Task A_cancel_that_arrives_just_as_the_handler_finishes_still_sends_no_result()
    {
        var api = new FakeApi { OnHeartbeat = _ => new HeartbeatReply(false, true) };
        api.Queue.Enqueue(api.NewTask());
        var handler = new LambdaHandler("test", async (c, ct) =>
        {
            await Task.Delay(300);                                        // ignores cancellation, like a handler stuck in native code
            return await OkManifest(c);
        });
        var (service, _) = Build(api, [handler]);

        await service.StartAsync(CancellationToken.None);
        await EventuallyAsync(() => api.Heartbeats.Count >= 1);
        await Task.Delay(500);
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(api.Results);
    }

    // ---- limits

    [Fact]
    public async Task No_more_than_the_configured_number_of_tasks_run_at_once()
    {
        var api = new FakeApi();
        for (var i = 0; i < 5; i++) api.Queue.Enqueue(api.NewTask());
        int current = 0, peak = 0;
        var handler = new LambdaHandler("test", async (c, ct) =>
        {
            var now = Interlocked.Increment(ref current);
            int seen;
            while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
            await Task.Delay(120, ct);
            Interlocked.Decrement(ref current);
            return await OkManifest(c);
        });
        var (service, _) = Build(api, [handler], o => o.MaxConcurrency = 2);

        await service.StartAsync(CancellationToken.None);
        await EventuallyAsync(() => api.Results.Count == 5);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(2, peak);
    }

    [Fact]
    public async Task While_draining_nothing_new_is_claimed_and_the_worker_stops_once_idle()
    {
        var api = new FakeApi();
        api.Queue.Enqueue(api.NewTask());
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "drain"), "");
        var (service, lifetime) = Build(api, [new LambdaHandler("test", (c, ct) => OkManifest(c))], o => o.ExitWhenDrained = true);

        await service.StartAsync(CancellationToken.None);
        await EventuallyAsync(() => lifetime.StopRequested);
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(api.Claims);
        Assert.Single(api.Queue);                                          // the waiting task was left for someone else
    }

    [Fact]
    public async Task An_administrator_asking_for_a_drain_has_the_same_effect_as_the_drain_file()
    {
        var api = new FakeApi { ServerDrain = true };
        api.Queue.Enqueue(api.NewTask());
        var (service, lifetime) = Build(api, [new LambdaHandler("test", (c, ct) => OkManifest(c))], o => o.ExitWhenDrained = true);

        await service.StartAsync(CancellationToken.None);
        await EventuallyAsync(() => lifetime.StopRequested);
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(api.Claims);
        Assert.Single(api.Queue);
    }

    [Fact]
    public async Task Draining_lets_a_running_task_finish_before_the_worker_stops()
    {
        var api = new FakeApi();
        api.Queue.Enqueue(api.NewTask());
        api.Queue.Enqueue(api.NewTask());
        var started = new TaskCompletionSource();
        var handler = new LambdaHandler("test", async (c, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(400, ct);
            return await OkManifest(c);
        });
        var (service, lifetime) = Build(api, [handler], o => { o.ExitWhenDrained = true; o.MaxConcurrency = 1; });

        await service.StartAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "drain"), "");
        await EventuallyAsync(() => lifetime.StopRequested);
        await service.StopAsync(CancellationToken.None);

        Assert.Single(api.Results);
        Assert.Single(api.Queue);
    }

    // ---- connecting

    [Fact]
    public async Task An_incompatible_contract_version_stops_the_worker_before_it_claims_anything()
    {
        var api = new FakeApi { HandshakeError = new ContractMismatchException("9.0.0", "This server speaks contract 9.0.0.") };
        api.Queue.Enqueue(api.NewTask());
        var (service, lifetime) = Build(api, [new LambdaHandler("test", (c, ct) => OkManifest(c))]);

        await service.StartAsync(CancellationToken.None);
        await EventuallyAsync(() => lifetime.StopRequested);
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(api.Claims);
    }

    [Fact]
    public async Task A_worker_with_no_matching_handler_for_its_configured_types_stops()
    {
        var api = new FakeApi();
        var (service, lifetime) = Build(api, [new LambdaHandler("test", (c, ct) => OkManifest(c))], o => o.TaskTypes = ["something-else"]);

        await service.StartAsync(CancellationToken.None);
        await EventuallyAsync(() => lifetime.StopRequested);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, api.HandshakeCalls);
    }

    [Fact]
    public async Task Only_the_configured_types_the_worker_has_handlers_for_are_asked_for()
    {
        var api = new FakeApi();
        var (service, _) = Build(api, [new LambdaHandler("a", (c, ct) => OkManifest(c)), new LambdaHandler("b", (c, ct) => OkManifest(c))], o => o.TaskTypes = ["b", "nope"]);

        await service.StartAsync(CancellationToken.None);
        await EventuallyAsync(() => api.Claims.Count > 0);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal("b", api.Claims.First());
    }

    [Fact]
    public async Task Scratch_folders_left_by_a_crash_are_swept_at_start()
    {
        var api = new FakeApi();
        var stale = Path.Combine(Scratch, "task-1-1");
        Directory.CreateDirectory(stale);
        await File.WriteAllTextAsync(Path.Combine(stale, "old.txt"), "x");
        var (service, _) = Build(api, [new LambdaHandler("test", (c, ct) => OkManifest(c))]);

        await service.StartAsync(CancellationToken.None);
        await EventuallyAsync(() => api.Claims.Count > 0);
        await service.StopAsync(CancellationToken.None);

        Assert.False(Directory.Exists(stale));
    }
}
