using System.Security.Cryptography;
using System.Text.Json;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Worker;
using AIToolbox.Ductwork.Worker.Handlers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AIToolbox.Ductwork.Worker.Tests;

/// <summary>Sheet identification as the worker runs it: a child is given one PDF, and what it reports is checked and recorded once.</summary>
public sealed class SheetIdentificationTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "ductwork-sheetid-" + Guid.NewGuid().ToString("N"));

    public SheetIdentificationTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); }
        catch (IOException) { }
    }

    private sealed class FakeRunner(Func<IReadOnlyList<string>, ProcessResult> onRun) : IProcessRunner
    {
        public List<IReadOnlyList<string>> Runs { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string> environment,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            Runs.Add(arguments);
            return Task.FromResult(onRun(arguments));
        }
    }

    private static string Value(IReadOnlyList<string> args, string flag) => args[args.ToList().IndexOf(flag) + 1];

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static ProcessResult Pages(IReadOnlyList<string> args, params (int No, string Number, string Kind, string Discipline)[] pages)
    {
        var json = JsonSerializer.Serialize(new
        {
            pages = pages.Select(p => new
            {
                pageNo = p.No, widthPts = 2592.0, heightPts = 1728.0, rotation = 270, sheetNumber = p.Number, title = "MECHANICAL PLAN", discipline = p.Discipline,
                kind = p.Kind, contentHash = new string((char)('a' + p.No), 64), hasTextLayer = true,
                views = new[] { new { title = "MECHANICAL PLAN", number = "1", type = "Plan", scaleRatio = (double?)48.0, polygon = (double[][]?)null } }
            })
        });
        File.WriteAllText(Value(args, "--out"), json);
        return new ProcessResult(0, "", "");
    }

    private static IOptions<WorkerOptions> Opts(bool ocr = true, string directory = "C:\\sidecar") =>
        Options.Create(new WorkerOptions { Sidecar = new SidecarOptions { PythonPath = "python", Directory = directory, ReadTextLessTitleBlocks = ocr } });

    private (TaskContext Context, FakeApi Api, Guid File) Setup(params InputSheet[] sheets)
    {
        var data = new byte[] { 37, 80, 68, 70 };
        var file = Guid.NewGuid();
        var api = new FakeApi();
        api.Files[file] = data;
        api.Input = new TaskInput(0, 1, "detect-residential-labels", null, [new InputFile(file, "plan.pdf", data.Length, Sha(data), 1, null)], sheets, [new InputBuildingType(9, "Type 4")]);
        // Recording sheets makes them part of what the server tells the next call about the job.
        api.OnRegisterSheets = (f, pages) => api.Input = api.Input with
        {
            Sheets = [.. api.Input.Sheets, .. pages.Select(p => new InputSheet(Guid.NewGuid(), f, p.PageNo, p.SheetNumber, p.Title, p.Kind ?? "Other", true, p.WidthPts, p.HeightPts, p.Rotation, null, [], p.Discipline))]
        };
        return (new TaskContext(api.NewTask("detect-residential-labels"), "w", _scratch, api, NullLogger.Instance), api, file);
    }

    private static InputSheet Existing(Guid file, int page, string number = "M1.2") =>
        new(Guid.NewGuid(), file, page, number, "Plan", "Plan", true, 2592, 1728, 0, null, [], "Mechanical");

    [Fact]
    public async Task Every_page_of_a_file_is_read_in_a_child_and_recorded_with_the_server()
    {
        var (context, api, file) = Setup();
        var runner = new FakeRunner(args => Pages(args, (1, "M0.1", "Other", "Mechanical"), (2, "M1.2", "Plan", "Mechanical"), (3, "E1.1", "Plan", "Electrical")));

        var recorded = await new SheetIdentification(runner, Opts()).RecordMissingAsync(context, default);

        Assert.Equal(3, recorded);
        var (registeredFile, pages) = Assert.Single(api.Registered);
        Assert.Equal(file, registeredFile);
        Assert.Equal(["M0.1", "M1.2", "E1.1"], pages.Select(p => p.SheetNumber));
        Assert.Equal(["Mechanical", "Mechanical", "Electrical"], pages.Select(p => p.Discipline));
        var view = Assert.Single(pages[0].Views!);                                           // the views the reader found travel with the page
        Assert.Equal(("MECHANICAL PLAN", "1", "Plan", 48.0), (view.Title, view.Number, view.Type, view.ScaleRatio));
        var args = Assert.Single(runner.Runs);
        Assert.Equal(["-B", "-m", "ductwork_sidecar", "sheets"], args.Take(4));
        Assert.EndsWith(".pdf", args[4]);
    }

    [Fact]
    public async Task Pages_already_recorded_are_left_alone_so_a_retry_does_the_rest()
    {
        var (context, api, file) = Setup(null!);
        api.Input = api.Input with { Sheets = [Existing(file, 1, "M0.1"), Existing(file, 2)] };
        var runner = new FakeRunner(args => Pages(args, (1, "M0.1", "Other", "Mechanical"), (2, "M1.2", "Plan", "Mechanical"), (3, "M1.3", "Plan", "Mechanical")));

        var recorded = await new SheetIdentification(runner, Opts()).RecordMissingAsync(context, default);

        Assert.Equal(1, recorded);
        Assert.Equal([3], api.Registered.Single().Pages.Select(p => p.PageNo));
    }

    [Fact]
    public async Task A_file_whose_pages_are_all_recorded_is_not_sent_again()
    {
        var (context, api, file) = Setup();
        api.Input = api.Input with { Sheets = [Existing(file, 1)] };
        var runner = new FakeRunner(args => Pages(args, (1, "M1.2", "Plan", "Mechanical")));

        Assert.Equal(0, await new SheetIdentification(runner, Opts()).RecordMissingAsync(context, default));
        Assert.Empty(api.Registered);
    }

    [Fact]
    public async Task A_copy_of_a_file_is_not_read_because_the_original_is()
    {
        var (context, api, file) = Setup();
        var copy = Guid.NewGuid();
        api.Input = api.Input with { Files = [.. api.Input.Files, new InputFile(copy, "copy.pdf", 4, Sha([37, 80, 68, 70]), 1, file)] };
        var runner = new FakeRunner(args => Pages(args, (1, "M1.2", "Plan", "Mechanical")));

        await new SheetIdentification(runner, Opts()).RecordMissingAsync(context, default);

        Assert.Single(runner.Runs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reading_text_less_title_blocks_by_OCR_is_a_setting(bool ocr)
    {
        var (context, _, _) = Setup();
        var runner = new FakeRunner(args => Pages(args, (1, "M1.2", "Plan", "Mechanical")));

        await new SheetIdentification(runner, Opts(ocr)).RecordMissingAsync(context, default);

        Assert.Equal(ocr, runner.Runs.Single().Contains("--ocr"));
    }

    [Fact]
    public async Task A_child_that_fails_or_writes_nonsense_ends_the_task_with_a_reason_and_records_nothing()
    {
        var (context, api, _) = Setup();
        var crash = new FakeRunner(_ => new ProcessResult(1, "", "Traceback: boom"));
        var junk = new FakeRunner(args => { File.WriteAllText(Value(args, "--out"), "not json"); return new ProcessResult(0, "", ""); });

        var failed = await Assert.ThrowsAsync<TaskFailedException>(() => new SheetIdentification(crash, Opts()).RecordMissingAsync(context, default));
        var unreadable = await Assert.ThrowsAsync<TaskFailedException>(() => new SheetIdentification(junk, Opts()).RecordMissingAsync(context, default));

        Assert.Contains("boom", failed.Message);
        Assert.Contains("could not be read", unreadable.Message);
        Assert.Empty(api.Registered);
    }

    [Fact]
    public async Task Without_a_sidecar_folder_it_says_so()
    {
        var (context, _, _) = Setup();

        var ex = await Assert.ThrowsAsync<TaskFailedException>(() => new SheetIdentification(new FakeRunner(_ => new ProcessResult(0, "", "")), Opts(directory: "")).RecordMissingAsync(context, default));

        Assert.False(ex.Retryable);
    }

    [Fact]
    public async Task The_identify_sheets_task_returns_an_empty_result_after_recording_the_sheets()
    {
        var (context, api, _) = Setup();
        var runner = new FakeRunner(args => Pages(args, (1, "M1.2", "Plan", "Mechanical")));
        var handler = new IdentifySheetsHandler(new SheetIdentification(runner, Opts()));

        var manifest = JsonSerializer.Deserialize<TaskResultManifest>(await handler.RunAsync(context, default), ContractJson.Options)!;

        Assert.Equal("identify-sheets", handler.TaskType);
        Assert.Empty(manifest.Detections);
        Assert.Single(api.Registered);
    }

    // ---- the residential detector reads the sheets itself when the job has none

    private static ProcessResult Residential(IReadOnlyList<string> args)
    {
        var detection = new DetectionProposal { DetectionId = "d1", Type = "Boot", Size = "4x10x7", Quantity = 1, Method = DetectionMethod.Text, Family = DetectorFamily.LabelFlagCount, Confidence = 0.9, SheetViewRef = Value(args, "--sheet-ref") };
        File.WriteAllText(Value(args, "--out"), JsonSerializer.Serialize(new TaskResultManifest
        {
            ContractVersion = ContractInfo.Version, TaskType = "x", TaskPublicId = Value(args, "--task-id"), Producer = ProducerRef.Algorithm("residential-label-counter", "0.1.0", "params-1"),
            Detections = [detection], Findings = []
        }, ContractJson.Options));
        return new ProcessResult(0, "", "");
    }

    [Fact]
    public async Task A_job_with_no_sheets_has_them_read_first_and_only_mechanical_plans_are_counted()
    {
        var (context, api, _) = Setup();
        context = new TaskContext(api.NewTask("detect-residential-labels") with { Producer = ProducerRef.Algorithm("residential-label-counter", "0.1.0", "params-1") }, "w", _scratch, api, NullLogger.Instance);
        var runner = new FakeRunner(args => args.Contains("sheets")
            ? Pages(args, (1, "M0.1", "Other", "Mechanical"), (2, "M1.2", "Plan", "Mechanical"), (3, "M1.3", "Plan", "Mechanical"), (4, "E1.1", "Plan", "Electrical"))
            : Residential(args));
        var options = Opts();
        var handler = new ResidentialLabelsHandler(runner, options, new SheetIdentification(runner, options));

        var manifest = JsonSerializer.Deserialize<TaskResultManifest>(await handler.RunAsync(context, default), ContractJson.Options)!;

        Assert.Equal(1, runner.Runs.Count(r => r.Contains("sheets")));
        Assert.Equal(2, runner.Runs.Count(r => r.Contains("residential")));                 // M1.2 and M1.3: not the legend, not the electrical plan
        Assert.Equal(["s1-d1", "s2-d1"], manifest.Detections.Select(d => d.DetectionId));
    }

    [Fact]
    public async Task A_job_that_already_has_sheets_is_not_read_again_and_a_non_mechanical_plan_is_skipped()
    {
        var (context, api, file) = Setup();
        api.Input = api.Input with
        {
            Sheets = [Existing(file, 1) with { Discipline = "Electrical" }, Existing(file, 2, "M1.3"), Existing(file, 3, "M1.4") with { Discipline = null }]
        };
        context = new TaskContext(api.NewTask("detect-residential-labels") with { Producer = ProducerRef.Algorithm("residential-label-counter", "0.1.0", "params-1") }, "w", _scratch, api, NullLogger.Instance);
        var runner = new FakeRunner(Residential);
        var options = Opts();

        await new ResidentialLabelsHandler(runner, options, new SheetIdentification(runner, options)).RunAsync(context, default);

        Assert.DoesNotContain(runner.Runs, r => r.Contains("sheets"));
        Assert.Equal(2, runner.Runs.Count(r => r.Contains("residential")));                 // the electrical one is skipped; one with no discipline is given the benefit of the doubt
        Assert.Empty(api.Registered);
    }

    [Fact]
    public async Task When_every_sheet_is_another_discipline_the_failure_says_what_to_check()
    {
        var (context, api, file) = Setup();
        api.Input = api.Input with { Sheets = [Existing(file, 1) with { Discipline = "Electrical" }] };
        var handler = new ResidentialLabelsHandler(new FakeRunner(Residential), Opts());

        var ex = await Assert.ThrowsAsync<TaskFailedException>(() => handler.RunAsync(context, default));

        Assert.Contains("no in-scope mechanical plan sheets", ex.Message);
    }
}
