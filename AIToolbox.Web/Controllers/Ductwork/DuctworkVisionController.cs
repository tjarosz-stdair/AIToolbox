using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Llm;
using AIToolbox.Web.Data;
using AIToolbox.Web.Ductwork;
using AIToolbox.Web.Models;
using AIToolbox.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers.Ductwork;

/// <summary>
/// Approving drawings to be sent to a hosted model (plan section 6.12). The dialog is told exactly what will be sent and where,
/// including the administrator's data-governance notice, and the approval covers only the sheets listed, for one run, until it expires.
/// </summary>
[Route("ductwork/api")]
public sealed class DuctworkVisionController(JobRepository jobs, VisionRepository vision, IAiProviderRepository providers) : DuctworkUserApiController
{
    /// <summary>What a person needs to know before asking: whether the capability is on, the notice, and which models are allowed.</summary>
    [HttpGet("vision/policy")]
    public async Task<IActionResult> Policy(CancellationToken ct)
    {
        var policy = await vision.GetPolicyAsync(ct);
        var allowed = (await providers.GetAllAsync(includeDisabled: false, ct)).Where(p => policy.AllowedProviderIds.Contains(p.Id) && p.WhyNotUsableForDuctwork(true) is null);
        return Json(new
        {
            enabled = policy.Enabled, noticeText = policy.NoticeText, noticeVersion = policy.NoticeVersion, approverRule = policy.ApproverRule, requireReason = policy.RequireReason,
            maxPagesPerApproval = policy.MaxPagesPerApproval, maxImagesPerPage = policy.MaxImagesPerPage, approvalLifetimeMinutes = policy.ApprovalLifetimeMinutes,
            providers = allowed.Select(p => new { providerId = p.Id, name = p.DisplayName, model = p.ModelName, governanceRoute = p.GovernanceRoute })
        });
    }

    /// <summary>The plain statement of what approving would do, and the reasons it cannot be approved if it cannot.</summary>
    [HttpPost("jobs/{jobId:long}/vision/disclosure")]
    public async Task<IActionResult> Disclosure(long jobId, [FromBody] VisionDisclosureRequest body, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var provider = await ProviderAsync(body.ProviderId, ct);
        var disclosure = await vision.PreviewApprovalAsync(jobId, body.SheetIds ?? [], provider, Audit(), ct);
        return Json(new
        {
            canApprove = disclosure.Problems.Count == 0, problems = disclosure.Problems, pageCount = disclosure.PageCount, imageBudget = disclosure.ImageBudget, lifetimeMinutes = disclosure.LifetimeMinutes,
            noticeText = disclosure.Policy.NoticeText, noticeVersion = disclosure.Policy.NoticeVersion, requireReason = disclosure.Policy.RequireReason,
            provider = new { providerId = provider.ProviderId, name = provider.DisplayName, model = provider.Model, host = provider.EndpointHost, governanceRoute = provider.GovernanceRoute },
            sheets = disclosure.Sheets.Select(s => new { sheetId = s.SheetId, sheetNumber = s.SheetNumber, title = s.Title, pageNo = s.PageNo, fileName = s.FileName })
        });
    }

    [HttpPost("jobs/{jobId:long}/vision-approvals")]
    public async Task<IActionResult> Approve(long jobId, [FromBody] CreateVisionApprovalRequest body, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var provider = await ProviderAsync(body.ProviderId, ct);
        var approval = await vision.CreateApprovalAsync(jobId, new ApprovalRequest
        {
            SheetIds = body.SheetIds ?? [], Provider = provider, Reason = body.Reason, AcknowledgedNoticeVersion = body.AcknowledgedNoticeVersion
        }, Audit(), ct);
        return Json(ApprovalOf(approval), 201);
    }

    [HttpGet("jobs/{jobId:long}/vision-approvals")]
    public async Task<IActionResult> List(long jobId, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        return Json((await vision.ListApprovalsAsync(jobId, ct)).Select(ApprovalOf));
    }

    [HttpPost("jobs/{jobId:long}/vision-approvals/{approvalId:guid}/revoke")]
    public async Task<IActionResult> Revoke(long jobId, Guid approvalId, [FromBody] RevokeVisionApprovalRequest body, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        var approval = await vision.GetApprovalAsync(jobId, approvalId, ct) ?? throw new KeyNotFoundException("That approval does not exist.");
        return Json(ApprovalOf(await vision.RevokeApprovalAsync(jobId, approval.VisionApprovalId, body.Reason ?? "", Audit(), ct)));
    }

    /// <summary>What was sent to a hosted model on this job: counts and outcomes, never the images or the instructions.</summary>
    [HttpGet("jobs/{jobId:long}/vision-transmissions")]
    public async Task<IActionResult> Transmissions(long jobId, CancellationToken ct)
    {
        await RequireMemberAsync(jobs, jobId, Email, ct);
        return Json((await vision.ListTransmissionsAsync(jobId, ct)).Select(t => new { id = t.Id, images = t.Images, bytes = t.Bytes, outcome = t.Outcome, requestedUtc = t.RequestedUtc, error = t.Error }));
    }

    private async Task<ProviderDisclosure> ProviderAsync(int providerId, CancellationToken ct)
    {
        var provider = await providers.GetByIdAsync(providerId, ct) ?? throw new JobRuleViolationException("That model does not exist.");
        if (provider.WhyNotUsableForDuctwork(needsVision: true) is { } why) throw new JobRuleViolationException($"That model cannot be used for drawings: {why}");
        return ToDisclosure(provider);
    }

    internal static ProviderDisclosure ToDisclosure(AiProvider p)
    {
        var snapshot = p.ToSnapshot();
        return new ProviderDisclosure(p.Id, p.DisplayName, p.ModelName, snapshot.EndpointHost, p.GovernanceRoute, snapshot);
    }

    private static object ApprovalOf(VisionApprovalRecord a) => new
    {
        approvalId = a.PublicId, kind = a.Kind, status = a.Status, approvedBy = a.ApprovedBy, approvedUtc = a.ApprovedUtc, expiresUtc = a.ExpiresUtc, isExpired = a.IsExpired,
        sheetIds = a.SheetIds, pageCount = a.PageCount, imageBudget = a.ImageBudget, imagesSent = a.ImagesSent, reason = a.Reason, noticeVersion = a.NoticeVersion,
        revokedBy = a.RevokedBy, revokedUtc = a.RevokedUtc, revokeReason = a.RevokeReason
    };
}

/// <summary>The administrator's controls for hosted vision: the master switch (off by default), which models may receive drawings, who may approve, the notice wording and the limits.</summary>
[ApiController]
[Authorize(Policy = "AdminOnly")]
[AutoValidateAntiforgeryToken]
[DuctworkApiException]
[Route("ductwork/api/admin/vision-policy")]
public sealed class DuctworkVisionAdminController(VisionRepository vision, IAiProviderRepository providers) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var policy = await vision.GetPolicyAsync(ct);
        var all = await providers.GetAllAsync(includeDisabled: true, ct);
        return DuctworkJson.Result(new
        {
            policy,
            providers = all.Select(p => new
            {
                providerId = p.Id, name = p.DisplayName, model = p.ModelName, governanceRoute = p.GovernanceRoute, enabled = p.IsEnabled,
                eligible = p.WhyNotUsableForDuctwork(needsVision: true) is null, whyNot = p.WhyNotUsableForDuctwork(needsVision: true)
            })
        });
    }

    [HttpPut]
    public async Task<IActionResult> Set([FromBody] VisionPolicyBody body, CancellationToken ct)
    {
        var requested = (body.AllowedProviderIds ?? []).Distinct().ToList();
        if (requested.Count > 0)
        {
            var known = (await providers.GetAllAsync(includeDisabled: true, ct)).ToDictionary(p => p.Id);
            foreach (var id in requested)
            {
                if (!known.TryGetValue(id, out var provider)) throw new JobRuleViolationException($"Provider {id} does not exist.");
                if (provider.WhyNotUsableForDuctwork(needsVision: true) is { } why) throw new JobRuleViolationException($"'{provider.DisplayName}' cannot receive drawings: {why}");
            }
        }

        var update = new VisionPolicyUpdate
        {
            Enabled = body.Enabled, AllowedProviderIds = requested, ApproverRule = DuctworkParse.OptionalEnum<VisionApproverRule>(body.ApproverRule, "approver rule") ?? VisionApproverRule.EditPermission,
            NoticeText = body.NoticeText, MaxPagesPerApproval = body.MaxPagesPerApproval, MaxImagesPerPage = body.MaxImagesPerPage, RequireReason = body.RequireReason,
            ApprovalLifetimeMinutes = body.ApprovalLifetimeMinutes, StandingTaskTypes = body.StandingTaskTypes ?? [], StandingMaxPagesPerJob = body.StandingMaxPagesPerJob
        };
        return DuctworkJson.Result(await vision.SetPolicyAsync(update, DuctworkAuditContexts.ForUser(HttpContext), ct));
    }
}
