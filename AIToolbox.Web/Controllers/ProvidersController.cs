using System.Text.Json;
using AIToolbox.Web.Data;
using AIToolbox.Web.Models;
using AIToolbox.Web.Security;
using AIToolbox.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers;

[Authorize(Policy = "AdminOnly")]
[Route("Admin/Providers")]
public sealed class ProvidersController : Controller
{
    private readonly IAiProviderRepository _providers;
    private readonly ISecretProtector _secretProtector;

    public ProvidersController(
        IAiProviderRepository providers,
        ISecretProtector secretProtector)
    {
        _providers = providers;
        _secretProtector = secretProtector;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var providers = await _providers.GetAllAsync(includeDisabled: true, cancellationToken);
        return View(providers);
    }

    [HttpGet("Export")]
    public async Task<IActionResult> Export(CancellationToken cancellationToken)
    {
        var providers = await _providers.GetAllAsync(includeDisabled: true, cancellationToken);
        var export = new
        {
            exportedUtc = DateTime.UtcNow,
            providers = providers.Select(provider => new
            {
                provider.Id,
                provider.DisplayName,
                ProviderType = provider.ProviderType.ToString(),
                provider.EndpointUrl,
                provider.ModelName,
                provider.ApiVersion,
                provider.ExtraHeadersJson,
                provider.MaxOutputTokens,
                provider.RequestTimeoutSeconds,
                provider.IsEnabled,
                provider.SupportsVision,
                provider.AllowedForDuctwork,
                provider.GovernanceRoute,
                ApiKeyConfigured = !string.IsNullOrWhiteSpace(provider.ApiKeyCipherText),
                provider.ApiKeyLastUpdatedUtc
            })
        };

        return Json(export);
    }

    [HttpGet("Create")]
    public IActionResult Create()
    {
        return View(new ProviderFormViewModel());
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ProviderFormViewModel model, CancellationToken cancellationToken)
    {
        ValidateExtraHeadersJson(model.ExtraHeadersJson);
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var provider = ToProvider(model);
        provider.ApiKeyCipherText = _secretProtector.Protect(model.ApiKey);
        provider.ApiKeyLastUpdatedUtc = string.IsNullOrWhiteSpace(model.ApiKey) ? null : DateTime.UtcNow;

        var id = await _providers.CreateAsync(provider, cancellationToken);
        TempData["StatusMessage"] = "AI tool provider created.";
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpGet("Edit/{id:int}")]
    public async Task<IActionResult> Edit(int id, CancellationToken cancellationToken)
    {
        var provider = await _providers.GetByIdAsync(id, cancellationToken);
        return provider is null ? NotFound() : View(ToForm(provider));
    }

    [HttpPost("Edit/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ProviderFormViewModel model, CancellationToken cancellationToken)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        ValidateExtraHeadersJson(model.ExtraHeadersJson);
        if (!ModelState.IsValid)
        {
            model.HasStoredApiKey = await ProviderHasStoredKeyAsync(id, cancellationToken);
            return View(model);
        }

        var provider = ToProvider(model);
        var updateSecret = model.ClearApiKey || !string.IsNullOrWhiteSpace(model.ApiKey);
        if (updateSecret)
        {
            provider.ApiKeyCipherText = model.ClearApiKey ? null : _secretProtector.Protect(model.ApiKey);
            provider.ApiKeyLastUpdatedUtc = model.ClearApiKey ? null : DateTime.UtcNow;
        }

        await _providers.UpdateAsync(provider, updateSecret, cancellationToken);
        TempData["StatusMessage"] = "AI tool configuration saved.";
        return RedirectToAction(nameof(Edit), new { id });
    }

    private async Task<bool> ProviderHasStoredKeyAsync(int id, CancellationToken cancellationToken)
    {
        var provider = await _providers.GetByIdAsync(id, cancellationToken);
        return !string.IsNullOrWhiteSpace(provider?.ApiKeyCipherText);
    }

    private void ValidateExtraHeadersJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(value);
            if (headers is null)
            {
                ModelState.AddModelError(nameof(ProviderFormViewModel.ExtraHeadersJson), "Extra headers must be a JSON object.");
            }
        }
        catch (JsonException)
        {
            ModelState.AddModelError(nameof(ProviderFormViewModel.ExtraHeadersJson), "Extra headers must be valid JSON.");
        }
    }

    private static AiProvider ToProvider(ProviderFormViewModel model)
    {
        return new AiProvider
        {
            Id = model.Id,
            DisplayName = model.DisplayName,
            ProviderType = model.ProviderType,
            EndpointUrl = model.EndpointUrl,
            ModelName = model.ModelName,
            ApiVersion = model.ApiVersion,
            ExtraHeadersJson = model.ExtraHeadersJson,
            MaxOutputTokens = model.MaxOutputTokens,
            RequestTimeoutSeconds = model.RequestTimeoutSeconds,
            IsEnabled = model.IsEnabled,
            SupportsVision = model.SupportsVision,
            AllowedForDuctwork = model.AllowedForDuctwork,
            GovernanceRoute = string.IsNullOrWhiteSpace(model.GovernanceRoute) ? null : model.GovernanceRoute.Trim()
        };
    }

    private static ProviderFormViewModel ToForm(AiProvider provider)
    {
        return new ProviderFormViewModel
        {
            Id = provider.Id,
            DisplayName = provider.DisplayName,
            ProviderType = provider.ProviderType,
            EndpointUrl = provider.EndpointUrl,
            ModelName = provider.ModelName,
            ApiVersion = provider.ApiVersion,
            ExtraHeadersJson = provider.ExtraHeadersJson,
            MaxOutputTokens = provider.MaxOutputTokens,
            RequestTimeoutSeconds = provider.RequestTimeoutSeconds,
            IsEnabled = provider.IsEnabled,
            SupportsVision = provider.SupportsVision,
            AllowedForDuctwork = provider.AllowedForDuctwork,
            GovernanceRoute = provider.GovernanceRoute,
            HasStoredApiKey = !string.IsNullOrWhiteSpace(provider.ApiKeyCipherText)
        };
    }
}
