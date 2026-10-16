using System.Text.Json;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Web.Controllers.Ductwork;
using static AIToolbox.Web.Tests.Ductwork.ApiHarness;

namespace AIToolbox.Web.Tests.Ductwork;

public class SheetApiTests(DuctworkDatabase db) : IClassFixture<DuctworkDatabase>
{
    private readonly ApiHarness _h = new(db);

    private static SheetBody Page(int no, string? hash = null) =>
        new(no, 2592, 1728, 0, $"M1.{no}", $"Plan {no}", "Mechanical", "Plan", hash, true);

    private static readonly double[][] Square = [[100, 100], [900, 100], [900, 700], [100, 700]];

    private async Task<(long Job, string Tab, long Token, Guid File)> ReadyAsync(byte seed = 5)
    {
        var job = await _h.NewJobAsync();
        var (tab, token) = await _h.EditorLocksAsync(job);
        return (job, tab, token, await _h.UploadedFileAsync(job, tab, token, seed));
    }

    private async Task<Guid> OneSheetAsync(long job, string tab, long token, Guid file)
    {
        var made = await RunAsync(() => _h.SheetsApi(Editor, tab, token).Register(job, file, new RegisterSheetsRequest([Page(1)]), default));
        Assert.Equal(201, made.Status);
        return made.Body[0].GetProperty("sheetId").GetGuid();
    }

    [Fact]
    public async Task A_person_records_pages_and_everyone_on_the_job_can_list_them()
    {
        var (job, tab, token, file) = await ReadyAsync();

        var made = await RunAsync(() => _h.SheetsApi(Editor, tab, token).Register(job, file, new RegisterSheetsRequest([Page(2), Page(1)]), default));
        Assert.Equal((201, 2), (made.Status, made.Body.GetArrayLength()));
        Assert.Equal([1, 2], made.Body.EnumerateArray().Select(s => s.GetProperty("pageNo").GetInt32()));

        var list = await RunAsync(() => _h.SheetsApi(Viewer, NewTab()).List(job, default));
        Assert.Equal(2, list.Body.GetArrayLength());
        Assert.Equal("Plan", list.Body[0].GetProperty("kind").GetString());
        Assert.Equal(404, (await RunAsync(() => _h.SheetsApi(Stranger, NewTab()).List(job, default))).Status);
    }

    [Fact]
    public async Task Changes_need_the_fencing_token_and_a_bad_value_names_the_valid_ones()
    {
        var (job, tab, token, file) = await ReadyAsync();

        Assert.Equal(400, (await RunAsync(() => _h.SheetsApi(Editor, tab).Register(job, file, new RegisterSheetsRequest([Page(1)]), default))).Status);
        Assert.Equal("lock-lost", (await RunAsync(() => _h.SheetsApi(Editor, tab, token + 99).Register(job, file, new RegisterSheetsRequest([Page(1)]), default))).Code);

        var badKind = await RunAsync(() => _h.SheetsApi(Editor, tab, token).Register(job, file, new RegisterSheetsRequest([Page(1) with { Kind = "Poster" }]), default));
        Assert.Equal((400, "bad-request"), (badKind.Status, badKind.Code));
        Assert.Contains("Plan, Schedule", badKind["error"].GetString());

        var empty = await RunAsync(() => _h.SheetsApi(Editor, tab, token).Register(job, file, new RegisterSheetsRequest([]), default));
        Assert.Equal((422, "rule"), (empty.Status, empty.Code));
    }

    [Fact]
    public async Task A_viewer_cannot_edit_and_a_stranger_is_told_nothing_exists()
    {
        var (job, tab, token, file) = await ReadyAsync();
        var sheet = await OneSheetAsync(job, tab, token, file);

        var viewer = await RunAsync(() => _h.SheetsApi(Viewer, NewTab(), token).Update(job, sheet, new UpdateSheetRequest("X", null, null, null, null), default));
        Assert.True(viewer.Status is 403 or 409);
        Assert.Equal(404, (await RunAsync(() => _h.SheetsApi(Stranger, NewTab(), token).Update(job, sheet, new UpdateSheetRequest("X", null, null, null, null), default))).Status);
    }

    [Fact]
    public async Task A_sheet_is_edited_and_a_sheet_id_of_another_job_is_not_found()
    {
        var (job, tab, token, file) = await ReadyAsync();
        var sheet = await OneSheetAsync(job, tab, token, file);

        var edited = await RunAsync(() => _h.SheetsApi(Editor, tab, token).Update(job, sheet, new UpdateSheetRequest("M9.9", "Roof plan", null, "Detail", false), default));
        Assert.Equal(("M9.9", "Roof plan", "Detail", false), (edited["sheetNumber"].GetString(), edited["title"].GetString(), edited["kind"].GetString(), edited["inScope"].GetBoolean()));

        var other = await _h.NewJobAsync();
        var (otherTab, otherToken) = await _h.EditorLocksAsync(other);
        var through = await RunAsync(() => _h.SheetsApi(Editor, otherTab, otherToken).Update(other, sheet, new UpdateSheetRequest("hijack", null, null, null, null), default));
        Assert.Equal(404, through.Status);
    }

    [Fact]
    public async Task Views_scale_and_regions_work_end_to_end_and_the_scale_says_who_set_it()
    {
        var (job, tab, token, file) = await ReadyAsync();
        var sheet = await OneSheetAsync(job, tab, token, file);
        var api = () => _h.SheetsApi(Editor, tab, token);

        var view = await RunAsync(() => api().CreateView(job, sheet, new CreateViewRequest("Unit plan", "1", "Plan", null, null), default));
        Assert.Equal((201, "Unit plan", JsonValueKind.Null), (view.Status, view["title"].GetString(), view["scaleRatio"].ValueKind));
        var viewId = view["viewId"].GetGuid();

        var scaled = await RunAsync(() => api().SetScale(job, viewId, new SetScaleRequest(48, "Stated", null), default));
        Assert.Equal((48.0, "Stated", Editor), (scaled["scaleRatio"].GetDouble(), scaled["scaleMethod"].GetString(), scaled["scaleSetBy"].GetString()));

        var calibration = JsonDocument.Parse("{\"knownLengthFeet\":10}").RootElement;
        var calibrated = await RunAsync(() => api().SetScale(job, viewId, new SetScaleRequest(47.5, "Calibrated", calibration), default));
        Assert.Equal(10, calibrated["scaleCalibration"].GetProperty("knownLengthFeet").GetInt32());

        var noMeasurement = await RunAsync(() => api().SetScale(job, viewId, new SetScaleRequest(47.5, "Calibrated", null), default));
        Assert.Equal((422, "rule"), (noMeasurement.Status, noMeasurement.Code));
        Assert.Equal(400, (await RunAsync(() => api().SetScale(job, viewId, new SetScaleRequest(48, "Guessed", null), default))).Status);

        var cleared = await RunAsync(() => api().ClearScale(job, viewId, default));
        Assert.Equal(JsonValueKind.Null, cleared["scaleRatio"].ValueKind);

        var region = await RunAsync(() => api().AddRegion(job, sheet, new AddRegionRequest("Exclude", "Legend", Square), default));
        Assert.Equal((201, "Exclude", 4), (region.Status, region["kind"].GetString(), region["polygon"].GetArrayLength()));
        var noReason = await RunAsync(() => api().AddRegion(job, sheet, new AddRegionRequest("Exclude", null, Square), default));
        Assert.Equal((422, "rule"), (noReason.Status, noReason.Code));
        Assert.Equal(400, (await RunAsync(() => api().AddRegion(job, sheet, new AddRegionRequest("Maybe", null, Square), default))).Status);

        var withEverything = (await RunAsync(() => _h.SheetsApi(Viewer, NewTab()).List(job, default))).Body[0];
        Assert.Equal((1, 1), (withEverything.GetProperty("views").GetArrayLength(), withEverything.GetProperty("regions").GetArrayLength()));

        Assert.True((await RunAsync(() => api().RemoveRegion(job, region["regionId"].GetGuid(), default)))["removed"].GetBoolean());
        Assert.True((await RunAsync(() => api().DeleteView(job, viewId, default)))["deleted"].GetBoolean());
    }

    [Fact]
    public async Task A_view_or_region_of_another_job_is_not_reachable()
    {
        var (job, tab, token, file) = await ReadyAsync();
        var sheet = await OneSheetAsync(job, tab, token, file);
        var viewId = (await RunAsync(() => _h.SheetsApi(Editor, tab, token).CreateView(job, sheet, new CreateViewRequest("v", null, null, null, null), default)))["viewId"].GetGuid();

        var other = await _h.NewJobAsync();
        var (otherTab, otherToken) = await _h.EditorLocksAsync(other);
        Assert.Equal(404, (await RunAsync(() => _h.SheetsApi(Editor, otherTab, otherToken).DeleteView(other, viewId, default))).Status);
        Assert.Equal(404, (await RunAsync(() => _h.SheetsApi(Editor, otherTab, otherToken).SetScale(other, viewId, new SetScaleRequest(48, "Stated", null), default))).Status);
        Assert.Equal(404, (await RunAsync(() => _h.SheetsApi(Editor, otherTab, otherToken).RemoveRegion(other, Guid.NewGuid(), default))).Status);
    }

    // ---- worker

    [Fact]
    public async Task The_worker_reports_pages_under_its_task_and_a_worker_without_the_task_is_refused()
    {
        var (job, tab, token, file) = await ReadyAsync();
        var producerRef = ProducerRef.Algorithm("indexer-" + Guid.NewGuid().ToString("N")[..6], "1.0", "h");
        var producer = await _h.ProducerAsync(producerRef);
        var type = "index-" + Guid.NewGuid().ToString("N")[..8];
        await _h.BindingRepo.BindAsync(type, producer, await _h.EvalRunAsync(producer), false, 3, AIToolbox.Ductwork.Core.Jobs.AuditContext.ForUser(Admin, sessionId: "setup", isAdmin: true));
        await RunAsync(() => _h.JobsApi(Editor, tab, token).EnqueueTask(job, new EnqueueTaskRequest(type, null, null), default));
        var claimed = await RunAsync(() => _h.WorkerApi().Claim(new WorkerClaimRequest("w1", [type]), default));
        var taskId = claimed["taskId"].GetInt64();

        var body = new WorkerSheetsRequest("w1", file, [Page(1, new string('a', 64)), Page(2, new string('b', 64))]);
        var stolen = await RunAsync(() => _h.WorkerApi().RegisterSheets(taskId, body with { WorkerName = "w2" }, default));
        Assert.Equal((422, "rule"), (stolen.Status, stolen.Code));

        var ok = await RunAsync(() => _h.WorkerApi().RegisterSheets(taskId, body, default));
        Assert.Equal((201, 2, 0), (ok.Status, ok["recorded"].GetInt32(), ok["duplicates"].GetInt32()));

        var list = await RunAsync(() => _h.SheetsApi(Viewer, NewTab()).List(job, default));
        Assert.Equal(2, list.Body.GetArrayLength());
    }
}
