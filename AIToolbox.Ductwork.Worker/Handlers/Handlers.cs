using System.Text.Json;
using AIToolbox.Ductwork.Contracts;
using Microsoft.Extensions.Options;

namespace AIToolbox.Ductwork.Worker.Handlers;

/// <summary>
/// Invents a few detections so the whole path (claim, heartbeat, result, review, count sheet) can be exercised without any
/// drawing logic. Off unless <see cref="WorkerOptions.EnableFakeHandler"/> is set, because its output is not real.
/// </summary>
public sealed class FakeDetectHandler : ITaskHandler
{
    public string TaskType => "fake-detect";

    public async Task<string> RunAsync(TaskContext context, CancellationToken cancellationToken)
    {
        var input = await context.GetInputAsync(cancellationToken);
        var detections = new List<DetectionProposal>();
        var types = new[] { "Type A", "Type B" };
        for (var i = 1; i <= 6; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.ReportProgress(i, 6, $"Fake item {i}");
            detections.Add(new DetectionProposal
            {
                DetectionId = $"fake-{i}", Type = "Boot", Size = i % 2 == 0 ? "4x10x7" : "4x12x7", Quantity = 1, Method = DetectionMethod.Text,
                Family = DetectorFamily.LabelFlagCount, Confidence = 0.9, BuildingType = types[i % 2], SheetViewRef = input.Sheets.FirstOrDefault()?.SheetNumber ?? "M1.0",
                Evidence = "invented by the fake handler"
            });
        }

        return JsonSerializer.Serialize(new TaskResultManifest
        {
            ContractVersion = ContractInfo.Version, TaskType = context.Task.TaskType, TaskPublicId = context.Task.TaskPublicId.ToString("D"),
            Producer = context.Task.Producer, Detections = detections, Findings = []
        }, ContractJson.Options);
    }
}

/// <summary>
/// Counts labeled terminals, fans, grilles and flag-note items on residential unit plans by running the Python sidecar once
/// per in-scope plan sheet, each in a child process that is given one PDF and one output path and nothing else.
/// </summary>
public sealed class ResidentialLabelsHandler(IProcessRunner runner, IOptions<WorkerOptions> options) : ITaskHandler
{
    public string TaskType => "detect-residential-labels";

    public async Task<string> RunAsync(TaskContext context, CancellationToken cancellationToken)
    {
        var sidecar = options.Value.Sidecar;
        if (string.IsNullOrWhiteSpace(sidecar.Directory))
            throw new TaskFailedException("The worker has no Sidecar:Directory configured, so it cannot run the residential detector.", retryable: false);

        var input = await context.GetInputAsync(cancellationToken);
        var sheets = input.Sheets.Where(s => s.InScope && s.DuplicateOf is null && string.Equals(s.Kind, "Plan", StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.FileId).ThenBy(s => s.PageNo).ToList();
        if (sheets.Count == 0)
            throw new TaskFailedException("This job has no in-scope plan sheets recorded. Record the sheets first, then run the detector.", retryable: false);

        var files = input.Files.ToDictionary(f => f.FileId);
        var buildingNames = input.BuildingTypes.ToDictionary(b => b.BuildingTypeId, b => b.Name);
        var detections = new List<DetectionProposal>();
        var findings = new List<FindingProposal>();
        var expected = context.Task.Producer.Key();

        for (var i = 0; i < sheets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sheet = sheets[i];
            var label = sheet.SheetNumber ?? $"page {sheet.PageNo}";
            context.ReportProgress(i, sheets.Count, $"Reading {label}");

            var pdf = await context.DownloadFileAsync(files[sheet.FileId], cancellationToken);
            var outPath = Path.Combine(context.ScratchDirectory, $"sheet-{i}.json");
            var arguments = new List<string>
            {
                "-B", "-m", "ductwork_sidecar", "residential", pdf, "--page", (sheet.PageNo - 1).ToString(), "--sheet-ref", sheet.SheetNumber ?? $"p{sheet.PageNo}",
                "--task-id", context.Task.TaskPublicId.ToString("D"), "--out", outPath
            };
            if (sheet.Views.FirstOrDefault(v => v.BuildingTypeId is not null)?.BuildingTypeId is { } bt && buildingNames.TryGetValue(bt, out var building))
            {
                arguments.Add("--building");
                arguments.Add(building);
            }

            var environment = new Dictionary<string, string>
            {
                ["PYTHONPATH"] = sidecar.Directory, ["PYTHONIOENCODING"] = "utf-8", ["PYTHONDONTWRITEBYTECODE"] = "1"
            };
            var run = await runner.RunAsync(sidecar.PythonPath, arguments, context.ScratchDirectory, environment, TimeSpan.FromMinutes(sidecar.PageTimeoutMinutes), cancellationToken);
            if (run.ExitCode != 0)
                throw new TaskFailedException($"The detector failed on {label} (exit {run.ExitCode}): {Tail(run.StandardError)}", retryable: false);

            TaskResultManifest page;
            try
            {
                page = JsonSerializer.Deserialize<TaskResultManifest>(await File.ReadAllTextAsync(outPath, cancellationToken), ContractJson.Options)
                    ?? throw new JsonException("empty manifest");
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                throw new TaskFailedException($"The detector's output for {label} could not be read: {ex.Message}", retryable: false, ex);
            }

            // The server holds the task to one producer. A detector that says it is another would have its whole result refused, so say why now.
            if (page.Producer.Key() != expected)
                throw new TaskFailedException(
                    $"The detector on this worker identifies itself differently from the binding for this task type ({page.Producer.Key()} instead of {expected}). Update the worker or the binding.",
                    retryable: false);

            // Detection ids must be unique across the whole result, and one page's ids repeat on the next.
            var prefix = $"s{i + 1}-";
            detections.AddRange(page.Detections.Select(d => d with { DetectionId = prefix + d.DetectionId }));
            findings.AddRange(page.Findings.Select(f => f with { FindingId = prefix + f.FindingId, DetectionId = f.DetectionId is null ? null : prefix + f.DetectionId }));
        }

        context.ReportProgress(sheets.Count, sheets.Count, "Done");
        return JsonSerializer.Serialize(new TaskResultManifest
        {
            ContractVersion = ContractInfo.Version, TaskType = context.Task.TaskType, TaskPublicId = context.Task.TaskPublicId.ToString("D"),
            Producer = context.Task.Producer, Detections = detections, Findings = findings
        }, ContractJson.Options);
    }

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 600 ? trimmed : "..." + trimmed[^600..];
    }
}
