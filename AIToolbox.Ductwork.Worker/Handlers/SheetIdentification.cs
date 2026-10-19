using System.Text.Json;
using AIToolbox.Ductwork.Contracts;
using Microsoft.Extensions.Options;

namespace AIToolbox.Ductwork.Worker.Handlers;

/// <summary>
/// Reads every page of the job's drawing files to say which sheet each is (number, title, discipline, kind) and what its content hashes to, and
/// records them with the server. The reading is the Python sidecar's, in a child process given one PDF and one output path; this only runs it,
/// checks what comes back, and sends it. The server groups pages with the same hash, so a sheet that repeats across files is read once.
/// </summary>
public sealed class SheetIdentification(IProcessRunner runner, IOptions<WorkerOptions> options)
{
    public bool IsAvailable => !string.IsNullOrWhiteSpace(options.Value.Sidecar.Directory);

    /// <summary>
    /// Identifies and records the pages of each file that are not recorded yet. Safe to repeat: pages already recorded are left alone, so a retry after
    /// a failure does the rest. Returns how many pages were recorded.
    /// </summary>
    public async Task<int> RecordMissingAsync(TaskContext context, CancellationToken ct, string? expectedProducer = null)
    {
        var sidecar = options.Value.Sidecar;
        if (!IsAvailable)
            throw new TaskFailedException("The worker has no Sidecar:Directory configured, so it cannot read sheet title blocks.", retryable: false);

        var input = await context.GetInputAsync(ct);
        var recorded = input.Sheets.GroupBy(s => s.FileId).ToDictionary(g => g.Key, g => g.Select(s => s.PageNo).ToHashSet());
        var files = input.Files.Where(f => f.DuplicateOf is null).ToList();
        var total = 0;

        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            context.ReportProgress(i, files.Count, $"Reading the title blocks of {file.FileName}");

            var pdf = await context.DownloadFileAsync(file, ct);
            var outPath = Path.Combine(context.ScratchDirectory, $"sheets-{i}.json");
            var arguments = new List<string> { "-B", "-m", "ductwork_sidecar", "sheets", pdf, "--out", outPath };
            if (sidecar.ReadTextLessTitleBlocks) arguments.Add("--ocr");

            var environment = new Dictionary<string, string> { ["PYTHONPATH"] = sidecar.Directory, ["PYTHONIOENCODING"] = "utf-8", ["PYTHONDONTWRITEBYTECODE"] = "1" };
            // Reading by OCR takes seconds per text-less page, so the timeout is per file and generous.
            var run = await runner.RunAsync(sidecar.PythonPath, arguments, context.ScratchDirectory, environment, TimeSpan.FromMinutes(sidecar.PageTimeoutMinutes * 3), ct);
            if (run.ExitCode != 0)
                throw new TaskFailedException($"Reading the sheets of \"{file.FileName}\" failed (exit {run.ExitCode}): {Tail(run.StandardError)}", retryable: false);

            IReadOnlyList<PageReport> pages;
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(outPath, ct));
                // When the task is the sheet-identification task itself, the server holds it to one producer, so a reader that says it is another is caught here.
                if (expectedProducer is not null && doc.RootElement.TryGetProperty("producer", out var reported)
                    && reported.Deserialize<ProducerRef>(ContractJson.Options)?.Key() is { } actual && actual != expectedProducer)
                    throw new TaskFailedException(
                        $"The sheet reader on this worker identifies itself differently from the binding for this task type ({actual} instead of {expectedProducer}). Update the worker or the binding.", retryable: false);
                pages = doc.RootElement.GetProperty("pages").Deserialize<List<PageReport>>(ContractJson.Options)
                    ?? throw new JsonException("no pages");
            }
            catch (Exception ex) when (ex is JsonException or IOException or KeyNotFoundException)
            {
                throw new TaskFailedException($"The sheet details for \"{file.FileName}\" could not be read: {ex.Message}", retryable: false, ex);
            }

            var have = recorded.GetValueOrDefault(file.FileId) ?? [];
            var fresh = pages.Where(p => !have.Contains(p.PageNo)).ToList();
            if (fresh.Count == 0) continue;

            var reply = await context.Api.RegisterSheetsAsync(context.Task.TaskId, file.FileId, fresh, ct);
            total += reply.Recorded;
        }

        context.ReportProgress(files.Count, files.Count, "Sheets recorded");
        return total;
    }

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 600 ? trimmed : "..." + trimmed[^600..];
    }
}

/// <summary>Records the sheets of a job's drawing files: number, title, discipline, kind and content hash. It finds no items and says so with an empty result.</summary>
public sealed class IdentifySheetsHandler(SheetIdentification identification) : ITaskHandler
{
    public string TaskType => "identify-sheets";

    public async Task<string> RunAsync(TaskContext context, CancellationToken cancellationToken)
    {
        await identification.RecordMissingAsync(context, cancellationToken, context.Task.Producer.Key());
        return JsonSerializer.Serialize(new TaskResultManifest
        {
            ContractVersion = ContractInfo.Version, TaskType = context.Task.TaskType, TaskPublicId = context.Task.TaskPublicId.ToString("D"),
            Producer = context.Task.Producer, Detections = [], Findings = []
        }, ContractJson.Options);
    }
}
