using System.Security.Cryptography;

namespace AIToolbox.Ductwork.Worker;

/// <summary>Does the work for one type of task and returns the result manifest as JSON. It runs inside a scratch folder it owns for the length of the task.</summary>
public interface ITaskHandler
{
    string TaskType { get; }

    Task<string> RunAsync(TaskContext context, CancellationToken cancellationToken);
}

/// <summary>A handler failed in a way that is not worth trying again, or is: it says which.</summary>
public sealed class TaskFailedException(string message, bool retryable, Exception? inner = null) : Exception(message, inner)
{
    public bool Retryable { get; } = retryable;
}

/// <summary>What a handler is given: the claimed task, a private scratch folder, and the server (never a database or a key).</summary>
public sealed class TaskContext
{
    private TaskInput? _input;

    public TaskContext(ClaimedTask task, string workerName, string scratchDirectory, IDuctworkApi api, ILogger logger)
    {
        Task = task;
        WorkerName = workerName;
        ScratchDirectory = scratchDirectory;
        Api = api;
        Logger = logger;
    }

    public ClaimedTask Task { get; }
    public string WorkerName { get; }
    public string ScratchDirectory { get; }
    public IDuctworkApi Api { get; }
    public ILogger Logger { get; }

    /// <summary>Progress the next heartbeat will report.</summary>
    public (int Done, int? Total, string? Message) Progress { get; private set; }

    /// <summary>
    /// Asks the server to show images to the hosted model this task was approved for. The server decides whether it may: a refusal is a
    /// <see cref="VisionRefusedApiException"/>, and the task should end rather than try another way to read the page.
    /// </summary>
    public Task<VisionReply> SendVisionAsync(string directive, IReadOnlyList<Guid> sheetIds, IReadOnlyList<VisionImage> images, CancellationToken ct) =>
        Api.SendVisionAsync(Task.TaskId, directive, sheetIds, images, ct);

    public void ReportProgress(int done, int? total = null, string? message = null) => Progress = (done, total, message);

    /// <summary>What the task is about. Asked once per task, and again after the task changes it (by recording sheets).</summary>
    public async Task<TaskInput> GetInputAsync(CancellationToken ct) => _input ??= await Api.GetInputAsync(Task.TaskId, ct);

    public async Task<TaskInput> RefreshInputAsync(CancellationToken ct) => _input = await Api.GetInputAsync(Task.TaskId, ct);

    /// <summary>
    /// Downloads a drawing file into the scratch folder and checks it against the hash the server holds, so a damaged or
    /// substituted file is caught before any work is done on it.
    /// </summary>
    public async Task<string> DownloadFileAsync(InputFile file, CancellationToken ct)
    {
        var path = Path.Combine(ScratchDirectory, file.FileId.ToString("N") + ".pdf");
        if (File.Exists(path)) return path;

        await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await Api.DownloadFileAsync(Task.TaskId, file.FileId, stream, ct);

        await using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(read, ct)).ToLowerInvariant();
        if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            read.Close();
            File.Delete(path);
            throw new TaskFailedException($"The downloaded file \"{file.FileName}\" does not match its checksum, so it was discarded.", retryable: true);
        }

        return path;
    }
}

public sealed class HandlerRegistry
{
    private readonly Dictionary<string, ITaskHandler> _handlers;

    public HandlerRegistry(IEnumerable<ITaskHandler> handlers) =>
        _handlers = handlers.ToDictionary(h => h.TaskType, StringComparer.Ordinal);

    public IReadOnlyCollection<string> TaskTypes => _handlers.Keys;

    public ITaskHandler? Find(string taskType) => _handlers.GetValueOrDefault(taskType);
}
