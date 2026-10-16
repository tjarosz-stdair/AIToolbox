using AIToolbox.Ductwork.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AIToolbox.Ductwork.Worker;

/// <summary>
/// The worker's loop. It introduces itself to the server (and refuses to claim anything if the contract versions are
/// incompatible), then keeps up to <see cref="WorkerOptions.MaxConcurrency"/> tasks running: each in its own scratch
/// folder, each with a heartbeat that keeps its lease and learns whether to carry on, each reporting a result or a failure.
/// It can be drained (finish what it has, take nothing new) without losing work.
/// </summary>
public sealed class WorkerService : BackgroundService
{
    private readonly IDuctworkApi _api;
    private readonly HandlerRegistry _handlers;
    private readonly WorkerOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<WorkerService> _logger;
    private HandshakeInfo _server = new(ContractInfo.Version, 120, 30, 15, 100L * 1024 * 1024);

    public WorkerService(IDuctworkApi api, HandlerRegistry handlers, IOptions<WorkerOptions> options, IHostApplicationLifetime lifetime, ILogger<WorkerService> logger)
    {
        _api = api;
        _handlers = handlers;
        _options = options.Value;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>Set by tests to run the loop quickly.</summary>
    internal TimeSpan? HeartbeatOverride { get; set; }

    private string[] Types => _options.TaskTypes.Length == 0
        ? _handlers.TaskTypes.ToArray()
        : _options.TaskTypes.Where(t => _handlers.Find(t) is not null).ToArray();

    private string DrainPath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_options.ScratchRoot.TrimEnd('/', '\\'))) ?? ".", _options.DrainFileName);

    private bool _serverDrain;

    /// <summary>Told to drain by the local drain file or by an administrator on the server.</summary>
    private bool Draining => _serverDrain || File.Exists(DrainPath);

    private async Task RefreshServerDrainAsync(CancellationToken ct)
    {
        try { _serverDrain = await _api.GetDrainRequestedAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is ApiException or HttpRequestException) { _logger.LogDebug("Could not check the drain request: {Message}", ex.Message); }   // keep the last known answer
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Types.Length == 0)
        {
            _logger.LogError("This worker has no handler for any of the task types it is configured to take, so there is nothing for it to do.");
            _lifetime.StopApplication();
            return;
        }

        Directory.CreateDirectory(_options.ScratchRoot);
        SweepScratch();

        if (!await HandshakeAsync(stoppingToken)) return;

        var running = new Dictionary<Task, long>();
        var concurrency = Math.Max(1, _options.MaxConcurrency);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var done in running.Keys.Where(t => t.IsCompleted).ToList()) running.Remove(done);

                await RefreshServerDrainAsync(stoppingToken);
                if (Draining)
                {
                    if (running.Count == 0 && _options.ExitWhenDrained)
                    {
                        _logger.LogInformation("Drained and idle: stopping.");
                        _lifetime.StopApplication();
                        return;
                    }
                }
                else if (running.Count < concurrency)
                {
                    ClaimedTask? claimed = null;
                    try { claimed = await _api.ClaimAsync(Types, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    catch (ApiException ex) { _logger.LogWarning("Could not ask for work: {Message}", ex.Message); }
                    catch (HttpRequestException ex) { _logger.LogWarning("The server could not be reached: {Message}", ex.Message); }

                    if (claimed is not null)
                    {
                        _logger.LogInformation("Claimed task {TaskId} ({TaskType}), attempt {Attempt} of {Max}.", claimed.TaskId, claimed.TaskType, claimed.Attempt, claimed.MaxAttempts);
                        running[RunTaskAsync(claimed, stoppingToken)] = claimed.TaskId;
                        continue;                                           // there may be more waiting
                    }
                }

                // Wait for the next poll, or for a running task to finish (which frees a slot sooner).
                var pause = Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds)), stoppingToken);
                try { await Task.WhenAny(running.Keys.Append(pause)); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            if (running.Count > 0)
            {
                _logger.LogInformation("Stopping with {Count} task(s) in progress; they will be handed back to the queue.", running.Count);
                try { await Task.WhenAll(running.Keys); }
                catch (Exception ex) { _logger.LogDebug(ex, "A task ended with an error while stopping."); }
            }
        }
    }

    private async Task<bool> HandshakeAsync(CancellationToken ct)
    {
        for (var attempt = 0; !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                _server = await _api.HandshakeAsync(Types, ct);
                _logger.LogInformation("Connected. Server contract {Contract}, lease {Lease}s, heartbeat every {Beat}s. Taking: {Types}.",
                    _server.ServerContractVersion, _server.LeaseSeconds, _server.HeartbeatSeconds, string.Join(", ", Types));
                return true;
            }
            catch (ContractMismatchException ex)
            {
                // Retrying cannot fix this: one side must be upgraded. Stay out of the queue rather than claim work that would be refused.
                _logger.LogCritical("{Message} This worker speaks contract {Mine}; the server speaks {Theirs}. Not claiming any work.", ex.Message, ContractInfo.Version, ex.ServerVersion);
                _lifetime.StopApplication();
                return false;
            }
            catch (Exception ex) when (ex is ApiException or HttpRequestException)
            {
                var wait = TimeSpan.FromSeconds(Math.Min(60, 2 << Math.Min(attempt, 5)));
                _logger.LogWarning("Could not reach the server ({Message}). Trying again in {Wait}s.", ex.Message, wait.TotalSeconds);
                try { await Task.Delay(wait, ct); }
                catch (OperationCanceledException) { return false; }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    private async Task RunTaskAsync(ClaimedTask task, CancellationToken hostStopping)
    {
        var scratch = Path.Combine(_options.ScratchRoot, $"task-{task.TaskId}-{task.Attempt}");
        using var taskStop = CancellationTokenSource.CreateLinkedTokenSource(hostStopping);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(Math.Max(1, _options.TaskTimeoutMinutes)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(taskStop.Token, deadline.Token);
        var serverEnded = false;
        Task? heartbeat = null;
        TaskContext? context = null;

        try
        {
            Directory.CreateDirectory(scratch);
            var handler = _handlers.Find(task.TaskType);
            if (handler is null)
            {
                await ReportFailureAsync(task, $"This worker has no handler for task type '{task.TaskType}'.", retryable: false);
                return;
            }

            context = new TaskContext(task, _options.WorkerName, scratch, _api, _logger);
            heartbeat = HeartbeatLoopAsync(task, context, taskStop, () => serverEnded = true, linked.Token);

            string manifest;
            try
            {
                manifest = await handler.RunAsync(context, linked.Token);
            }
            finally
            {
                await taskStop.CancelAsync();                        // ends the heartbeat loop
            }

            if (serverEnded)
            {
                _logger.LogInformation("Task {TaskId} was cancelled or taken back by the server; its result was not sent.", task.TaskId);
                return;
            }

            var reply = await _api.SubmitResultAsync(task.TaskId, manifest, null, hostStopping.IsCancellationRequested ? CancellationToken.None : hostStopping);
            _logger.LogInformation("Task {TaskId} done: {Entities} item(s), {Findings} finding(s){Cancelled}.", task.TaskId, reply.Entities, reply.Findings, reply.Cancelled ? " (the server had cancelled it)" : "");
        }
        catch (OperationCanceledException) when (serverEnded)
        {
            _logger.LogInformation("Task {TaskId} stopped: the server cancelled it or no longer holds it for this worker.", task.TaskId);
        }
        catch (OperationCanceledException) when (hostStopping.IsCancellationRequested)
        {
            await ReportFailureAsync(task, "The worker was stopped before the task finished.", retryable: true);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            await ReportFailureAsync(task, $"The task did not finish within {_options.TaskTimeoutMinutes} minutes.", retryable: true);
        }
        catch (ResultRejectedApiException ex)
        {
            await ReportFailureAsync(task, $"The server refused the result: {string.Join(" ", ex.Reasons.Take(5))}", retryable: false);
        }
        catch (TaskFailedException ex)
        {
            await ReportFailureAsync(task, ex.Message, ex.Retryable);
        }
        catch (ApiException ex) when (!serverEnded)
        {
            await ReportFailureAsync(task, $"The server refused a request: {ex.Message}", retryable: ex.IsTransient);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Task {TaskId} failed.", task.TaskId);
            await ReportFailureAsync(task, $"{ex.GetType().Name}: {ex.Message}", retryable: true);
        }
        finally
        {
            await taskStop.CancelAsync();
            if (heartbeat is not null)
            {
                try { await heartbeat; }
                catch (Exception ex) { _logger.LogDebug(ex, "The heartbeat for task {TaskId} ended with an error.", task.TaskId); }
            }

            DeleteScratch(scratch);
        }
    }

    private async Task HeartbeatLoopAsync(ClaimedTask task, TaskContext context, CancellationTokenSource stop, Action serverEnded, CancellationToken ct)
    {
        var every = HeartbeatOverride ?? TimeSpan.FromSeconds(Math.Max(1, _server.HeartbeatSeconds));
        while (!stop.IsCancellationRequested)
        {
            try { await Task.Delay(every, stop.Token); }
            catch (OperationCanceledException) { return; }

            try
            {
                var (done, total, message) = context.Progress;
                var reply = await _api.HeartbeatAsync(task.TaskId, done, total, message, CancellationToken.None);
                if (!reply.Continue)
                {
                    serverEnded();
                    await stop.CancelAsync();
                    return;
                }
            }
            catch (Exception ex) when (ex is ApiException or HttpRequestException)
            {
                // A missed heartbeat is not fatal. If the lease runs out the server will say so on the next one that gets through.
                _logger.LogWarning("Heartbeat for task {TaskId} failed: {Message}", task.TaskId, ex.Message);
            }
        }
    }

    private async Task ReportFailureAsync(ClaimedTask task, string error, bool retryable)
    {
        _logger.LogWarning("Task {TaskId} failed ({Retry}): {Error}", task.TaskId, retryable ? "will retry if attempts remain" : "will not retry", error);
        try
        {
            // Best effort and not tied to the host's stopping token: handing the task back is exactly what a stopping worker should do.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _api.FailAsync(task.TaskId, error.Length > 1900 ? error[..1900] : error, retryable, timeout.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not report the failure of task {TaskId}; its lease will run out and the task will be handed back: {Message}", task.TaskId, ex.Message);
        }
    }

    /// <summary>A crash can leave scratch folders behind. Nothing in scratch is needed after a restart, because every task starts again from the server.</summary>
    private void SweepScratch()
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(_options.ScratchRoot, "task-*")) DeleteScratch(directory);
        }
        catch (IOException ex)
        {
            _logger.LogWarning("Could not clean the scratch folder: {Message}", ex.Message);
        }
    }

    private void DeleteScratch(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not remove scratch folder {Path}: {Message}", path, ex.Message);
        }
    }
}
