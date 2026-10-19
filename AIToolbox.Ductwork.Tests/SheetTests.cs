using System.Security.Cryptography;
using System.Text;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Counting;
using AIToolbox.Ductwork.Core.Data;
using AIToolbox.Ductwork.Core.Drawings;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Tasks;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Tests;

public sealed class SheetTests(SqlFixture db) : IClassFixture<SqlFixture>, IDisposable
{
    private const string Owner = "owner@example.com";
    private const string Editor = "editor@example.com";
    private const string Viewer = "viewer@example.com";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ductwork-sheets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private Func<CancellationToken, Task<SqlConnection>> Open => async ct =>
    {
        var connection = new SqlConnection(db.ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    };

    private SheetRepository Sheets => new(Open);

    private static AuditContext Ctx(string email, string? session = null) =>
        AuditContext.ForUser(email, displayName: email.Split('@')[0], sessionId: session ?? "s-" + email, userAgent: "tests");

    private sealed record Setup(JobRecord Job, EditToken Token, SourceFileRecord File);

    private async Task<Setup> JobWithFileAsync(byte seed = 1)
    {
        var jobs = new JobRepository(db.ConnectionString);
        var job = await jobs.CreateAsync(new NewJob { Name = "Sheets " + Guid.NewGuid().ToString("N")[..8] }, Ctx(Owner));
        await jobs.AddMemberAsync(job.JobId, Editor, JobRole.Editor, Ctx(Owner));
        await jobs.AddMemberAsync(job.JobId, Viewer, JobRole.Viewer, Ctx(Owner));
        var token = (await new EditLockService(db.ConnectionString).AcquireAsync(job.JobId, Ctx(Editor))).Token!;
        return new Setup(job, token, await UploadAsync(job.JobId, token, seed));
    }

    private async Task<SourceFileRecord> UploadAsync(long jobId, EditToken token, byte seed)
    {
        var data = new byte[600];
        Array.Fill(data, seed);
        Encoding.ASCII.GetBytes("%PDF-1.7\n").CopyTo(data, 0);
        var uploads = new UploadService(Open, new FileSystemArtifactStore(_root), new UploadSettings { ChunkBytes = 1000 });
        var session = await uploads.BeginAsync(jobId, new BeginUpload($"f{seed}.pdf", data.Length, null), token, Ctx(Editor));
        await uploads.AppendAsync(session.PublicId, 0, new MemoryStream(data), null, Ctx(Editor));
        return await uploads.CompleteAsync(session.PublicId, token, Ctx(Editor));
    }

    [Fact]
    public async Task A_page_that_is_plainly_another_discipline_is_recorded_out_of_scope_and_can_be_brought_back()
    {
        var s = await JobWithFileAsync();

        var sheets = await Sheets.RegisterAsync(s.Job.JobId, s.File.PublicId,
            [Page(1) with { Discipline = "Electrical" }, Page(2) with { Discipline = "Mechanical" }, Page(3) with { Discipline = null }, Page(4) with { Discipline = "Plumbing" }, Page(5) with { Discipline = "HVAC" }],
            s.Token, Ctx(Editor));

        Assert.Equal([false, true, true, false, true], sheets.OrderBy(x => x.PageNo).Select(x => x.InScope));
        var back = await Sheets.UpdateSheetAsync(sheets.Single(x => x.PageNo == 1).SheetId, new SheetEdit { InScope = true }, s.Token, Ctx(Editor));
        Assert.True(back.InScope);
    }

    private static NewDetectedView Found(string title, double? ratio, params double[][] polygon) =>
        new() { Title = title, Type = ViewType.Plan, ScaleRatio = ratio, Polygon = polygon.Length == 0 ? null : polygon };

    [Fact]
    public async Task Views_a_reader_found_are_created_with_the_sheet_with_their_stated_scale_and_who_read_it()
    {
        var s = await JobWithFileAsync();
        var page = Page(1) with
        {
            Views = [Found("1. LEVEL 1 PLAN", 96, [0, 0], [1000, 0], [1000, 800], [0, 800]), Found("2. ROOF DETAIL", null), new NewDetectedView { Title = "3. FAN DETAIL", Type = ViewType.Detail }]
        };

        var sheets = await new SheetRepository(Open).RegisterFromTaskAsync(await TaskAsync(s), s.File.PublicId, [page], AuditContext.ForWorker("c1:farm"));

        var views = (await Sheets.ListByJobAsync(s.Job.JobId)).Single().Views.OrderBy(v => v.Title).ToList();
        Assert.Equal(3, views.Count);
        Assert.Equal(("1. LEVEL 1 PLAN", 96d, ScaleMethod.Stated, "c1:farm", ViewExtraction.Geometry), (views[0].Title, views[0].ScaleRatio, views[0].ScaleMethod, views[0].ScaleSetBy, views[0].ExtractionMethod));
        Assert.NotNull(views[0].PolygonJson);
        Assert.Null(views[1].ScaleRatio);                                                   // no scale printed means none is claimed
        Assert.Null(views[1].PolygonJson);                                                  // whole page
        Assert.Equal(ViewType.Detail, views[2].Type);
        _ = sheets;
    }

    [Fact]
    public async Task A_page_that_repeats_one_already_recorded_does_not_get_its_views_made_twice()
    {
        var a = await JobWithFileAsync(1);
        var h = Hash("same");
        await Sheets.RegisterAsync(a.Job.JobId, a.File.PublicId, [Page(1, h) with { Views = [Found("LEVEL 1 PLAN", 96)] }], a.Token, Ctx(Editor));
        var second = await UploadAsync(a.Job.JobId, a.Token, 2);

        await Sheets.RegisterAsync(a.Job.JobId, second.PublicId, [Page(1, h) with { Views = [Found("LEVEL 1 PLAN", 96)] }], a.Token, Ctx(Editor));

        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.SheetView WHERE JobId = @j;", ("@j", a.Job.JobId)));
    }

    [Fact]
    public async Task A_view_outside_the_page_or_with_no_title_is_refused_or_skipped_and_never_half_recorded()
    {
        var s = await JobWithFileAsync();

        var outside = await Assert.ThrowsAsync<JobRuleViolationException>(() =>
            Sheets.RegisterAsync(s.Job.JobId, s.File.PublicId, [Page(1) with { Views = [Found("PLAN", 96, [0, 0], [99999, 0], [99999, 10])] }], s.Token, Ctx(Editor)));
        Assert.Contains("outside the page", outside.Message);
        Assert.Empty(await Sheets.ListByJobAsync(s.Job.JobId));                              // the page was not recorded either

        await Sheets.RegisterAsync(s.Job.JobId, s.File.PublicId, [Page(1) with { Views = [Found("   ", 96), Found("PLAN", 96)] }], s.Token, Ctx(Editor));
        Assert.Equal(["PLAN"], (await Sheets.ListByJobAsync(s.Job.JobId)).Single().Views.Select(v => v.Title));
    }

    private async Task<long> TaskAsync(Setup s)
    {
        var producer = await db.ScalarAsync<long>("INSERT INTO ductwork.Producer (ProducerKey, Kind, AlgorithmName, AlgorithmVersion, ParamsHash) OUTPUT inserted.ProducerId VALUES (@k, 'Algorithm', 'x', '1', 'h');", ("@k", "v-" + Guid.NewGuid().ToString("N")));
        var id = await db.ScalarAsync<long>(
            "INSERT INTO ductwork.ProcessingTask (JobId, TaskType, Status, ProducerId, ContractVersion, CreatedBy, LeaseOwner, LeaseExpiresUtc, StartedUtc, Attempt) OUTPUT inserted.ProcessingTaskId " +
            "VALUES (@j, 'identify-sheets', 'Running', @p, '0.1.0', 'tests', 'c1:farm', DATEADD(MINUTE, 5, SYSUTCDATETIME()), SYSUTCDATETIME(), 1);", ("@j", s.Job.JobId), ("@p", producer));
        return id;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static NewSheet Page(int no, string? hash = null, string? number = null) => new()
    {
        PageNo = no, WidthPts = 2592, HeightPts = 1728, SheetNumber = number ?? $"M1.{no}", Title = $"Plan {no}", Discipline = "Mechanical", Kind = SheetKind.Plan, ContentHashHex = hash
    };

    private static readonly IReadOnlyList<double[]> Square = [[100, 100], [900, 100], [900, 700], [100, 700]];

    private Task<int> EventsAsync(long jobId, string action) =>
        db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.JobAuditEvent WHERE JobId = @j AND Action = @a;", ("@j", jobId), ("@a", action));

    // ---- registering pages

    [Fact]
    public async Task Pages_of_a_file_are_recorded_with_their_size_and_details_and_audited()
    {
        var s = await JobWithFileAsync();

        var sheets = await Sheets.RegisterAsync(s.Job.JobId, s.File.PublicId, [Page(2), Page(1)], s.Token, Ctx(Editor));

        Assert.Equal([1, 2], sheets.Select(x => x.PageNo));
        Assert.Equal(("M1.1", "Plan 1", "Mechanical", SheetKind.Plan, true, 2592.0, 1728.0), (sheets[0].SheetNumber, sheets[0].Title, sheets[0].Discipline, sheets[0].Kind, sheets[0].InScope, sheets[0].WidthPts, sheets[0].HeightPts));
        Assert.Equal(s.File.PublicId, sheets[0].SourceFilePublicId);
        Assert.Equal(1, await EventsAsync(s.Job.JobId, "sheets.registered"));
        Assert.True((await new JobRepository(db.ConnectionString).VerifyAuditChainAsync(s.Job.JobId)).IsValid);
    }

    [Fact]
    public async Task A_page_whose_content_the_job_already_holds_is_flagged_as_a_duplicate_and_left_out_of_scope()
    {
        var s = await JobWithFileAsync(1);
        var second = await UploadAsync(s.Job.JobId, s.Token, 2);
        var h = Hash("unit plan");

        var first = await Sheets.RegisterAsync(s.Job.JobId, s.File.PublicId, [Page(1, h), Page(2, Hash("other"))], s.Token, Ctx(Editor));
        var again = await Sheets.RegisterAsync(s.Job.JobId, second.PublicId, [Page(1, h)], s.Token, Ctx(Editor));

        Assert.Null(first[0].DuplicateOfPublicId);
        Assert.Equal((first[0].PublicId, false), (again[0].DuplicateOfPublicId, again[0].InScope));
        Assert.True(first[0].InScope);

        // A person can bring a duplicate back in scope.
        var back = await Sheets.UpdateSheetAsync(again[0].SheetId, new SheetEdit { InScope = true }, s.Token, Ctx(Editor));
        Assert.True(back.InScope);
    }

    [Fact]
    public async Task The_same_content_in_two_jobs_is_not_a_duplicate()
    {
        var a = await JobWithFileAsync(1);
        var b = await JobWithFileAsync(1);
        var h = Hash("shared");

        await Sheets.RegisterAsync(a.Job.JobId, a.File.PublicId, [Page(1, h)], a.Token, Ctx(Editor));
        var other = await Sheets.RegisterAsync(b.Job.JobId, b.File.PublicId, [Page(1, h)], b.Token, Ctx(Editor));

        Assert.Null(other[0].DuplicateOfPublicId);
        Assert.True(other[0].InScope);
    }

    [Fact]
    public async Task Bad_registrations_are_refused_whole_and_record_nothing()
    {
        var s = await JobWithFileAsync();
        var ctx = Ctx(Editor);

        await Assert.ThrowsAsync<JobRuleViolationException>(() => Sheets.RegisterAsync(s.Job.JobId, s.File.PublicId, [], s.Token, ctx));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Sheets.RegisterAsync(s.Job.JobId, s.File.PublicId, [Page(1), Page(1)], s.Token, ctx));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Sheets.RegisterAsync(s.Job.JobId, s.File.PublicId, [Page(0)], s.Token, ctx));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Sheets.RegisterAsync(s.Job.JobId, s.File.PublicId, [Page(1) with { WidthPts = 0 }], s.Token, ctx));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Sheets.RegisterAsync(s.Job.JobId, s.File.PublicId, [Page(1) with { Rotation = 45 }], s.Token, ctx));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Sheets.RegisterAsync(s.Job.JobId, s.File.PublicId, [Page(1, "xyz")], s.Token, ctx));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Sheets.RegisterAsync(s.Job.JobId, Guid.NewGuid(), [Page(1)], s.Token, ctx));

        await Sheets.RegisterAsync(s.Job.JobId, s.File.PublicId, [Page(1)], s.Token, ctx);
        var again = await Assert.ThrowsAsync<JobRuleViolationException>(() => Sheets.RegisterAsync(s.Job.JobId, s.File.PublicId, [Page(1), Page(2)], s.Token, ctx));
        Assert.Contains("already recorded", again.Message);
        Assert.Single(await Sheets.ListByJobAsync(s.Job.JobId));
    }

    [Fact]
    public async Task A_duplicate_file_cannot_have_pages_recorded_on_it()
    {
        var s = await JobWithFileAsync(1);
        var copy = await UploadAsync(s.Job.JobId, s.Token, 1);          // identical bytes: recorded as a copy

        Assert.NotNull(copy.DuplicateOfPublicId);
        var refused = await Assert.ThrowsAsync<JobRuleViolationException>(() => Sheets.RegisterAsync(s.Job.JobId, copy.PublicId, [Page(1)], s.Token, Ctx(Editor)));
        Assert.Contains("copy", refused.Message);
    }

    [Fact]
    public async Task A_file_of_another_job_is_not_reachable()
    {
        var a = await JobWithFileAsync(1);
        var b = await JobWithFileAsync(2);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => Sheets.RegisterAsync(b.Job.JobId, a.File.PublicId, [Page(1)], b.Token, Ctx(Editor)));
    }

    [Fact]
    public async Task Registering_needs_the_lock_and_edit_permission()
    {
        var s = await JobWithFileAsync();

        await Assert.ThrowsAsync<StaleEditTokenException>(() => Sheets.RegisterAsync(s.Job.JobId, s.File.PublicId, [Page(1)], s.Token, Ctx(Editor, "other-tab")));
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => Sheets.RegisterAsync(s.Job.JobId, s.File.PublicId, [Page(1)], s.Token, Ctx(Viewer)));
    }

    // ---- the worker path

    private async Task<(ProcessingTaskRecord Task, AuditContext Worker)> RunningTaskAsync(Setup s)
    {
        await using var connection = await Open(default);
        var producer = await ProducerStore.EnsureProducerAsync(connection, ProducerRef.Algorithm("index-" + Guid.NewGuid().ToString("N")[..6], "1.0", "h"));
        var type = "index-" + Guid.NewGuid().ToString("N")[..8];
        var tasks = new ProcessingTaskRepository(db.ConnectionString);
        await tasks.EnqueueAsync(new NewProcessingTask { JobId = s.Job.JobId, TaskType = type, ProducerId = producer }, s.Token, Ctx(Editor));
        var worker = AuditContext.ForWorker("w1");
        return ((await tasks.ClaimNextAsync(worker, [type]))!, worker);
    }

    [Fact]
    public async Task The_worker_records_pages_under_the_task_it_holds_and_the_sheets_remember_the_task()
    {
        var s = await JobWithFileAsync();
        var (task, worker) = await RunningTaskAsync(s);

        var sheets = await Sheets.RegisterFromTaskAsync(task.ProcessingTaskId, s.File.PublicId, [Page(1), Page(2)], worker);

        Assert.All(sheets, x => Assert.Equal((task.ProcessingTaskId, "w1"), (x.ProcessingTaskId, x.CreatedBy)));
    }

    [Fact]
    public async Task Another_worker_a_finished_task_or_a_cancelled_task_cannot_record_pages()
    {
        var s = await JobWithFileAsync();
        var (task, worker) = await RunningTaskAsync(s);

        await Assert.ThrowsAsync<JobRuleViolationException>(() => Sheets.RegisterFromTaskAsync(task.ProcessingTaskId, s.File.PublicId, [Page(1)], AuditContext.ForWorker("w2")));
        await Assert.ThrowsAsync<ArgumentException>(() => Sheets.RegisterFromTaskAsync(task.ProcessingTaskId, s.File.PublicId, [Page(1)], Ctx(Editor)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Sheets.RegisterFromTaskAsync(999_999, s.File.PublicId, [Page(1)], worker));

        await new ProcessingTaskRepository(db.ConnectionString).FailAsync(task.ProcessingTaskId, worker, "stop", retryable: false);
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Sheets.RegisterFromTaskAsync(task.ProcessingTaskId, s.File.PublicId, [Page(1)], worker));
    }

    // ---- editing sheets and views

    [Fact]
    public async Task A_person_edits_a_sheet_and_unchanged_values_write_nothing()
    {
        var s = await JobWithFileAsync();
        var sheet = (await Sheets.RegisterAsync(s.Job.JobId, s.File.PublicId, [Page(1)], s.Token, Ctx(Editor)))[0];

        var edited = await Sheets.UpdateSheetAsync(sheet.SheetId, new SheetEdit { SheetNumber = " M2.0 ", Kind = SheetKind.Schedule, InScope = false }, s.Token, Ctx(Editor));
        Assert.Equal(("M2.0", SheetKind.Schedule, false), (edited.SheetNumber, edited.Kind, edited.InScope));

        var before = await EventsAsync(s.Job.JobId, "sheet.edited");
        await Sheets.UpdateSheetAsync(sheet.SheetId, new SheetEdit { SheetNumber = "M2.0", Kind = SheetKind.Schedule }, s.Token, Ctx(Editor));
        Assert.Equal(before, await EventsAsync(s.Job.JobId, "sheet.edited"));
    }

    [Fact]
    public async Task A_sheet_can_hold_several_views_each_with_its_own_floor_and_building_type()
    {
        var s = await JobWithFileAsync();
        var sheet = (await Sheets.RegisterAsync(s.Job.JobId, s.File.PublicId, [Page(1)], s.Token, Ctx(Editor)))[0];
        var type = await new BuildingTypeRepository(db.ConnectionString).CreateAsync(s.Job.JobId, new NewBuildingType { Name = "Type 4" }, s.Token, Ctx(Editor));

        var plan = await Sheets.CreateViewAsync(sheet.SheetId, new NewView { Title = "Unit plan", Floor = "1", BuildingTypeId = type.BuildingTypeId }, s.Token, Ctx(Editor));
        var enlarged = await Sheets.CreateViewAsync(sheet.SheetId, new NewView { Title = "Enlarged kitchen", Type = ViewType.Detail, Polygon = Square }, s.Token, Ctx(Editor));

        Assert.Equal((type.BuildingTypeId, "1", ViewType.Plan, ViewExtraction.Manual, (double?)null), (plan.BuildingTypeId, plan.Floor, plan.Type, plan.ExtractionMethod, plan.ScaleRatio));
        Assert.Null(plan.PolygonJson);
        Assert.NotNull(enlarged.PolygonJson);
        Assert.Equal(2, (await Sheets.GetAsync(sheet.SheetId))!.Views.Count);

        var edited = await Sheets.UpdateViewAsync(enlarged.SheetViewId, new ViewEdit { Title = "Kitchen detail", ClearPolygon = true, BuildingTypeId = type.BuildingTypeId }, s.Token, Ctx(Editor));
        Assert.Equal(("Kitchen detail", (string?)null, type.BuildingTypeId), (edited.Title, edited.PolygonJson, edited.BuildingTypeId));
        var cleared = await Sheets.UpdateViewAsync(enlarged.SheetViewId, new ViewEdit { ClearBuildingType = true }, s.Token, Ctx(Editor));
        Assert.Null(cleared.BuildingTypeId);

        Assert.True(await Sheets.DeleteViewAsync(plan.SheetViewId, s.Token, Ctx(Editor)));
        Assert.Single((await Sheets.GetAsync(sheet.SheetId))!.Views);
    }

    [Fact]
    public async Task A_view_cannot_point_at_a_building_type_of_another_job_or_have_a_blank_title()
    {
        var s = await JobWithFileAsync();
        var other = await JobWithFileAsync();
        var sheet = (await Sheets.RegisterAsync(s.Job.JobId, s.File.PublicId, [Page(1)], s.Token, Ctx(Editor)))[0];
        var foreign = await new BuildingTypeRepository(db.ConnectionString).CreateAsync(other.Job.JobId, new NewBuildingType { Name = "Elsewhere" }, other.Token, Ctx(Editor));

        await Assert.ThrowsAsync<JobRuleViolationException>(() => Sheets.CreateViewAsync(sheet.SheetId, new NewView { Title = "x", BuildingTypeId = foreign.BuildingTypeId }, s.Token, Ctx(Editor)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Sheets.CreateViewAsync(sheet.SheetId, new NewView { Title = "  " }, s.Token, Ctx(Editor)));
    }

    [Theory]
    [InlineData("2points")]
    [InlineData("outside")]
    [InlineData("nan")]
    [InlineData("badpoint")]
    public async Task A_region_polygon_must_be_three_or_more_finite_points_inside_the_page(string kind)
    {
        var s = await JobWithFileAsync();
        var sheet = (await Sheets.RegisterAsync(s.Job.JobId, s.File.PublicId, [Page(1)], s.Token, Ctx(Editor)))[0];
        IReadOnlyList<double[]> polygon = kind switch
        {
            "2points" => [[1, 1], [2, 2]],
            "outside" => [[0, 0], [5000, 0], [5000, 100]],
            "nan" => [[0, 0], [double.NaN, 5], [10, 10]],
            _ => [[0, 0], [1], [10, 10]]
        };

        await Assert.ThrowsAsync<JobRuleViolationException>(() => Sheets.CreateViewAsync(sheet.SheetId, new NewView { Title = "bad", Polygon = polygon }, s.Token, Ctx(Editor)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Sheets.AddRegionAsync(sheet.SheetId, RegionKind.Include, null, polygon, s.Token, Ctx(Editor)));
    }

    // ---- scale

    [Fact]
    public async Task A_scale_is_stated_or_calibrated_by_a_person_and_says_who_and_how()
    {
        var s = await JobWithFileAsync();
        var sheet = (await Sheets.RegisterAsync(s.Job.JobId, s.File.PublicId, [Page(1)], s.Token, Ctx(Editor)))[0];
        var view = await Sheets.CreateViewAsync(sheet.SheetId, new NewView { Title = "Plan" }, s.Token, Ctx(Editor));
        Assert.Null(view.ScaleRatio);                                    // never assumed

        var stated = await Sheets.SetScaleAsync(view.SheetViewId, 48, ScaleMethod.Stated, null, s.Token, Ctx(Editor));
        Assert.Equal((48.0, ScaleMethod.Stated, Editor), (stated.ScaleRatio, stated.ScaleMethod, stated.ScaleSetBy));
        Assert.NotNull(stated.ScaleSetUtc);

        var calibrated = await Sheets.SetScaleAsync(view.SheetViewId, 47.6, ScaleMethod.Calibrated, "{\"pointsPts\":[[0,0],[720,0]],\"knownLengthFeet\":10}", s.Token, Ctx(Editor));
        Assert.Equal(ScaleMethod.Calibrated, calibrated.ScaleMethod);
        Assert.Contains("knownLengthFeet", calibrated.ScaleCalibrationJson);

        var cleared = await Sheets.ClearScaleAsync(view.SheetViewId, s.Token, Ctx(Editor));
        Assert.Equal(((double?)null, (ScaleMethod?)null, (string?)null), (cleared.ScaleRatio, cleared.ScaleMethod, cleared.ScaleSetBy));
        Assert.Equal((2, 1), (await EventsAsync(s.Job.JobId, "view.scale-set"), await EventsAsync(s.Job.JobId, "view.scale-cleared")));
    }

    [Theory]
    [InlineData(0.0, ScaleMethod.Stated, null)]
    [InlineData(-5.0, ScaleMethod.Stated, null)]
    [InlineData(double.NaN, ScaleMethod.Stated, null)]
    [InlineData(double.PositiveInfinity, ScaleMethod.Stated, null)]
    [InlineData(48.0, ScaleMethod.Calibrated, null)]
    [InlineData(48.0, ScaleMethod.Calibrated, "not json")]
    public async Task A_bad_scale_or_a_calibration_without_its_measurement_is_refused(double ratio, ScaleMethod method, string? calibration)
    {
        var s = await JobWithFileAsync();
        var sheet = (await Sheets.RegisterAsync(s.Job.JobId, s.File.PublicId, [Page(1)], s.Token, Ctx(Editor)))[0];
        var view = await Sheets.CreateViewAsync(sheet.SheetId, new NewView { Title = "Plan" }, s.Token, Ctx(Editor));

        await Assert.ThrowsAsync<JobRuleViolationException>(() => Sheets.SetScaleAsync(view.SheetViewId, ratio, method, calibration, s.Token, Ctx(Editor)));
        Assert.Null((await Sheets.GetViewAsync(s.Job.JobId, view.PublicId))!.ScaleRatio);
    }

    [Fact]
    public async Task The_database_itself_refuses_a_scale_with_no_provenance_or_an_exclusion_with_no_reason()
    {
        var s = await JobWithFileAsync();
        var sheet = (await Sheets.RegisterAsync(s.Job.JobId, s.File.PublicId, [Page(1)], s.Token, Ctx(Editor)))[0];
        var view = await Sheets.CreateViewAsync(sheet.SheetId, new NewView { Title = "Plan" }, s.Token, Ctx(Editor));

        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync("UPDATE ductwork.SheetView SET ScaleRatio = 48 WHERE SheetViewId = @v;", ("@v", view.SheetViewId)));
        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync(
            "INSERT INTO ductwork.SheetRegion (JobId, SheetId, Kind, PolygonJson, CreatedBy) VALUES (@j, @s, 'Exclude', '[]', 'x');", ("@j", s.Job.JobId), ("@s", sheet.SheetId)));
    }

    // ---- regions

    [Fact]
    public async Task Regions_are_added_and_removed_and_leaving_an_area_out_needs_a_reason()
    {
        var s = await JobWithFileAsync();
        var sheet = (await Sheets.RegisterAsync(s.Job.JobId, s.File.PublicId, [Page(1)], s.Token, Ctx(Editor)))[0];

        await Assert.ThrowsAsync<JobRuleViolationException>(() => Sheets.AddRegionAsync(sheet.SheetId, RegionKind.Exclude, "  ", Square, s.Token, Ctx(Editor)));
        var legend = await Sheets.AddRegionAsync(sheet.SheetId, RegionKind.Exclude, "Legend, not part of the plan", Square, s.Token, Ctx(Editor));
        var include = await Sheets.AddRegionAsync(sheet.SheetId, RegionKind.Include, null, Square, s.Token, Ctx(Editor));

        Assert.Equal(2, (await Sheets.GetAsync(sheet.SheetId))!.Regions.Count);
        Assert.True(await Sheets.RemoveRegionAsync(legend.SheetRegionId, s.Token, Ctx(Editor)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Sheets.RemoveRegionAsync(legend.SheetRegionId, s.Token, Ctx(Editor)));
        Assert.Equal([include.PublicId], (await Sheets.GetAsync(sheet.SheetId))!.Regions.Select(r => r.PublicId));
    }

    // ---- job rules

    [Fact]
    public async Task Nothing_here_can_change_once_the_job_is_closed_and_every_change_is_audited()
    {
        var s = await JobWithFileAsync();
        var sheet = (await Sheets.RegisterAsync(s.Job.JobId, s.File.PublicId, [Page(1)], s.Token, Ctx(Editor)))[0];
        var view = await Sheets.CreateViewAsync(sheet.SheetId, new NewView { Title = "Plan" }, s.Token, Ctx(Editor));
        await Sheets.AddRegionAsync(sheet.SheetId, RegionKind.Include, null, Square, s.Token, Ctx(Editor));

        Assert.Equal((1, 1, 1), (await EventsAsync(s.Job.JobId, "sheets.registered"), await EventsAsync(s.Job.JobId, "view.created"), await EventsAsync(s.Job.JobId, "region.added")));

        await new JobRepository(db.ConnectionString).CloseAsync(s.Job.JobId, "done", Ctx(Editor));
        await Assert.ThrowsAnyAsync<Exception>(() => Sheets.UpdateSheetAsync(sheet.SheetId, new SheetEdit { Title = "late" }, s.Token, Ctx(Editor)));
        await Assert.ThrowsAnyAsync<Exception>(() => Sheets.SetScaleAsync(view.SheetViewId, 48, ScaleMethod.Stated, null, s.Token, Ctx(Editor)));
        Assert.True((await new JobRepository(db.ConnectionString).VerifyAuditChainAsync(s.Job.JobId)).IsValid);
    }
}
