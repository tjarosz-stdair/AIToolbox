using System.Text.Json;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Data;
using AIToolbox.Web.Controllers.Ductwork;
using AIToolbox.Web.Models;
using AIToolbox.Web.Services;
using static AIToolbox.Web.Tests.Ductwork.ApiHarness;

namespace AIToolbox.Web.Tests.Ductwork;

public class VisionApiTests(DuctworkDatabase db) : IClassFixture<DuctworkDatabase>
{
    private const int ProviderId = 7;
    private readonly ApiHarness _h = new(db);

    private static VisionPolicyBody Policy(bool enabled = true, int[]? providers = null, string approver = "EditPermission", int maxPages = 5, string[]? standing = null, int standingPages = 0) =>
        new(enabled, providers ?? [ProviderId], approver, null, maxPages, 3, true, 60, standing ?? [], standingPages);

    private static string Base64(int size) => Convert.ToBase64String(Enumerable.Repeat((byte)7, size).ToArray());

    private async Task SetPolicyAsync(VisionPolicyBody body)
    {
        var reply = await RunAsync(() => _h.VisionAdminApi().Set(body, default));
        Assert.True(reply.Ok, reply.Body.ToString());
    }

    /// <summary>A job with a recorded sheet, a task type bound to the configured vision model, and an editor holding the lock.</summary>
    private async Task<(long Job, string Tab, long Token, List<Guid> Sheets, string TaskType, int NoticeVersion)> JobAsync(AiProvider? provider = null)
    {
        provider ??= FakeProviders.Vision(ProviderId);
        if (_h.Providers.All.All(p => p.Id != provider.Id)) _h.Providers.All.Add(provider);
        await SetPolicyAsync(Policy());

        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        var snapshot = await ProducerStore.EnsureProviderSnapshotAsync(connection, provider.ToSnapshot());
        var producer = await ProducerStore.EnsureProducerAsync(connection, ProducerRef.Llm(9000 + snapshot, snapshot));
        var type = "classify-" + Guid.NewGuid().ToString("N")[..8];
        await _h.BindingRepo.BindAsync(type, producer, await _h.EvalRunAsync(producer), true, 2, AIToolbox.Ductwork.Core.Jobs.AuditContext.ForUser(Admin, sessionId: "setup", isAdmin: true));

        var job = await _h.NewJobAsync();
        var (tab, token) = await _h.EditorLocksAsync(job);
        var file = await _h.UploadedFileAsync(job, tab, token);
        var sheets = await RunAsync(() => _h.SheetsApi(Editor, tab, token).Register(job, file, new RegisterSheetsRequest(
            [new SheetBody(1, 2592, 1728, 0, "M1.1", "One", null, "Plan", null, true), new SheetBody(2, 2592, 1728, 0, "M1.2", "Two", null, "Plan", null, true)]), default));
        var notice = (await RunAsync(() => _h.VisionApi(Editor, NewTab()).Policy(default)))["noticeVersion"].GetInt32();
        return (job, tab, token, sheets.Body.EnumerateArray().Select(s => s.GetProperty("sheetId").GetGuid()).ToList(), type, notice);
    }

    private async Task<Guid> ApproveAsync(long job, List<Guid> sheets, int notice, int count = 2, string? reason = "Local methods could not read it.")
    {
        var reply = await RunAsync(() => _h.VisionApi(Editor, NewTab()).Approve(job, new CreateVisionApprovalRequest(sheets.Take(count).ToArray(), ProviderId, reason, notice), default));
        Assert.Equal(201, reply.Status);
        return reply["approvalId"].GetGuid();
    }

    private async Task<(long TaskId, Guid TaskPublic)> RunningAsync(long job, string tab, long token, string type, Guid approval)
    {
        var queued = await RunAsync(() => _h.JobsApi(Editor, tab, token).EnqueueTask(job, new EnqueueTaskRequest(type, null, approval), default));
        Assert.True(queued.Ok, queued.Body.ToString());
        var claimed = await RunAsync(() => _h.WorkerApi().Claim(new WorkerClaimRequest("w1", [type]), default));
        return (claimed["taskId"].GetInt64(), claimed["taskPublicId"].GetGuid());
    }

    private Task<Reply> SendAsync(long taskId, Guid[] sheets, int images = 1, int size = 100, string worker = "w1", string directive = "classify the crop") =>
        RunAsync(() => _h.WorkerApi().Vision(taskId, new WorkerVisionRequest(worker, directive, sheets,
            Enumerable.Range(0, images).Select(i => new VisionImageBody("crop " + i, "image/png", Base64(size))).ToArray()), default));

    // ---- the administrator

    [Fact]
    public async Task The_administrator_allows_only_providers_that_can_carry_drawings()
    {
        _h.Providers.All.Add(FakeProviders.Vision(7));
        _h.Providers.All.Add(FakeProviders.Vision(8, allowed: false));
        _h.Providers.All.Add(FakeProviders.Vision(9, supportsVision: false));
        _h.Providers.All.Add(FakeProviders.Vision(10, route: null));

        Assert.True((await RunAsync(() => _h.VisionAdminApi().Set(Policy(providers: [7]), default))).Ok);
        foreach (var bad in new[] { 8, 9, 10, 99 })
            Assert.Equal((422, "rule"), ((await RunAsync(() => _h.VisionAdminApi().Set(Policy(providers: [bad]), default))) is { } r ? (r.Status, r.Code) : (0, "")));

        var view = await RunAsync(() => _h.VisionAdminApi().Get(default));
        var eligible = view["providers"].EnumerateArray().ToDictionary(p => p.GetProperty("providerId").GetInt32(), p => p.GetProperty("eligible").GetBoolean());
        Assert.Equal((true, false, false, false), (eligible[7], eligible[8], eligible[9], eligible[10]));
        Assert.Contains("governance route", view["providers"].EnumerateArray().Single(p => p.GetProperty("providerId").GetInt32() == 10).GetProperty("whyNot").GetString());
    }

    [Fact]
    public async Task A_bad_approver_rule_or_limit_is_refused()
    {
        _h.Providers.All.Add(FakeProviders.Vision(7));

        Assert.Equal(400, (await RunAsync(() => _h.VisionAdminApi().Set(Policy(approver: "Anyone"), default))).Status);
        Assert.Equal(422, (await RunAsync(() => _h.VisionAdminApi().Set(Policy(maxPages: 0), default))).Status);
    }

    [Fact]
    public async Task What_people_see_is_the_switch_the_notice_and_only_the_models_they_may_use()
    {
        _h.Providers.All.Add(FakeProviders.Vision(7));
        _h.Providers.All.Add(FakeProviders.Vision(8, allowed: false));
        await SetPolicyAsync(Policy());

        var summary = await RunAsync(() => _h.VisionApi(Viewer, NewTab()).Policy(default));

        Assert.True(summary["enabled"].GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(summary["noticeText"].GetString()));
        Assert.Equal([7], summary["providers"].EnumerateArray().Select(p => p.GetProperty("providerId").GetInt32()));
    }

    // ---- the approval dialog

    [Fact]
    public async Task The_disclosure_names_the_sheets_the_model_the_route_and_the_notice_before_anything_is_approved()
    {
        var (job, _, _, sheets, _, notice) = await JobAsync();

        var disclosure = await RunAsync(() => _h.VisionApi(Editor, NewTab()).Disclosure(job, new VisionDisclosureRequest(sheets.ToArray(), ProviderId), default));

        Assert.Equal((true, 2, 6, notice), (disclosure["canApprove"].GetBoolean(), disclosure["pageCount"].GetInt32(), disclosure["imageBudget"].GetInt32(), disclosure["noticeVersion"].GetInt32()));
        Assert.Equal(("vision-1", "Microsoft Foundry", "foundry.example.invalid"), (disclosure["provider"].GetProperty("model").GetString(), disclosure["provider"].GetProperty("governanceRoute").GetString(), disclosure["provider"].GetProperty("host").GetString()));
        Assert.Equal(["M1.1", "M1.2"], disclosure["sheets"].EnumerateArray().Select(s => s.GetProperty("sheetNumber").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(disclosure["noticeText"].GetString()));
        Assert.DoesNotContain("apiKey", disclosure.Body.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var viewer = await RunAsync(() => _h.VisionApi(Viewer, NewTab()).Disclosure(job, new VisionDisclosureRequest(sheets.ToArray(), ProviderId), default));
        Assert.False(viewer["canApprove"].GetBoolean());
        Assert.Equal(404, (await RunAsync(() => _h.VisionApi(Stranger, NewTab()).Disclosure(job, new VisionDisclosureRequest(sheets.ToArray(), ProviderId), default))).Status);
    }

    [Fact]
    public async Task Approving_needs_a_reason_the_current_notice_and_an_allowed_model_and_a_viewer_cannot()
    {
        var (job, _, _, sheets, _, notice) = await JobAsync();
        var api = () => _h.VisionApi(Editor, NewTab());

        Assert.Equal(422, (await RunAsync(() => api().Approve(job, new CreateVisionApprovalRequest(sheets.ToArray(), ProviderId, " ", notice), default))).Status);
        Assert.Equal(422, (await RunAsync(() => api().Approve(job, new CreateVisionApprovalRequest(sheets.ToArray(), ProviderId, "x", notice - 1), default))).Status);
        Assert.Equal(422, (await RunAsync(() => api().Approve(job, new CreateVisionApprovalRequest(sheets.ToArray(), 99, "x", notice), default))).Status);
        Assert.Equal(403, (await RunAsync(() => _h.VisionApi(Viewer, NewTab()).Approve(job, new CreateVisionApprovalRequest(sheets.ToArray(), ProviderId, "x", notice), default))).Status);
        Assert.Equal(404, (await RunAsync(() => _h.VisionApi(Stranger, NewTab()).Approve(job, new CreateVisionApprovalRequest(sheets.ToArray(), ProviderId, "x", notice), default))).Status);
        Assert.Empty((await RunAsync(() => api().List(job, default))).Body.EnumerateArray());
    }

    // ---- the whole path

    [Fact]
    public async Task An_approved_run_sends_images_through_the_gateway_to_the_approved_model_and_records_only_counts()
    {
        var (job, tab, token, sheets, type, notice) = await JobAsync();
        var approval = await ApproveAsync(job, sheets, notice);
        var (taskId, _) = await RunningAsync(job, tab, token, type, approval);
        _h.ProviderClient.OnImage = r => new DocumentPromptResult { ProviderId = r.Provider.Id, ProviderName = r.Provider.DisplayName, ResponseText = "{\"type\":\"Boot\"}", InputTokens = 12, OutputTokens = 3 };

        var sent = await SendAsync(taskId, sheets.Take(1).ToArray(), images: 2, size: 200);

        Assert.Equal((200, "{\"type\":\"Boot\"}", 2, "vision-1"), (sent.Status, sent["text"].GetString(), sent["imagesSent"].GetInt32(), sent["model"].GetString()));
        var request = _h.ProviderClient.Sent.Single();
        Assert.Equal((ProviderId, "classify the crop", 2, 200), (request.Provider.Id, request.Directive, request.Images.Count, request.Images[0].Bytes.Length));

        var list = await RunAsync(() => _h.VisionApi(Viewer, NewTab()).List(job, default));
        Assert.Equal(("Used", 2), (list.Body[0].GetProperty("status").GetString(), list.Body[0].GetProperty("imagesSent").GetInt32()));
        var log = await RunAsync(() => _h.VisionApi(Viewer, NewTab()).Transmissions(job, default));
        Assert.Equal(("Sent", 2), (log.Body[0].GetProperty("outcome").GetString(), log.Body[0].GetProperty("images").GetInt32()));
        Assert.DoesNotContain("classify the crop", log.Body.GetRawText());
    }

    [Fact]
    public async Task The_gateway_refuses_with_a_reason_when_the_budget_the_sheets_or_the_worker_are_wrong()
    {
        var (job, tab, token, sheets, type, notice) = await JobAsync();
        var approval = await ApproveAsync(job, sheets, notice, count: 1);              // one page, three images
        var (taskId, _) = await RunningAsync(job, tab, token, type, approval);

        var over = await SendAsync(taskId, sheets.Take(1).ToArray(), images: 4);
        Assert.Equal((403, "vision-refused", "budget"), (over.Status, over.Code, over["reason"].GetString()));
        var stray = await SendAsync(taskId, [sheets[1]]);
        Assert.Equal("sheet-not-approved", stray["reason"].GetString());
        var other = await SendAsync(taskId, sheets.Take(1).ToArray(), worker: "w2");
        Assert.Equal("not-running", other["reason"].GetString());
        Assert.Empty(_h.ProviderClient.Sent);                                           // nothing left the server
    }

    [Fact]
    public async Task A_bad_request_is_a_400_before_anything_is_reserved()
    {
        var (job, tab, token, sheets, type, notice) = await JobAsync();
        var (taskId, _) = await RunningAsync(job, tab, token, type, await ApproveAsync(job, sheets, notice));

        var badType = await RunAsync(() => _h.WorkerApi().Vision(taskId, new WorkerVisionRequest("w1", "d", [sheets[0]], [new VisionImageBody("x", "application/pdf", Base64(10))]), default));
        var badData = await RunAsync(() => _h.WorkerApi().Vision(taskId, new WorkerVisionRequest("w1", "d", [sheets[0]], [new VisionImageBody("x", "image/png", "not base64!!")]), default));
        var tooBig = await SendAsync(taskId, [sheets[0]], size: 6 * 1024 * 1024);
        var noImages = await SendAsync(taskId, [sheets[0]], images: 0);
        var noDirective = await SendAsync(taskId, [sheets[0]], directive: " ");

        Assert.All(new[] { badType, badData, tooBig, noImages, noDirective }, r => Assert.Equal(400, r.Status));
        var approvalList = await RunAsync(() => _h.VisionApi(Viewer, NewTab()).List(job, default));
        Assert.Equal(0, approvalList.Body[0].GetProperty("imagesSent").GetInt32());
    }

    [Fact]
    public async Task A_revoked_approval_or_the_switch_turned_off_stops_the_next_send_at_once()
    {
        var (job, tab, token, sheets, type, notice) = await JobAsync();
        var approval = await ApproveAsync(job, sheets, notice);
        var (taskId, _) = await RunningAsync(job, tab, token, type, approval);
        Assert.Equal(200, (await SendAsync(taskId, [sheets[0]])).Status);

        await SetPolicyAsync(Policy(enabled: false));
        var off = await SendAsync(taskId, [sheets[0]]);
        Assert.Equal((403, "disabled"), (off.Status, off["reason"].GetString()));

        var approvals = await RunAsync(() => _h.VisionApi(Viewer, NewTab()).List(job, default));
        Assert.Equal("Revoked", approvals.Body[0].GetProperty("status").GetString());
        Assert.Equal(1, _h.ProviderClient.Sent.Count);
    }

    [Fact]
    public async Task An_approver_can_revoke_with_a_reason_before_the_run_uses_it()
    {
        var (job, tab, token, sheets, type, notice) = await JobAsync();
        var approval = await ApproveAsync(job, sheets, notice);

        Assert.Equal(422, (await RunAsync(() => _h.VisionApi(Editor, NewTab()).Revoke(job, approval, new RevokeVisionApprovalRequest(" "), default))).Status);
        var revoked = await RunAsync(() => _h.VisionApi(Editor, NewTab()).Revoke(job, approval, new RevokeVisionApprovalRequest("Wrong sheets"), default));
        Assert.Equal(("Revoked", "Wrong sheets"), (revoked["status"].GetString(), revoked["revokeReason"].GetString()));

        var queued = await RunAsync(() => _h.JobsApi(Editor, tab, token).EnqueueTask(job, new EnqueueTaskRequest(type, null, approval), default));
        Assert.Equal((422, "rule"), (queued.Status, queued.Code));
        Assert.Equal(404, (await RunAsync(() => _h.JobsApi(Editor, tab, token).EnqueueTask(job, new EnqueueTaskRequest(type, null, Guid.NewGuid()), default))).Status);
    }

    [Fact]
    public async Task If_the_provider_was_edited_after_approval_nothing_is_sent_and_the_images_go_back_to_the_budget()
    {
        var provider = FakeProviders.Vision(ProviderId);
        var (job, tab, token, sheets, type, notice) = await JobAsync(provider);
        var (taskId, _) = await RunningAsync(job, tab, token, type, await ApproveAsync(job, sheets, notice));

        provider.ModelName = "a-different-model";                                      // an administrator changed the provider since
        var refused = await SendAsync(taskId, [sheets[0]]);

        Assert.Equal((403, "provider"), (refused.Status, refused["reason"].GetString()));
        Assert.Empty(_h.ProviderClient.Sent);
        var approvals = await RunAsync(() => _h.VisionApi(Viewer, NewTab()).List(job, default));
        Assert.Equal(0, approvals.Body[0].GetProperty("imagesSent").GetInt32());
        var log = await RunAsync(() => _h.VisionApi(Viewer, NewTab()).Transmissions(job, default));
        Assert.Equal("Failed", log.Body[0].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_provider_that_fails_after_the_send_is_a_502_and_the_images_stay_counted()
    {
        var (job, tab, token, sheets, type, notice) = await JobAsync();
        var (taskId, _) = await RunningAsync(job, tab, token, type, await ApproveAsync(job, sheets, notice));
        _h.ProviderClient.OnImage = r => new DocumentPromptResult { ProviderId = r.Provider.Id, ProviderName = r.Provider.DisplayName, ErrorMessage = "The provider timed out." };

        var failed = await SendAsync(taskId, [sheets[0]], images: 2);

        Assert.Equal((502, "provider-error"), (failed.Status, failed.Code));
        var approvals = await RunAsync(() => _h.VisionApi(Viewer, NewTab()).List(job, default));
        Assert.Equal(2, approvals.Body[0].GetProperty("imagesSent").GetInt32());
    }

    [Fact]
    public async Task A_request_the_provider_client_refuses_before_sending_gives_its_images_back()
    {
        var (job, tab, token, sheets, type, notice) = await JobAsync();
        var (taskId, _) = await RunningAsync(job, tab, token, type, await ApproveAsync(job, sheets, notice));
        _h.ProviderClient.OnImage = _ => throw new ImagePromptRejectedException("This provider has no image route.");

        var refused = await SendAsync(taskId, [sheets[0]], images: 2);

        Assert.Equal((400, "image-rejected"), (refused.Status, refused["reason"].GetString()));
        Assert.Equal(0, (await RunAsync(() => _h.VisionApi(Viewer, NewTab()).List(job, default))).Body[0].GetProperty("imagesSent").GetInt32());
    }

    [Fact]
    public async Task A_task_type_approved_in_advance_by_the_administrator_runs_without_a_per_run_approval_within_its_limit()
    {
        var (job, tab, token, sheets, type, _) = await JobAsync();
        await SetPolicyAsync(Policy(standing: [type], standingPages: 2));

        var queued = await RunAsync(() => _h.JobsApi(Editor, tab, token).EnqueueTask(job, new EnqueueTaskRequest(type, null, null), default));
        Assert.True(queued.Ok, queued.Body.ToString());
        var approvals = await RunAsync(() => _h.VisionApi(Viewer, NewTab()).List(job, default));
        Assert.Equal(("Standing", "Used", 2), (approvals.Body[0].GetProperty("kind").GetString(), approvals.Body[0].GetProperty("status").GetString(), approvals.Body[0].GetProperty("pageCount").GetInt32()));
        _ = sheets;
    }

    [Fact]
    public async Task Without_the_switch_on_a_task_that_sends_drawings_cannot_even_be_queued()
    {
        var (job, tab, token, sheets, type, notice) = await JobAsync();
        var approval = await ApproveAsync(job, sheets, notice);
        await SetPolicyAsync(Policy(enabled: false));

        var queued = await RunAsync(() => _h.JobsApi(Editor, tab, token).EnqueueTask(job, new EnqueueTaskRequest(type, null, approval), default));

        Assert.Equal((422, "rule"), (queued.Status, queued.Code));
        Assert.Contains("switched off", queued["error"].GetString());
    }
}
