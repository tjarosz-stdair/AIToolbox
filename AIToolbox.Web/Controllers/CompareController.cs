using AIToolbox.Web.Data;
using AIToolbox.Web.Models;
using AIToolbox.Web.Security;
using AIToolbox.Web.Services;
using AIToolbox.Web.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace AIToolbox.Web.Controllers;

public sealed class CompareController : Controller
{
    private readonly IAiProviderRepository _providers;
    private readonly IPromptRunRepository _runs;
    private readonly IPromptOrchestrator _orchestrator;

    public CompareController(
        IAiProviderRepository providers,
        IPromptRunRepository runs,
        IPromptOrchestrator orchestrator)
    {
        _providers = providers;
        _runs = runs;
        _orchestrator = orchestrator;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        return View(new AskViewModel
        {
            Providers = await BuildProviderCheckboxesAsync([], cancellationToken)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Ask(AskViewModel model, CancellationToken cancellationToken)
    {
        if (model.SelectedProviderIds.Count == 0)
        {
            ModelState.AddModelError(nameof(model.SelectedProviderIds), "Select at least one AI tool.");
        }

        if (!ModelState.IsValid)
        {
            model.Providers = await BuildProviderCheckboxesAsync(model.SelectedProviderIds, cancellationToken);
            return View("Index", model);
        }

        var userEmail = User.GetEmailAddress() ?? User.Identity?.Name ?? "unknown";
        var promptRunId = await _orchestrator.SubmitQuestionAsync(
            userEmail,
            model.Question,
            model.IsPublic,
            model.SelectedProviderIds,
            cancellationToken);

        return RedirectToAction(nameof(Run), new { id = promptRunId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task AskStream(AskViewModel model, CancellationToken cancellationToken)
    {
        Response.ContentType = "application/x-ndjson";
        Response.Headers["Cache-Control"] = "no-cache";

        if (model.SelectedProviderIds.Count == 0)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteProgressAsync(new { type = "error", message = "Select at least one AI tool." }, cancellationToken);
            return;
        }

        if (!ModelState.IsValid)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteProgressAsync(new { type = "error", message = "Enter a question and select at least one AI tool." }, cancellationToken);
            return;
        }

        await Response.StartAsync(cancellationToken);

        try
        {
            var selectedProviders = await _providers.GetEnabledByIdsAsync(model.SelectedProviderIds, cancellationToken);
            await WriteProgressAsync(new
            {
                type = "started",
                total = selectedProviders.Count,
                providers = selectedProviders.Select(provider => provider.DisplayName)
            }, cancellationToken);

            var userEmail = User.GetEmailAddress() ?? User.Identity?.Name ?? "unknown";
            var promptRunId = await _orchestrator.SubmitQuestionWithProgressAsync(
                userEmail,
                model.Question,
                model.IsPublic,
                model.SelectedProviderIds,
                async result =>
                {
                    await WriteProgressAsync(new
                    {
                        type = "providerCompleted",
                        providerId = result.ProviderId,
                        providerName = result.ProviderName,
                        succeeded = result.Succeeded,
                        durationMs = result.DurationMs,
                        errorMessage = result.ErrorMessage
                    }, cancellationToken);
                },
                cancellationToken);

            await WriteProgressAsync(new
            {
                type = "complete",
                redirectUrl = Url.Action(nameof(Run), new { id = promptRunId })
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            await WriteProgressAsync(new { type = "error", message = ex.Message }, cancellationToken);
        }
    }

    [HttpGet]
    public async Task<IActionResult> History(string scope = "all", CancellationToken cancellationToken = default)
    {
        scope = NormalizeHistoryScope(scope);
        var userEmail = User.GetEmailAddress() ?? User.Identity?.Name ?? "unknown";
        var runs = await _runs.GetVisiblePromptRunsAsync(userEmail, scope, cancellationToken: cancellationToken);

        return View(new PromptRunHistoryViewModel
        {
            Scope = scope,
            Runs = runs.Select(run => new PromptRunHistoryItemViewModel
            {
                Id = run.Id,
                UserEmail = run.UserEmail,
                Question = run.Question,
                IsPublic = run.IsPublic,
                IsOwner = IsOwner(run.UserEmail),
                CreatedUtc = run.CreatedUtc,
                ResponseCount = run.ResponseCount,
                AnalysisRunCount = run.AnalysisRunCount,
                LastAnalysisUtc = run.LastAnalysisUtc
            }).ToList()
        });
    }

    [HttpGet]
    public async Task<IActionResult> Run(int id, CancellationToken cancellationToken)
    {
        var run = await _runs.GetPromptRunAsync(id, cancellationToken);
        if (run is null)
        {
            return NotFound();
        }

        if (!CanView(run))
        {
            return Forbid();
        }

        return View(new PromptRunViewModel
        {
            Run = run,
            IsOwner = IsOwner(run.UserEmail),
            AnalysisRuns = await _runs.GetAnalysisRunSummariesAsync(run.Id, cancellationToken),
            AnalysisProviders = await BuildProviderCheckboxesAsync([], cancellationToken)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Analyze(AnalyzeRequestViewModel model, CancellationToken cancellationToken)
    {
        var run = await _runs.GetPromptRunAsync(model.PromptRunId, cancellationToken);
        if (run is null)
        {
            return NotFound();
        }

        if (!CanView(run))
        {
            return Forbid();
        }

        if (model.SelectedProviderIds.Count == 0)
        {
            ModelState.AddModelError(nameof(model.SelectedProviderIds), "Select at least one AI tool for analysis.");
            return View("Run", new PromptRunViewModel
            {
                Run = run,
                IsOwner = IsOwner(run.UserEmail),
                AnalysisRuns = await _runs.GetAnalysisRunSummariesAsync(run.Id, cancellationToken),
                AnalysisProviders = await BuildProviderCheckboxesAsync(model.SelectedProviderIds, cancellationToken)
            });
        }

        var userEmail = User.GetEmailAddress() ?? User.Identity?.Name ?? "unknown";
        var analysisRunId = await _orchestrator.AnalyzeResponsesAsync(
            userEmail,
            model.PromptRunId,
            model.SelectedProviderIds,
            cancellationToken);

        return RedirectToAction(nameof(Analysis), new { id = analysisRunId });
    }

    [HttpGet]
    public async Task<IActionResult> Analysis(int id, CancellationToken cancellationToken)
    {
        var analysisRun = await _runs.GetAnalysisRunAsync(id, cancellationToken);
        if (analysisRun is null)
        {
            return NotFound();
        }

        var promptRun = await _runs.GetPromptRunAsync(analysisRun.PromptRunId, cancellationToken);
        if (promptRun is null)
        {
            return NotFound();
        }

        if (!CanView(promptRun))
        {
            return Forbid();
        }

        return View(new AnalysisRunViewModel
        {
            PromptRun = promptRun,
            AnalysisRun = analysisRun
        });
    }

    private async Task<List<ProviderCheckboxViewModel>> BuildProviderCheckboxesAsync(
        IEnumerable<int> selectedIds,
        CancellationToken cancellationToken)
    {
        var selected = selectedIds.ToHashSet();
        var providers = await _providers.GetAllAsync(includeDisabled: false, cancellationToken);

        return providers.Select(provider => new ProviderCheckboxViewModel
        {
            Id = provider.Id,
            DisplayName = provider.DisplayName,
            ProviderType = provider.ProviderType.ToString(),
            ModelName = provider.ModelName,
            IsChecked = selected.Contains(provider.Id)
        }).ToList();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateVisibility(
        int id,
        bool isPublic,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        var userEmail = User.GetEmailAddress() ?? User.Identity?.Name ?? "unknown";
        var updated = await _runs.UpdatePromptRunVisibilityAsync(id, userEmail, isPublic, cancellationToken);
        if (!updated)
        {
            return Forbid();
        }

        var updatedRun = await _runs.GetPromptRunAsync(id, cancellationToken);
        var savedIsPublic = updatedRun?.IsPublic ?? isPublic;
        TempData["StatusMessage"] = savedIsPublic
            ? "Saved query is now public to authorized users."
            : "Saved query is now private.";

        var normalizedReturnUrl = NormalizeLocalReturnUrl(returnUrl);
        if (normalizedReturnUrl is not null)
        {
            return Redirect(normalizedReturnUrl);
        }

        return RedirectToAction(nameof(Run), new { id });
    }

    private bool CanView(PromptRun run)
    {
        return run.IsPublic || IsOwner(run.UserEmail);
    }

    private bool IsOwner(string ownerEmail)
    {
        var userEmail = User.GetEmailAddress();
        return string.Equals(ownerEmail, userEmail, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHistoryScope(string? scope)
    {
        return scope?.Trim().ToLowerInvariant() switch
        {
            "mine" => "mine",
            "public" => "public",
            _ => "all"
        };
    }

    private string? NormalizeLocalReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) || !Url.IsLocalUrl(returnUrl))
        {
            return null;
        }

        if (returnUrl.StartsWith("~/", StringComparison.Ordinal))
        {
            return Url.Content(returnUrl);
        }

        var pathBase = Request.PathBase.Value;
        if (string.IsNullOrWhiteSpace(pathBase)
            || returnUrl.StartsWith(pathBase, StringComparison.OrdinalIgnoreCase)
            || !returnUrl.StartsWith("/", StringComparison.Ordinal))
        {
            return returnUrl;
        }

        return pathBase + returnUrl;
    }

    private async Task WriteProgressAsync(object payload, CancellationToken cancellationToken)
    {
        await Response.WriteAsync(JsonSerializer.Serialize(payload), cancellationToken);
        await Response.WriteAsync("\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }
}
