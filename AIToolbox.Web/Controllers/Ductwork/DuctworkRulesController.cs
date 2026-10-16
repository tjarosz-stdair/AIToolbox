using AIToolbox.Ductwork.Core.Counting;
using AIToolbox.Ductwork.Core.Drawings;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Rules;
using AIToolbox.Web.Ductwork;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers.Ductwork;

/// <summary>
/// Interpretation rules and the findings they answer (plan section 6.11). The person is the arbiter: a rule is written,
/// previewed, approved and applied by a person, and a finding is closed by a rule that decided the item, by an override,
/// or by a waiver with a reason. Rules and findings are addressed by public id inside a job.
/// </summary>
[Route("ductwork/api/jobs/{jobId:long}")]
public sealed class DuctworkRulesController(JobRepository jobs, EditLockService locks, RuleRepository rules, FindingRepository findings, SheetRepository sheets) : DuctworkUserApiController
{
    // ------------------------------------------------------------------ rules

    [HttpGet("rules")]
    public async Task<IActionResult> ListRules(long jobId, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var sheetIds = (await sheets.ListByJobAsync(jobId, ct)).ToDictionary(s => s.SheetId, s => s.PublicId);
        return Json((await rules.ListForJobAsync(jobId, ct)).Select(r => RuleOf(r, sheetIds)));
    }

    /// <summary>What a rule that has not been saved yet would change, so its effect is seen before it is written down.</summary>
    [HttpPost("rules/preview")]
    public async Task<IActionResult> PreviewDraft(long jobId, [FromBody] RuleBody body, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        return Json(await rules.GetDraftPreviewAsync(jobId, await ToNewAsync(jobId, body, ct), ct));
    }

    [HttpPost("rules")]
    public async Task<IActionResult> Create(long jobId, [FromBody] RuleBody body, CancellationToken ct)
    {
        var audit = Audit();
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var draft = await ToNewAsync(jobId, body, ct);
        var token = await locks.ResolveTokenAsync(jobId, FencingToken(), audit, ct);
        var created = await rules.CreateAsync(jobId, draft, token, audit, ct);
        return Json(RuleOf(created, await SheetIdsAsync(jobId, ct)), 201);
    }

    [HttpGet("rules/{ruleId:guid}/preview")]
    public async Task<IActionResult> Preview(long jobId, Guid ruleId, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var rule = await FindAsync(jobId, ruleId, ct);
        return Json(await rules.GetPreviewAsync(jobId, rule.RuleId, ct));
    }

    [HttpPost("rules/{ruleId:guid}/revise")]
    public async Task<IActionResult> Revise(long jobId, Guid ruleId, [FromBody] RuleBody body, CancellationToken ct)
    {
        var (audit, token, rule) = await EditAsync(jobId, ruleId, ct);
        var revised = await rules.ReviseAsync(jobId, rule.RuleId, await ToNewAsync(jobId, body, ct), token, audit, ct);
        return Json(RuleOf(revised, await SheetIdsAsync(jobId, ct)), 201);
    }

    [HttpPost("rules/{ruleId:guid}/approve")]
    public async Task<IActionResult> Approve(long jobId, Guid ruleId, CancellationToken ct)
    {
        var (audit, token, rule) = await EditAsync(jobId, ruleId, ct);
        return Json(RuleOf(await rules.ApproveAsync(jobId, rule.RuleId, token, audit, ct), await SheetIdsAsync(jobId, ct)));
    }

    /// <summary>Applies an approved rule. What it does is what the preview showed.</summary>
    [HttpPost("rules/{ruleId:guid}/apply")]
    public async Task<IActionResult> Apply(long jobId, Guid ruleId, CancellationToken ct)
    {
        var (audit, token, rule) = await EditAsync(jobId, ruleId, ct);
        return Json(await rules.ApplyAsync(jobId, rule.RuleId, token, audit, ct));
    }

    [HttpPost("rules/{ruleId:guid}/undo-application")]
    public async Task<IActionResult> UndoApplication(long jobId, Guid ruleId, CancellationToken ct)
    {
        var (audit, token, rule) = await EditAsync(jobId, ruleId, ct);
        return Json(await rules.UndoApplicationAsync(jobId, rule.RuleId, token, audit, ct));
    }

    [HttpPost("rules/{ruleId:guid}/retire")]
    public async Task<IActionResult> Retire(long jobId, Guid ruleId, [FromBody] RetireRuleRequest body, CancellationToken ct)
    {
        var (audit, token, rule) = await EditAsync(jobId, ruleId, ct);
        return Json(RuleOf(await rules.RetireAsync(jobId, rule.RuleId, body.Reason ?? "", token, audit, ct), await SheetIdsAsync(jobId, ct)));
    }

    // ------------------------------------------------------------------ findings

    [HttpGet("findings")]
    public async Task<IActionResult> ListFindings(long jobId, [FromQuery] string? state, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        return Json((await findings.ListByJobAsync(jobId, DuctworkParse.OptionalEnum<FindingState>(state, "finding state"), ct)).Select(FindingOf));
    }

    /// <summary>Closes a finding by a rule (which must have decided the item), by an override (the item was changed or rejected), or by a waiver with a reason.</summary>
    [HttpPost("findings/{findingId:guid}/resolve")]
    public async Task<IActionResult> Resolve(long jobId, Guid findingId, [FromBody] ResolveFindingRequest body, CancellationToken ct)
    {
        var audit = Audit();
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var kind = DuctworkParse.Enum<FindingResolutionKind>(body.Kind, "resolution kind");
        var finding = await findings.GetByPublicIdAsync(jobId, findingId, ct) ?? throw new KeyNotFoundException("That finding does not exist.");
        var rule = kind == FindingResolutionKind.Rule
            ? await FindAsync(jobId, body.RuleId ?? throw new DuctworkBadRequestException("Say which rule answers this finding."), ct)
            : null;
        var token = await locks.ResolveTokenAsync(jobId, FencingToken(), audit, ct);

        var result = kind switch
        {
            FindingResolutionKind.Rule => await findings.ResolveByRuleAsync(jobId, finding.FindingId, rule!.RuleId, body.Note, token, audit, ct),
            FindingResolutionKind.Override => await findings.ResolveByOverrideAsync(jobId, finding.FindingId, body.Note, token, audit, ct),
            _ => await findings.WaiveAsync(jobId, finding.FindingId, body.Note ?? "", token, audit, ct)
        };
        return Json(FindingOf(result));
    }

    [HttpPost("findings/{findingId:guid}/reopen")]
    public async Task<IActionResult> Reopen(long jobId, Guid findingId, CancellationToken ct)
    {
        var audit = Audit();
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var finding = await findings.GetByPublicIdAsync(jobId, findingId, ct) ?? throw new KeyNotFoundException("That finding does not exist.");
        var token = await locks.ResolveTokenAsync(jobId, FencingToken(), audit, ct);
        return Json(FindingOf(await findings.ReopenAsync(jobId, finding.FindingId, token, audit, ct)));
    }

    // ------------------------------------------------------------------ helpers

    private async Task<RuleRecord> FindAsync(long jobId, Guid ruleId, CancellationToken ct) =>
        await rules.GetByPublicIdAsync(jobId, ruleId, ct) ?? throw new KeyNotFoundException("That rule does not exist.");

    private async Task<(AuditContext Audit, EditToken Token, RuleRecord Rule)> EditAsync(long jobId, Guid ruleId, CancellationToken ct)
    {
        var audit = Audit();
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var rule = await FindAsync(jobId, ruleId, ct);
        var token = await locks.ResolveTokenAsync(jobId, FencingToken(), audit, ct);
        return (audit, token, rule);
    }

    private async Task<Dictionary<long, Guid>> SheetIdsAsync(long jobId, CancellationToken ct) =>
        (await sheets.ListByJobAsync(jobId, ct)).ToDictionary(s => s.SheetId, s => s.PublicId);

    private async Task<NewRule> ToNewAsync(long jobId, RuleBody body, CancellationToken ct)
    {
        long? sheetId = null;
        if (body.SheetId is { } publicSheet)
            sheetId = (await sheets.GetByPublicIdAsync(jobId, publicSheet, ct) ?? throw new KeyNotFoundException("That sheet does not exist.")).SheetId;

        long? findingId = null;
        if (body.FromFindingId is { } publicFinding)
            findingId = (await findings.GetByPublicIdAsync(jobId, publicFinding, ct) ?? throw new KeyNotFoundException("That finding does not exist.")).FindingId;

        return new NewRule
        {
            Name = body.Name ?? "", Description = body.Description, Scope = DuctworkParse.OptionalEnum<RuleScope>(body.Scope, "rule scope") ?? RuleScope.Job,
            BuildingTypeId = body.BuildingTypeId, SheetId = sheetId, Match = body.Match ?? new RuleMatch(),
            Action = body.Action ?? throw new DuctworkBadRequestException("A rule needs an action."), FromFindingId = findingId
        };
    }

    private static IReadOnlyList<string> Candidates(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    private static object RuleOf(RuleRecord r, IReadOnlyDictionary<long, Guid> sheetIds) => new
    {
        ruleId = r.PublicId, ruleKey = r.RuleKey, version = r.Version, name = r.Name, description = r.Description, scope = r.Scope, buildingTypeId = r.BuildingTypeId,
        sheetId = r.SheetId is { } s && sheetIds.TryGetValue(s, out var publicId) ? publicId : (Guid?)null, match = r.Match, action = r.Action, status = r.Status,
        createdBy = r.CreatedBy, createdUtc = r.CreatedUtc, approvedBy = r.ApprovedBy, approvedUtc = r.ApprovedUtc, retiredBy = r.RetiredBy, retiredUtc = r.RetiredUtc, retireReason = r.RetireReason
    };

    private static object FindingOf(FindingRecord f) => new
    {
        findingId = f.PublicId, entityId = f.EntityPublicId, item = f.Item, kind = f.Kind, question = f.Question, candidates = Candidates(f.CandidatesJson), evidence = f.Evidence, state = f.State,
        resolvedByKind = f.ResolvedByKind, resolvedRuleId = f.ResolvedRulePublicId, resolvedBy = f.ResolvedBy, resolvedUtc = f.ResolvedUtc, resolutionNote = f.ResolutionNote,
        createdUtc = f.CreatedUtc
    };
}
