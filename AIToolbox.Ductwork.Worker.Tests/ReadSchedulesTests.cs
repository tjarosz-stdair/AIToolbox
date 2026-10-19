using System.Security.Cryptography;
using System.Text.Json;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Worker;
using AIToolbox.Ductwork.Worker.Handlers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AIToolbox.Ductwork.Worker.Tests;

public sealed class ScheduleAssemblerTests
{
    private static ScheduleReport Schedule(string title, string sheet, params ScheduleRowReport[] rows) => new(sheet, 1, title, [], rows);

    private static ScheduleRowReport Row(string mark, string? model = "FV-0510", double? quantity = null, string? location = "BATHROOM") =>
        new(mark, null, location, "PANASONIC", model, null, "50 CFM", null, quantity, [10, 20, 400, 32]);

    private static MarkCountReport Count(string mark, string sheet, int page, int count) => new(mark, sheet, page, count, [100, 200, 130, 214]);

    [Fact]
    public void A_mark_written_on_plans_gives_one_proposal_per_sheet_with_the_number_of_tags_counted_there()
    {
        var (detections, findings) = ScheduleAssembler.Build([Schedule("EXHAUST FAN SCHEDULE", "M5.1", Row("EF-1"))], [Count("EF-1", "M1.2", 2, 3), Count("EF-1", "M1.3", 3, 1)]);

        Assert.Empty(findings);
        Assert.Equal([("M1.2", 3d), ("M1.3", 1d)], detections.Select(d => (d.SheetViewRef!, d.Quantity)));
        var d = detections[0];
        Assert.Equal(("EXHAUST FAN", "EF-1", "PANASONIC", "FV-0510", DetectionMethod.Schedule, DetectorFamily.ScheduleNote, null), (d.Type, d.Tag, d.Manufacturer, d.Model, d.Method, d.Family, d.Disposition));
        Assert.Equal(new BoundingBox(100, 200, 130, 214), d.Box);                              // where the tag is, so the viewer can show it
        Assert.Contains("Written 3 time(s) on M1.2", d.Evidence);
        Assert.Contains("EXHAUST FAN SCHEDULE on M5.1", d.Evidence);
    }

    [Fact]
    public void A_quantity_in_the_schedule_is_used_as_written_and_a_plan_count_that_disagrees_raises_a_question()
    {
        var (detections, findings) = ScheduleAssembler.Build([Schedule("LOUVER SCHEDULE", "M5", Row("L-1", quantity: 4))], [Count("L-1", "M1.1", 1, 7)]);

        var d = Assert.Single(detections);
        Assert.Equal((4d, "M5", null), (d.Quantity, d.SheetViewRef, d.Disposition));
        var f = Assert.Single(findings);
        Assert.Equal((FindingKind.ReferenceDisagreement, d.DetectionId), (f.Kind, f.DetectionId));
        Assert.Contains("quantity of 4", f.Question);
        Assert.Contains("7 time(s)", f.Question);
        Assert.Equal(["4", "7"], f.Candidates);
    }

    [Fact]
    public void A_quantity_that_the_plans_agree_with_raises_nothing()
    {
        var (detections, findings) = ScheduleAssembler.Build([Schedule("LOUVER SCHEDULE", "M5", Row("L-1", quantity: 4))], [Count("L-1", "M1.1", 1, 3), Count("L-1", "M1.2", 2, 1)]);

        Assert.Single(detections);
        Assert.Empty(findings);
    }

    [Fact]
    public void A_scheduled_mark_found_on_no_plan_is_to_be_determined_with_a_question_and_never_counted_as_one()
    {
        var (detections, findings) = ScheduleAssembler.Build([Schedule("STAIR PRESSURIZATION FAN SCHEDULE", "M5.1", Row("SPF-A"))], []);

        var d = Assert.Single(detections);
        Assert.Equal((Disposition.TBD, "M5.1", 1d), (d.Disposition, d.SheetViewRef, d.Quantity));
        Assert.Contains("not found on any plan", d.Evidence);
        var f = Assert.Single(findings);
        Assert.Equal((FindingKind.MissingEvidence, d.DetectionId), (f.Kind, f.DetectionId));
        Assert.Contains("SPF-A is scheduled", f.Question);
    }

    [Fact]
    public void A_mark_that_two_schedules_name_is_one_piece_of_equipment_described_by_the_first()
    {
        var (detections, _) = ScheduleAssembler.Build(
            [Schedule("GRILLE, REGISTER AND DIFFUSER SCHEDULE", "M5", Row("EF-1", model: "WRONG")), Schedule("EXHAUST FAN SCHEDULE", "M5", Row("EF-1", model: "RIGHT"))], [Count("EF-1", "M1", 1, 2)]);

        var d = Assert.Single(detections);
        Assert.Equal("WRONG", d.Model);
    }

    [Fact]
    public void Ids_are_unique_even_when_marks_repeat_after_cleaning_and_a_row_with_no_details_still_has_an_honest_type()
    {
        var (detections, _) = ScheduleAssembler.Build(
            [Schedule("SCHEDULE", "M5", new ScheduleRowReport("JF-XX-#", null, null, null, null, null, null, null, null, null), new ScheduleRowReport("JF XX #", null, null, null, null, null, null, null, null, null))], []);

        Assert.Equal(2, detections.Select(d => d.DetectionId).Distinct().Count());
        Assert.All(detections, d => Assert.Equal("Equipment", d.Type));
        Assert.All(detections, d => Assert.Null(d.Manufacturer));
    }

    [Theory]
    [InlineData("EXHAUST FAN SCHEDULE", "EXHAUST FAN")]
    [InlineData("GRILLE, REGISTER AND DIFFUSER SCHEDULE", "GRILLE, REGISTER AND DIFFUSER")]
    [InlineData("Mini-Split Heat Pump Schedules", "Mini-Split Heat Pump")]
    [InlineData("SCHEDULE", "Equipment")]
    public void The_kind_of_equipment_is_the_schedules_title_without_the_word_schedule(string title, string expected) =>
        Assert.Equal(expected, ScheduleAssembler.TypeOf(title));
}

public sealed class ReadSchedulesHandlerTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "ductwork-sched-" + Guid.NewGuid().ToString("N"));
    private static readonly ProducerRef Reader = ProducerRef.Algorithm("schedule-reader", "0.1.0", "params-1");

    public ReadSchedulesHandlerTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); }
        catch (IOException) { }
    }

    private sealed class FakeRunner(Func<IReadOnlyList<string>, ProcessResult> onRun) : IProcessRunner
    {
        public List<IReadOnlyList<string>> Runs { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Runs.Add(arguments);
            return Task.FromResult(onRun(arguments));
        }
    }

    private static string Value(IReadOnlyList<string> args, string flag) => args[args.ToList().IndexOf(flag) + 1];

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static object Producer(ProducerRef p) => new { kind = "Algorithm", algorithmName = p.AlgorithmName, algorithmVersion = p.AlgorithmVersion, paramsHash = p.ParamsHash };

    private static ProcessResult Respond(IReadOnlyList<string> args, ProducerRef? producer = null)
    {
        var p = Producer(producer ?? Reader);
        var spec = JsonDocument.Parse(File.ReadAllText(Value(args, "--spec"))).RootElement;
        string json;
        if (args[3] == "schedules")
            json = JsonSerializer.Serialize(new
            {
                producer = p,
                schedules = new[]
                {
                    new
                    {
                        sheet = "M5.1", page = 5, title = "EXHAUST FAN SCHEDULE", headers = new[] { "MARK" },
                        rows = new[]
                        {
                            new { mark = "EF-1", description = (string?)null, location = "BATH", manufacturer = "PANASONIC", model = "FV-0510", model2 = (string?)null, capacity = "50 CFM", size = (string?)null, quantity = (double?)null, box = new[] { 1.0, 2, 3, 4 } },
                            new { mark = "EF-2", description = (string?)null, location = "KITCHEN", manufacturer = "PANASONIC", model = "FV-1115", model2 = (string?)null, capacity = "100 CFM", size = (string?)null, quantity = (double?)null, box = new[] { 1.0, 2, 3, 4 } }
                        }
                    }
                }
            });
        else
            json = JsonSerializer.Serialize(new
            {
                producer = p,
                counts = spec.GetProperty("marks").EnumerateArray().Where(m => m.GetString() == "EF-1")
                    .SelectMany(m => spec.GetProperty("pages").EnumerateArray().Select(pg => new { mark = "EF-1", sheet = pg.GetProperty("sheet").GetString(), page = pg.GetProperty("page").GetInt32(), count = 2, box = new[] { 10.0, 20, 40, 34 } }))
            });
        File.WriteAllText(Value(args, "--out"), json);
        return new ProcessResult(0, "", "");
    }

    private (TaskContext Context, FakeApi Api, Guid ScheduleFile, Guid PlanFile) Setup(bool separateFiles = true, bool noSheets = false)
    {
        var data = new byte[] { 37, 80, 68, 70 };
        var scheduleFile = Guid.NewGuid();
        var planFile = separateFiles ? Guid.NewGuid() : scheduleFile;
        var api = new FakeApi();
        api.Files[scheduleFile] = data;
        api.Files[planFile] = data;
        var files = new List<InputFile> { new(scheduleFile, "schedules.pdf", data.Length, Sha(data), 1, null) };
        if (separateFiles) files.Add(new InputFile(planFile, "plans.pdf", data.Length, Sha(data), 1, null));
        InputSheet S(Guid f, int page, string number, string kind) => new(Guid.NewGuid(), f, page, number, "T", kind, true, 2592, 1728, 0, null, [], "Mechanical");
        api.Input = new TaskInput(0, 1, "read-schedules", null, files, noSheets ? [] : [S(scheduleFile, 5, "M5.1", "Schedule"), S(planFile, 2, "M1.2", "Plan"), S(planFile, 3, "M1.3", "Plan")], []);
        var task = api.NewTask("read-schedules") with { Producer = Reader };
        return (new TaskContext(task, "w", _scratch, api, NullLogger.Instance), api, scheduleFile, planFile);
    }

    private static IOptions<WorkerOptions> Opts(string directory = "C:\\sidecar") => Options.Create(new WorkerOptions { Sidecar = new SidecarOptions { PythonPath = "python", Directory = directory } });

    private static TaskResultManifest Parse(string json) => JsonSerializer.Deserialize<TaskResultManifest>(json, ContractJson.Options)!;

    [Fact]
    public async Task Schedules_are_read_then_the_scheduled_marks_are_counted_on_the_plans_and_proposed()
    {
        var (context, _, _, _) = Setup();
        var runner = new FakeRunner(a => Respond(a));

        var manifest = Parse(await new ReadSchedulesHandler(runner, Opts()).RunAsync(context, default));

        Assert.Equal(["schedules", "marks"], runner.Runs.Select(r => r[3]));
        var marksSpec = JsonDocument.Parse(File.ReadAllText(Value(runner.Runs[1], "--spec"))).RootElement;
        Assert.Equal(["EF-1", "EF-2"], marksSpec.GetProperty("marks").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal(["M1.2", "M1.3"], marksSpec.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("sheet").GetString()));
        Assert.Equal([("EF-1", "M1.2", 2d), ("EF-1", "M1.3", 2d)], manifest.Detections.Where(d => d.Disposition is null).Select(d => (d.Tag!, d.SheetViewRef!, d.Quantity)));
        var tbd = Assert.Single(manifest.Detections, d => d.Disposition == Disposition.TBD);
        Assert.Equal("EF-2", tbd.Tag);                                                      // scheduled, found on no plan
        Assert.Equal(Reader.Key(), manifest.Producer.Key());
        Assert.Single(manifest.Findings, f => f.Kind == FindingKind.MissingEvidence);
    }

    [Fact]
    public async Task A_job_in_one_file_is_read_with_the_schedule_pages_and_the_plan_pages_of_that_file()
    {
        var (context, _, _, _) = Setup(separateFiles: false);
        var runner = new FakeRunner(a => Respond(a));

        await new ReadSchedulesHandler(runner, Opts()).RunAsync(context, default);

        Assert.Equal(2, runner.Runs.Count);
        var schedulePages = JsonDocument.Parse(File.ReadAllText(Value(runner.Runs[0], "--spec"))).RootElement.GetProperty("pages");
        Assert.Equal([5], schedulePages.EnumerateArray().Select(p => p.GetProperty("page").GetInt32()));
    }

    [Fact]
    public async Task Without_plan_sheets_nothing_is_counted_and_every_row_is_to_be_determined()
    {
        var (context, api, scheduleFile, _) = Setup(separateFiles: false);
        api.Input = api.Input with { Sheets = api.Input.Sheets.Where(s => s.Kind == "Schedule").ToList() };
        var runner = new FakeRunner(a => Respond(a));

        var manifest = Parse(await new ReadSchedulesHandler(runner, Opts()).RunAsync(context, default));

        Assert.Single(runner.Runs);
        Assert.All(manifest.Detections, d => Assert.Equal(Disposition.TBD, d.Disposition));
        Assert.Equal(2, manifest.Detections.Count);
        _ = scheduleFile;
    }

    [Fact]
    public async Task A_reader_that_identifies_as_another_producer_is_caught_before_the_server_would_refuse_everything()
    {
        var (context, _, _, _) = Setup();
        var runner = new FakeRunner(a => Respond(a, ProducerRef.Algorithm("schedule-reader", "9.9.9", "other")));

        var ex = await Assert.ThrowsAsync<TaskFailedException>(() => new ReadSchedulesHandler(runner, Opts()).RunAsync(context, default));

        Assert.Contains("identifies itself differently", ex.Message);
        Assert.False(ex.Retryable);
    }

    [Fact]
    public async Task A_child_that_fails_or_writes_nonsense_ends_the_task_with_a_reason()
    {
        var (context, _, _, _) = Setup();

        var crash = await Assert.ThrowsAsync<TaskFailedException>(() => new ReadSchedulesHandler(new FakeRunner(_ => new ProcessResult(1, "", "Traceback: boom")), Opts()).RunAsync(context, default));
        var junk = await Assert.ThrowsAsync<TaskFailedException>(() => new ReadSchedulesHandler(new FakeRunner(a => { File.WriteAllText(Value(a, "--out"), "nope"); return new ProcessResult(0, "", ""); }), Opts()).RunAsync(context, default));

        Assert.Contains("boom", crash.Message);
        Assert.Contains("could not be read", junk.Message);
    }

    [Fact]
    public async Task A_job_with_no_schedule_sheets_says_what_to_check()
    {
        var (context, api, _, _) = Setup();
        api.Input = api.Input with { Sheets = api.Input.Sheets.Where(s => s.Kind == "Plan").ToList() };

        var ex = await Assert.ThrowsAsync<TaskFailedException>(() => new ReadSchedulesHandler(new FakeRunner(_ => new ProcessResult(0, "", "")), Opts()).RunAsync(context, default));

        Assert.Contains("no in-scope mechanical schedule sheets", ex.Message);
    }

    [Fact]
    public async Task A_job_with_no_sheets_has_them_read_first()
    {
        var (context, api, scheduleFile, planFile) = Setup(noSheets: true);
        api.OnRegisterSheets = (f, pages) => api.Input = api.Input with
        {
            Sheets = [.. api.Input.Sheets, .. pages.Select(p => new InputSheet(Guid.NewGuid(), f, p.PageNo, p.SheetNumber, p.Title, p.Kind ?? "Other", true, p.WidthPts, p.HeightPts, p.Rotation, null, [], p.Discipline))]
        };
        var runner = new FakeRunner(a =>
        {
            if (a[3] != "sheets") return Respond(a);
            var isSchedules = Path.GetFileNameWithoutExtension(a[4]).Contains(scheduleFile.ToString("N"));
            File.WriteAllText(Value(a, "--out"), JsonSerializer.Serialize(new
            {
                pages = new[] { new { pageNo = isSchedules ? 5 : 2, widthPts = 2592.0, heightPts = 1728.0, rotation = 0, sheetNumber = isSchedules ? "M5.1" : "M1.2", title = "T", discipline = "Mechanical", kind = isSchedules ? "Schedule" : "Plan", contentHash = new string(isSchedules ? 'a' : 'b', 64), hasTextLayer = true } }
            }));
            return new ProcessResult(0, "", "");
        });
        var options = Opts();

        var manifest = Parse(await new ReadSchedulesHandler(runner, options, new SheetIdentification(runner, options)).RunAsync(context, default));

        Assert.Equal(2, api.Registered.Count);
        Assert.Contains(manifest.Detections, d => d.Tag == "EF-1" && d.SheetViewRef == "M1.2");
        _ = planFile;
    }
}
