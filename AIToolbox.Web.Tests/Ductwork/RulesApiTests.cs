using System.Text.Json;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Rules;
using AIToolbox.Web.Controllers.Ductwork;
using static AIToolbox.Web.Tests.Ductwork.ApiHarness;

namespace AIToolbox.Web.Tests.Ductwork;

public class RulesApiTests(DuctworkDatabase db) : IClassFixture<DuctworkDatabase>
{
    private readonly ApiHarness _h = new(db);

    private static DetectionProposal Boot(string id, string size) => new()
    {
        DetectionId = id, Type = "Boot", Size = size, BuildingType = "Type 4", Quantity = 1, Confidence = 0.95,
        Method = DetectionMethod.Text, Family = DetectorFamily.LabelFlagCount, SheetViewRef = "M1.2", Evidence = "label"
    };

    /// <summary>A job where a worker reported two 4x12 boots, one of them with a question attached.</summary>
    private async Task<(long Job, string Tab, long Token, List<Guid> Items)> JobAsync()
    {
        var producerRef = ProducerRef.Algorithm("rules-" + Guid.NewGuid().ToString("N")[..6], "1.0", "h");
        var producer = await _h.ProducerAsync(producerRef);
        var type = "detect-" + Guid.NewGuid().ToString("N")[..8];
        await _h.BindingRepo.BindAsync(type, producer, await _h.EvalRunAsync(producer), false, 3, AIToolbox.Ductwork.Core.Jobs.AuditContext.ForUser(Admin, sessionId: "setup", isAdmin: true));

        var job = await _h.NewJobAsync();
        var (tab, token) = await _h.EditorLocksAsync(job);
        await RunAsync(() => _h.JobsApi(Editor, tab, token).EnqueueTask(job, new EnqueueTaskRequest(type, null, null), default));
        var claimed = await RunAsync(() => _h.WorkerApi().Claim(new WorkerClaimRequest("w1", [type]), default));
        var manifest = JsonSerializer.Serialize(new TaskResultManifest
        {
            ContractVersion = ContractInfo.Version, TaskType = type, TaskPublicId = claimed["taskPublicId"].GetString()!, Producer = producerRef,
            Detections = [Boot("d1", "4x12"), Boot("d2", "4x12")],
            Findings = [new FindingProposal { FindingId = "f1", Kind = FindingKind.GrammarVariant, Question = "Is a 4x12 boot depth 7?", DetectionId = "d1", Evidence = "label" }]
        }, ContractJson.Options);
        var posted = await RunAsync(() => _h.WorkerApi(body: new MemoryStream(System.Text.Encoding.UTF8.GetBytes(manifest))).Result(claimed["taskId"].GetInt64(), "w1", null, default));
        Assert.True(posted.Ok);

        var list = await RunAsync(() => _h.JobsApi(Viewer).ListEntities(job, null, null, null, null, null, default));
        return (job, tab, token, list.Body.EnumerateArray().Select(e => e.GetProperty("publicId").GetGuid()).ToList());
    }

    private static RuleBody DepthRule(string name = "Two-dimension boot means depth 7", string? scope = null) =>
        new(name, "Estimator convention", scope, null, null, new RuleMatch { Type = "Boot", SizePattern = @"^\d+x\d+$" }, new RuleAction { SizeSuffix = "x7" }, null);

    [Fact]
    public async Task A_finding_is_answered_by_previewing_writing_approving_and_applying_a_rule_and_then_closing_it()
    {
        var (job, tab, token, items) = await JobAsync();
        var api = () => _h.RulesApi(Editor, tab, token);

        var preview = await RunAsync(() => api().PreviewDraft(job, DepthRule(), default));
        Assert.Equal(2, preview["changes"].GetArrayLength());

        var created = await RunAsync(() => api().Create(job, DepthRule(), default));
        Assert.Equal((201, "Proposed", "Job", 1), (created.Status, created["status"].GetString(), created["scope"].GetString(), created["version"].GetInt32()));
        var ruleId = created["ruleId"].GetGuid();

        Assert.Equal(2, (await RunAsync(() => api().Preview(job, ruleId, default)))["changes"].GetArrayLength());
        Assert.Equal((422, "rule"), ((await RunAsync(() => api().Apply(job, ruleId, default))) is { } early ? (early.Status, early.Code) : (0, "")));

        Assert.Equal("Approved", (await RunAsync(() => api().Approve(job, ruleId, default)))["status"].GetString());
        var applied = await RunAsync(() => api().Apply(job, ruleId, default));
        Assert.Equal((2, 0, 0), (applied["changed"].GetInt32(), applied["skipped"].GetInt32(), applied["conflicts"].GetInt32()));

        var item = await RunAsync(() => _h.ReviewApi(Viewer, NewTab()).Get(job, items[0], default));
        Assert.Equal(("4x12x7", "Rule"), (item["size"].GetString(), item["resolutionSource"].GetString()));

        var open = await RunAsync(() => api().ListFindings(job, "Open", default));
        Assert.Equal(1, open.Body.GetArrayLength());
        var findingId = open.Body[0].GetProperty("findingId").GetGuid();
        var sheetBefore = await RunAsync(() => _h.JobsApi(Viewer).CountSheet(job, default));
        Assert.Equal((1, false), (sheetBefore["openFindings"].GetArrayLength(), sheetBefore["isFinal"].GetBoolean()));

        var closed = await RunAsync(() => api().Resolve(job, findingId, new ResolveFindingRequest("Rule", ruleId, "Convention"), default));
        Assert.Equal(("Resolved", "Rule", ruleId), (closed["state"].GetString(), closed["resolvedByKind"].GetString(), closed["resolvedRuleId"].GetGuid()));
        Assert.Equal(0, (await RunAsync(() => _h.JobsApi(Viewer).CountSheet(job, default)))["openFindings"].GetArrayLength());
    }

    [Fact]
    public async Task Closing_a_finding_by_a_rule_that_has_not_decided_the_item_is_a_422()
    {
        var (job, tab, token, _) = await JobAsync();
        var api = () => _h.RulesApi(Editor, tab, token);
        var ruleId = (await RunAsync(() => api().Create(job, DepthRule(), default)))["ruleId"].GetGuid();
        await RunAsync(() => api().Approve(job, ruleId, default));
        var findingId = (await RunAsync(() => api().ListFindings(job, null, default))).Body[0].GetProperty("findingId").GetGuid();

        var early = await RunAsync(() => api().Resolve(job, findingId, new ResolveFindingRequest("Rule", ruleId, null), default));
        Assert.Equal((422, "rule"), (early.Status, early.Code));
        Assert.Equal(400, (await RunAsync(() => api().Resolve(job, findingId, new ResolveFindingRequest("Rule", null, null), default))).Status);
        Assert.Equal(400, (await RunAsync(() => api().Resolve(job, findingId, new ResolveFindingRequest("Guess", null, null), default))).Status);
    }

    [Fact]
    public async Task A_finding_can_be_waived_with_a_reason_and_reopened()
    {
        var (job, tab, token, _) = await JobAsync();
        var api = () => _h.RulesApi(Editor, tab, token);
        var findingId = (await RunAsync(() => api().ListFindings(job, null, default))).Body[0].GetProperty("findingId").GetGuid();

        Assert.Equal(422, (await RunAsync(() => api().Resolve(job, findingId, new ResolveFindingRequest("Waiver", null, " "), default))).Status);
        var waived = await RunAsync(() => api().Resolve(job, findingId, new ResolveFindingRequest("Waiver", null, "Not needed"), default));
        Assert.Equal(("Waived", "Waiver"), (waived["state"].GetString(), waived["resolvedByKind"].GetString()));

        var reopened = await RunAsync(() => api().Reopen(job, findingId, default));
        Assert.Equal("Open", reopened["state"].GetString());
        Assert.Equal(404, (await RunAsync(() => api().Reopen(job, Guid.NewGuid(), default))).Status);
    }

    [Fact]
    public async Task Rules_can_be_undone_revised_and_retired_through_the_api()
    {
        var (job, tab, token, items) = await JobAsync();
        var api = () => _h.RulesApi(Editor, tab, token);
        var v1 = (await RunAsync(() => api().Create(job, DepthRule(), default)))["ruleId"].GetGuid();
        await RunAsync(() => api().Approve(job, v1, default));
        await RunAsync(() => api().Apply(job, v1, default));

        var undone = await RunAsync(() => api().UndoApplication(job, v1, default));
        Assert.Equal((2, 0), (undone["undone"].GetInt32(), undone["skipped"].GetInt32()));
        Assert.Equal("4x12", (await RunAsync(() => _h.ReviewApi(Viewer, NewTab()).Get(job, items[0], default)))["size"].GetString());

        var v2 = await RunAsync(() => api().Revise(job, v1, DepthRule("Depth 7, revised"), default));
        Assert.Equal((201, 2, "Proposed"), (v2.Status, v2["version"].GetInt32(), v2["status"].GetString()));
        await RunAsync(() => api().Approve(job, v2["ruleId"].GetGuid(), default));

        var rules = await RunAsync(() => api().ListRules(job, default));
        Assert.Equal(["Retired", "Approved"], rules.Body.EnumerateArray().Select(r => r.GetProperty("status").GetString()));

        Assert.Equal(422, (await RunAsync(() => api().Retire(job, v2["ruleId"].GetGuid(), new RetireRuleRequest(" "), default))).Status);
        Assert.Equal("Retired", (await RunAsync(() => api().Retire(job, v2["ruleId"].GetGuid(), new RetireRuleRequest("Changed my mind"), default)))["status"].GetString());
    }

    [Fact]
    public async Task Bad_bodies_are_400_and_422_and_an_instance_rule_needs_more_than_edit_permission()
    {
        var (job, tab, token, _) = await JobAsync();
        var api = () => _h.RulesApi(Editor, tab, token);

        Assert.Equal(400, (await RunAsync(() => api().Create(job, DepthRule(scope: "Galaxy"), default))).Status);
        Assert.Equal(400, (await RunAsync(() => api().Create(job, DepthRule() with { Action = null }, default))).Status);
        Assert.Equal(422, (await RunAsync(() => api().Create(job, DepthRule() with { Match = new RuleMatch() }, default))).Status);
        Assert.Equal(404, (await RunAsync(() => api().Create(job, DepthRule() with { Scope = "Sheet", SheetId = Guid.NewGuid() }, default))).Status);

        var instance = await RunAsync(() => api().Create(job, DepthRule("Shared", "Instance"), default));
        Assert.Equal((403, "forbidden"), (instance.Status, instance.Code));
    }

    [Fact]
    public async Task Changes_need_the_fencing_token_and_a_viewer_or_stranger_gets_nothing()
    {
        var (job, tab, token, _) = await JobAsync();

        Assert.Equal(400, (await RunAsync(() => _h.RulesApi(Editor, tab).Create(job, DepthRule(), default))).Status);
        Assert.Equal("lock-lost", (await RunAsync(() => _h.RulesApi(Editor, tab, token + 5).Create(job, DepthRule(), default))).Code);
        Assert.False((await RunAsync(() => _h.RulesApi(Viewer, NewTab(), token).Create(job, DepthRule(), default))).Ok);
        Assert.Equal(404, (await RunAsync(() => _h.RulesApi(Stranger, NewTab()).ListRules(job, default))).Status);
        Assert.Equal(404, (await RunAsync(() => _h.RulesApi(Stranger, NewTab()).ListFindings(job, null, default))).Status);
        Assert.Equal(200, (await RunAsync(() => _h.RulesApi(Viewer, NewTab()).ListFindings(job, null, default))).Status);
    }

    [Fact]
    public async Task A_rule_of_another_job_is_not_reachable()
    {
        var (job, tab, token, _) = await JobAsync();
        var (other, otherTab, otherToken, _) = await JobAsync();
        var ruleId = (await RunAsync(() => _h.RulesApi(Editor, tab, token).Create(job, DepthRule(), default)))["ruleId"].GetGuid();

        Assert.Equal(404, (await RunAsync(() => _h.RulesApi(Editor, otherTab, otherToken).Approve(other, ruleId, default))).Status);
        Assert.Equal(404, (await RunAsync(() => _h.RulesApi(Editor, otherTab, otherToken).Preview(other, ruleId, default))).Status);
        var listed = await RunAsync(() => _h.RulesApi(Editor, otherTab, otherToken).ListRules(other, default));
        Assert.DoesNotContain(listed.Body.EnumerateArray(), r => r.GetProperty("ruleId").GetGuid() == ruleId);
    }
}
