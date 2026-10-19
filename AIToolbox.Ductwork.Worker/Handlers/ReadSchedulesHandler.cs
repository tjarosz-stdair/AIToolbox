using System.Text;
using System.Text.Json;
using AIToolbox.Ductwork.Contracts;
using Microsoft.Extensions.Options;

namespace AIToolbox.Ductwork.Worker.Handlers;

public sealed record ScheduleRowReport(
    string Mark, string? Description, string? Location, string? Manufacturer, string? Model, string? Model2, string? Capacity, string? Size, double? Quantity, double[]? Box);

public sealed record ScheduleReport(string Sheet, int Page, string Title, IReadOnlyList<string>? Headers, IReadOnlyList<ScheduleRowReport> Rows);

public sealed record MarkCountReport(string Mark, string Sheet, int Page, int Count, double[]? Box);

/// <summary>What the sidecar reports from one file's schedule pages.</summary>
public sealed record SchedulesResult(ProducerRef Producer, IReadOnlyList<ScheduleReport> Schedules);

/// <summary>What the sidecar reports from counting marks on one file's plan pages.</summary>
public sealed record MarksResult(ProducerRef Producer, IReadOnlyList<MarkCountReport> Counts);

/// <summary>
/// Turns what was read from the schedules and counted on the plans into proposals (plan section 6.7, family A). A schedule says what each
/// mark is; how many there are is how many times the mark is written on the plans, unless the schedule has a quantity column.
/// <list type="bullet">
/// <item>A schedule quantity is used as written, and a plan count that disagrees with it raises a question rather than winning.</item>
/// <item>Otherwise each plan sheet where the mark is written gives one proposal with the number of tags counted there, so a typical unit
/// plan gives the per-building quantity that the building type's multiplier is applied to.</item>
/// <item>A scheduled mark found on no plan is listed as to be determined, with a question, and is never counted as one.</item>
/// </list>
/// </summary>
public static class ScheduleAssembler
{
    public static (IReadOnlyList<DetectionProposal> Detections, IReadOnlyList<FindingProposal> Findings) Build(
        IReadOnlyList<ScheduleReport> schedules, IReadOnlyList<MarkCountReport> counts)
    {
        var detections = new List<DetectionProposal>();
        var findings = new List<FindingProposal>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string Id(string mark, string sheet)
        {
            var baseId = $"sch-{Sanitize(mark)}-{Sanitize(sheet)}";
            var id = baseId;
            for (var i = 2; !used.Add(id); i++) id = $"{baseId}-{i}";
            return id;
        }

        foreach (var schedule in schedules)
        {
            foreach (var row in schedule.Rows)
            {
                // A mark that appears in two schedules is one piece of equipment: the first schedule that names it describes it.
                if (!seen.Add(row.Mark)) continue;

                var type = TypeOf(schedule.Title);
                var description = Describe(schedule, row);
                var onPlans = counts.Where(c => c.Mark.Equals(row.Mark, StringComparison.OrdinalIgnoreCase) && c.Count > 0).OrderBy(c => c.Page).ToList();
                var planTotal = onPlans.Sum(c => c.Count);

                if (row.Quantity is { } quantity)
                {
                    var id = Id(row.Mark, schedule.Sheet);
                    detections.Add(New(id, type, row, schedule.Sheet, quantity, 0.9, null, row.Box, $"{description} The schedule gives a quantity of {Fmt(quantity)}."));
                    if (planTotal > 0 && Math.Abs(planTotal - quantity) > 0.0001)
                        findings.Add(new FindingProposal
                        {
                            FindingId = "f-" + id, Kind = FindingKind.ReferenceDisagreement, DetectionId = id,
                            Question = $"The schedule gives a quantity of {Fmt(quantity)} for {row.Mark}, but its tag is written {planTotal} time(s) on the plans ({string.Join(", ", onPlans.Select(c => $"{c.Count} on {c.Sheet}"))}). Which is right?",
                            Candidates = [Fmt(quantity), planTotal.ToString()], Evidence = description
                        });
                    continue;
                }

                if (onPlans.Count == 0)
                {
                    var id = Id(row.Mark, schedule.Sheet);
                    detections.Add(New(id, type, row, schedule.Sheet, 1, 0.5, Disposition.TBD, row.Box, $"{description} The tag was not found on any plan sheet that was read, so how many there are is not known."));
                    findings.Add(new FindingProposal
                    {
                        FindingId = "f-" + id, Kind = FindingKind.MissingEvidence, DetectionId = id,
                        Question = $"{row.Mark} is scheduled but its tag was not found on any plan sheet that was read. Is it on a sheet that has not been read, or is it not used on this job?",
                        Evidence = description
                    });
                    continue;
                }

                foreach (var count in onPlans)
                {
                    var id = Id(row.Mark, count.Sheet);
                    var evidence = $"{description} Written {count.Count} time(s) on {count.Sheet}.";
                    detections.Add(New(id, type, row, count.Sheet, count.Count, 0.8, null, count.Box ?? row.Box, evidence));
                }
            }
        }

        return (detections, findings);
    }

    private static DetectionProposal New(string id, string type, ScheduleRowReport row, string sheet, double quantity, double confidence, Disposition? disposition, double[]? box, string evidence) =>
        new()
        {
            DetectionId = id, Type = type, Tag = row.Mark, Size = Nz(row.Size), Manufacturer = Nz(row.Manufacturer), Model = Nz(row.Model), Quantity = quantity, Unit = QuantityUnit.EA,
            Method = DetectionMethod.Schedule, Family = DetectorFamily.ScheduleNote, Confidence = confidence, SheetViewRef = sheet, Disposition = disposition,
            Box = box is { Length: 4 } b ? new BoundingBox(b[0], b[1], b[2], b[3]) : null, Evidence = evidence.Length > 1000 ? evidence[..1000] : evidence
        };

    /// <summary>"EXHAUST FAN SCHEDULE" names the kind of equipment: "EXHAUST FAN".</summary>
    public static string TypeOf(string title)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(title ?? "", @"\bSCHEDULES?\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        t = string.Join(' ', t.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return t.Length == 0 ? "Equipment" : t.Length > 100 ? t[..100] : t;
    }

    private static string Describe(ScheduleReport schedule, ScheduleRowReport row)
    {
        var parts = new List<string> { $"{schedule.Title} on {schedule.Sheet}:" };
        foreach (var value in new[] { row.Description, row.Location, Join(row.Manufacturer, row.Model), row.Model2, row.Capacity })
            if (!string.IsNullOrWhiteSpace(value)) parts.Add(value.Trim() + ";");
        return string.Join(' ', parts).TrimEnd(';') + ".";
    }

    private static string Join(string? a, string? b) => string.Join(' ', new[] { a, b }.Where(v => !string.IsNullOrWhiteSpace(v)));

    private static string? Nz(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Fmt(double value) => value == Math.Truncate(value) ? ((long)value).ToString() : value.ToString("0.##");

    private static string Sanitize(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_');
        return sb.ToString();
    }
}

/// <summary>
/// Reads the equipment schedules on a job's schedule sheets and counts each scheduled mark on its plan sheets, by running the Python
/// sidecar in a child process per file, and proposes what it found. The sheets are read first when the job has none recorded.
/// </summary>
public sealed class ReadSchedulesHandler(IProcessRunner runner, IOptions<WorkerOptions> options, SheetIdentification? identification = null) : ITaskHandler
{
    public string TaskType => "read-schedules";

    public async Task<string> RunAsync(TaskContext context, CancellationToken cancellationToken)
    {
        var sidecar = options.Value.Sidecar;
        if (string.IsNullOrWhiteSpace(sidecar.Directory))
            throw new TaskFailedException("The worker has no Sidecar:Directory configured, so it cannot read schedules.", retryable: false);

        var input = await context.GetInputAsync(cancellationToken);
        if (input.Sheets.Count == 0 && identification is { IsAvailable: true })
        {
            await identification.RecordMissingAsync(context, cancellationToken);
            input = await context.RefreshInputAsync(cancellationToken);
        }

        var usable = input.Sheets.Where(s => s.InScope && s.DuplicateOf is null && (string.IsNullOrEmpty(s.Discipline) || string.Equals(s.Discipline, "Mechanical", StringComparison.OrdinalIgnoreCase))).ToList();
        var scheduleSheets = usable.Where(s => string.Equals(s.Kind, "Schedule", StringComparison.OrdinalIgnoreCase)).ToList();
        var planSheets = usable.Where(s => string.Equals(s.Kind, "Plan", StringComparison.OrdinalIgnoreCase)).ToList();
        if (scheduleSheets.Count == 0)
            throw new TaskFailedException(input.Sheets.Count == 0
                ? "This job has no sheets recorded. Record the sheets first, then read the schedules."
                : "This job has no in-scope mechanical schedule sheets. Check the sheets' kind, discipline and scope on the Drawings tab.", retryable: false);

        var files = input.Files.ToDictionary(f => f.FileId);
        var expected = context.Task.Producer.Key();
        var schedules = new List<ScheduleReport>();
        var counts = new List<MarkCountReport>();

        var scheduleFiles = scheduleSheets.GroupBy(s => s.FileId).OrderBy(g => files[g.Key].FileName).ToList();
        var planFiles = planSheets.GroupBy(s => s.FileId).OrderBy(g => files[g.Key].FileName).ToList();
        var steps = scheduleFiles.Count + planFiles.Count;
        var done = 0;

        foreach (var group in scheduleFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.ReportProgress(done++, steps, $"Reading the schedules in {files[group.Key].FileName}");
            var result = await RunAsync<SchedulesResult>(context, files[group.Key], "schedules", new { pages = group.OrderBy(s => s.PageNo).Select(s => new { page = s.PageNo, sheet = SheetRef(s) }) }, expected, cancellationToken);
            schedules.AddRange(result.Schedules);
        }

        var marks = schedules.SelectMany(s => s.Rows).Select(r => r.Mark).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (marks.Count > 0)
            foreach (var group in planFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                context.ReportProgress(done++, steps, $"Counting tags on the plans in {files[group.Key].FileName}");
                var result = await RunAsync<MarksResult>(context, files[group.Key], "marks", new { marks, pages = group.OrderBy(s => s.PageNo).Select(s => new { page = s.PageNo, sheet = SheetRef(s) }) }, expected, cancellationToken);
                counts.AddRange(result.Counts);
            }

        var (detections, findings) = ScheduleAssembler.Build(schedules, counts);
        context.ReportProgress(steps, steps, "Done");
        return JsonSerializer.Serialize(new TaskResultManifest
        {
            ContractVersion = ContractInfo.Version, TaskType = context.Task.TaskType, TaskPublicId = context.Task.TaskPublicId.ToString("D"),
            Producer = context.Task.Producer, Detections = detections, Findings = findings
        }, ContractJson.Options);
    }

    private static string SheetRef(InputSheet s) => string.IsNullOrWhiteSpace(s.SheetNumber) ? $"p{s.PageNo}" : s.SheetNumber!;

    private async Task<T> RunAsync<T>(TaskContext context, InputFile file, string command, object spec, string expectedProducer, CancellationToken ct)
    {
        var sidecar = options.Value.Sidecar;
        var pdf = await context.DownloadFileAsync(file, ct);
        var key = $"{command}-{file.FileId:N}";
        var specPath = Path.Combine(context.ScratchDirectory, key + "-spec.json");
        var outPath = Path.Combine(context.ScratchDirectory, key + ".json");
        await File.WriteAllTextAsync(specPath, JsonSerializer.Serialize(spec), ct);

        var environment = new Dictionary<string, string> { ["PYTHONPATH"] = sidecar.Directory, ["PYTHONIOENCODING"] = "utf-8", ["PYTHONDONTWRITEBYTECODE"] = "1" };
        var run = await runner.RunAsync(sidecar.PythonPath, ["-B", "-m", "ductwork_sidecar", command, pdf, "--spec", specPath, "--out", outPath], context.ScratchDirectory, environment,
            TimeSpan.FromMinutes(sidecar.PageTimeoutMinutes * 3), ct);
        if (run.ExitCode != 0)
            throw new TaskFailedException($"Reading \"{file.FileName}\" failed (exit {run.ExitCode}): {Tail(run.StandardError)}", retryable: false);

        T result;
        ProducerRef producer;
        try
        {
            var json = await File.ReadAllTextAsync(outPath, ct);
            result = JsonSerializer.Deserialize<T>(json, ContractJson.Options) ?? throw new JsonException("empty result");
            producer = result switch { SchedulesResult s => s.Producer, MarksResult m => m.Producer, _ => throw new JsonException("unexpected result") };
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            throw new TaskFailedException($"The result for \"{file.FileName}\" could not be read: {ex.Message}", retryable: false, ex);
        }

        // The server holds the task to one producer. A reader that says it is another would have its whole result refused, so say why now.
        if (producer.Key() != expectedProducer)
            throw new TaskFailedException(
                $"The schedule reader on this worker identifies itself differently from the binding for this task type ({producer.Key()} instead of {expectedProducer}). Update the worker or the binding.",
                retryable: false);
        return result;
    }

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 600 ? trimmed : "..." + trimmed[^600..];
    }
}
