using System.Text.Json;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Web.Controllers.Ductwork;
using static AIToolbox.Web.Tests.Ductwork.ApiHarness;

namespace AIToolbox.Web.Tests.Ductwork;

/// <summary>What the workbench screens rely on from the server, beyond the API each tab already had.</summary>
public class UiBackingTests(DuctworkDatabase db) : IClassFixture<DuctworkDatabase>
{
    private readonly ApiHarness _h = new(db);

    private static DetectionProposal Boot(string id, string size, string view) => new()
    {
        DetectionId = id, Type = "Boot", Size = size, BuildingType = "Type 4", Quantity = 1, Confidence = 0.95,
        Method = DetectionMethod.Text, Family = DetectorFamily.LabelFlagCount, Box = new BoundingBox(10, 20, 40, 50), SheetViewRef = view, Evidence = "label"
    };

    private async Task<(long Job, string Tab, long Token, string Type, ProducerRef Producer)> BoundJobAsync()
    {
        var producerRef = ProducerRef.Algorithm("ui-" + Guid.NewGuid().ToString("N")[..6], "1.0", "h");
        var producer = await _h.ProducerAsync(producerRef);
        var type = "detect-" + Guid.NewGuid().ToString("N")[..8];
        await _h.BindingRepo.BindAsync(type, producer, await _h.EvalRunAsync(producer), false, 3, AIToolbox.Ductwork.Core.Jobs.AuditContext.ForUser(Admin, sessionId: "setup", isAdmin: true));
        var job = await _h.NewJobAsync();
        var (tab, token) = await _h.EditorLocksAsync(job);
        return (job, tab, token, type, producerRef);
    }

    private async Task ReportAsync(long job, string tab, long token, string type, ProducerRef producerRef, params DetectionProposal[] detections)
    {
        await RunAsync(() => _h.JobsApi(Editor, tab, token).EnqueueTask(job, new EnqueueTaskRequest(type, null, null), default));
        var claimed = await RunAsync(() => _h.WorkerApi().Claim(new WorkerClaimRequest("w1", [type]), default));
        var manifest = JsonSerializer.Serialize(new TaskResultManifest
        {
            ContractVersion = ContractInfo.Version, TaskType = type, TaskPublicId = claimed["taskPublicId"].GetString()!, Producer = producerRef, Detections = detections,
            Findings = [new FindingProposal { FindingId = "f1", Kind = FindingKind.GrammarVariant, Question = "Depth?", DetectionId = detections[0].DetectionId, Candidates = ["4x12x7", "4x12x4"] }]
        }, ContractJson.Options);
        Assert.True((await RunAsync(() => _h.WorkerApi(body: new MemoryStream(System.Text.Encoding.UTF8.GetBytes(manifest))).Result(claimed["taskId"].GetInt64(), "w1", null, default))).Ok);
    }

    // ---- the pages

    [Fact]
    public async Task The_job_page_is_served_to_a_member_and_is_not_found_for_anyone_else()
    {
        var job = await _h.NewJobAsync();

        var member = await RunAsync(() => _h.AppPages(Editor).Job(job, default));
        Assert.Equal((200, job), (member.Status, member["model"].GetProperty("jobId").GetInt64()));
        Assert.Equal(Editor, member["model"].GetProperty("email").GetString());

        Assert.Equal(404, (await RunAsync(() => _h.AppPages(Stranger).Job(job, default))).Status);
        Assert.Equal(404, (await RunAsync(() => _h.AppPages(Stranger, admin: true).Job(job, default))).Status);   // an administrator sees only jobs they are on
        Assert.Equal(404, (await RunAsync(() => _h.AppPages(Editor).Job(long.MaxValue, default))).Status);
    }

    [Fact]
    public async Task The_job_list_and_settings_pages_tell_the_script_who_is_asking()
    {
        var list = await RunAsync(() => _h.AppPages(Editor).Index(default));
        Assert.Equal((200, false), (list.Status, list["model"].GetProperty("isAdmin").GetBoolean()));
        var admin = await RunAsync(() => Task.FromResult(_h.AppPages(Admin, admin: true).Admin()));
        Assert.True(admin["model"].GetProperty("isAdmin").GetBoolean());
    }

    // ---- the catalog

    [Fact]
    public async Task Task_types_lists_only_what_an_administrator_has_switched_on_and_whether_it_sends_drawings()
    {
        var (_, _, _, type, _) = await BoundJobAsync();

        var reply = await RunAsync(() => _h.CatalogApi(Viewer).TaskTypes(default));

        var mine = reply.Body.EnumerateArray().Single(t => t.GetProperty("taskType").GetString() == type);
        Assert.Equal((false, 3), (mine.GetProperty("sendsDrawingContent").GetBoolean(), mine.GetProperty("maxAttempts").GetInt32()));
        var retired = await _h.BindingRepo.RetireAsync(type, AIToolbox.Ductwork.Core.Jobs.AuditContext.ForUser(Admin, sessionId: "s", isAdmin: true));
        Assert.True(retired);
        Assert.DoesNotContain((await RunAsync(() => _h.CatalogApi(Viewer).TaskTypes(default))).Body.EnumerateArray(), t => t.GetProperty("taskType").GetString() == type);
    }

    [Fact]
    public async Task The_admin_page_can_list_producers_that_have_been_measured_with_their_runs()
    {
        var producerRef = ProducerRef.Algorithm("measured-" + Guid.NewGuid().ToString("N")[..6], "2.1", "h");
        var producer = await _h.ProducerAsync(producerRef);
        var run = await _h.EvalRunAsync(producer);
        await _h.ProducerAsync(ProducerRef.Algorithm("unmeasured-" + Guid.NewGuid().ToString("N")[..6], "1.0", "h"));

        var reply = await RunAsync(() => _h.AdminApi().Producers(default));

        var found = reply.Body.EnumerateArray().Single(p => p.GetProperty("producerId").GetInt64() == producer);
        Assert.Equal((200, $"{producerRef.AlgorithmName} 2.1", "Algorithm", run), (reply.Status, found.GetProperty("label").GetString(), found.GetProperty("kind").GetString(), found.GetProperty("evalRuns")[0].GetProperty("evalRunId").GetInt32()));
        Assert.DoesNotContain(reply.Body.EnumerateArray(), p => p.GetProperty("label").GetString()!.StartsWith("unmeasured-"));   // nothing to bind without a measurement
    }

    // ---- what the review screen needs about items and questions

    [Fact]
    public async Task A_questions_candidates_are_part_of_what_the_screen_is_given()
    {
        var (job, tab, token, type, producerRef) = await BoundJobAsync();
        await ReportAsync(job, tab, token, type, producerRef, Boot("d1", "4x12", "M1.1"));

        var findings = await RunAsync(() => _h.RulesApi(Viewer, NewTab()).ListFindings(job, null, default));

        Assert.Equal(["4x12x7", "4x12x4"], findings.Body[0].GetProperty("candidates").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public async Task An_item_is_placed_on_a_view_by_the_views_public_id_and_then_says_which_view_and_sheet_it_is_on()
    {
        var (job, tab, token, type, producerRef) = await BoundJobAsync();
        await ReportAsync(job, tab, token, type, producerRef, Boot("d1", "4x10x7", "M1.1"), Boot("d2", "4x12x7", "M1.1"));
        var file = await _h.UploadedFileAsync(job, tab, token);
        var sheets = await RunAsync(() => _h.SheetsApi(Editor, tab, token).Register(job, file, new RegisterSheetsRequest([new SheetBody(1, 612, 792, 0, "M1.1", "One", null, "Plan", null, true)]), default));
        var sheetId = sheets.Body[0].GetProperty("sheetId").GetGuid();
        var viewId = (await RunAsync(() => _h.SheetsApi(Editor, tab, token).CreateView(job, sheetId, new CreateViewRequest("Whole page", null, "Plan", null, null), default)))["viewId"].GetGuid();
        var items = await RunAsync(() => _h.JobsApi(Viewer).ListEntities(job, null, null, null, null, null, default));
        var first = items.Body[0];
        Assert.Equal(JsonValueKind.Null, first.GetProperty("viewId").ValueKind);                   // not placed yet

        var placed = await RunAsync(() => _h.ReviewApi(Editor, tab, token).Edit(job, first.GetProperty("publicId").GetGuid(),
            new EditEntityRequest(first.GetProperty("revision").GetInt32(), null, null, null, null, null, null, null, null, null, null, null, false, viewId, false), default));

        Assert.Equal((200, viewId, sheetId), (placed.Status, placed["viewId"].GetGuid(), placed["sheetId"].GetGuid()));
        var again = await RunAsync(() => _h.JobsApi(Viewer).ListEntities(job, null, null, null, null, null, default));
        Assert.Equal(viewId, again.Body[0].GetProperty("viewId").GetGuid());
    }

    [Fact]
    public async Task A_view_of_another_job_cannot_be_used_to_place_an_item_or_add_one()
    {
        var (job, tab, token, type, producerRef) = await BoundJobAsync();
        await ReportAsync(job, tab, token, type, producerRef, Boot("d1", "4x10x7", "M1.1"));
        var (other, otherTab, otherToken, _, _) = await BoundJobAsync();
        var file = await _h.UploadedFileAsync(other, otherTab, otherToken, 11);
        var sheets = await RunAsync(() => _h.SheetsApi(Editor, otherTab, otherToken).Register(other, file, new RegisterSheetsRequest([new SheetBody(1, 612, 792, 0, "M9.9", null, null, "Plan", null, true)]), default));
        var foreign = (await RunAsync(() => _h.SheetsApi(Editor, otherTab, otherToken).CreateView(other, sheets.Body[0].GetProperty("sheetId").GetGuid(), new CreateViewRequest("Theirs", null, "Plan", null, null), default)))["viewId"].GetGuid();
        var item = (await RunAsync(() => _h.JobsApi(Viewer).ListEntities(job, null, null, null, null, null, default))).Body[0];

        var place = await RunAsync(() => _h.ReviewApi(Editor, tab, token).Edit(job, item.GetProperty("publicId").GetGuid(),
            new EditEntityRequest(1, null, null, null, null, null, null, null, null, null, null, null, false, foreign, false), default));
        var add = await RunAsync(() => _h.ReviewApi(Editor, tab, token).Add(job, new AddEntityRequest("Damper", null, null, null, null, null, null, 1, "EA", null, foreign, null, null, null, null), default));

        Assert.Equal(404, place.Status);
        Assert.Equal(404, add.Status);
    }
}
