using System.Text.Json;
using AIToolbox.Web.Data;
using AIToolbox.Web.Models;
using AIToolbox.Web.Security;
using AIToolbox.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers;

/// <summary>
/// Admin CRUD for "External Resources" — configured non-chat, non-MCP bolt-on service
/// integrations (Azure Speech-to-Text is the first). Deliberately its own area, distinct from
/// Providers (chat AI providers/ToolDefinitions) and McpClientCredentials (MCP OAuth secrets).
/// </summary>
[Authorize(Policy = "AdminOnly")]
[Route("Admin/ExternalResources")]
public sealed class ExternalResourcesController : Controller
{
    private readonly IExternalResourceRepository _resources;
    private readonly ISecretProtector _secretProtector;

    public ExternalResourcesController(
        IExternalResourceRepository resources,
        ISecretProtector secretProtector)
    {
        _resources = resources;
        _secretProtector = secretProtector;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var resources = await _resources.GetAllAsync(includeDisabled: true, cancellationToken);
        return View(resources);
    }

    [HttpGet("Create")]
    public IActionResult Create()
    {
        return View(new ExternalResourceFormViewModel());
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ExternalResourceFormViewModel model, CancellationToken cancellationToken)
    {
        ValidateTypeSpecificFields(model);
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var existing = await _resources.GetAllAsync(includeDisabled: true, cancellationToken);
        if (existing.Any(r => r.ResourceType == model.ResourceType))
        {
            ModelState.AddModelError(string.Empty, "A resource of this type is already configured — edit it instead of adding a second one.");
            return View(model);
        }

        var resource = ToResource(model);
        resource.SecretCipherText = _secretProtector.Protect(model.SubscriptionKey);
        resource.SecretLastUpdatedUtc = string.IsNullOrWhiteSpace(model.SubscriptionKey) ? null : DateTime.UtcNow;

        var id = await _resources.CreateAsync(resource, cancellationToken);
        TempData["StatusMessage"] = "External resource created.";
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpGet("Edit/{id:int}")]
    public async Task<IActionResult> Edit(int id, CancellationToken cancellationToken)
    {
        var resource = await _resources.GetByIdAsync(id, cancellationToken);
        return resource is null ? NotFound() : View(ToForm(resource));
    }

    [HttpPost("Edit/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ExternalResourceFormViewModel model, CancellationToken cancellationToken)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        ValidateTypeSpecificFields(model);
        if (!ModelState.IsValid)
        {
            model.HasStoredSecret = await ResourceHasStoredSecretAsync(id, cancellationToken);
            return View(model);
        }

        var resource = ToResource(model);
        var updateSecret = model.ClearSecret || !string.IsNullOrWhiteSpace(model.SubscriptionKey);
        if (updateSecret)
        {
            resource.SecretCipherText = model.ClearSecret ? null : _secretProtector.Protect(model.SubscriptionKey);
            resource.SecretLastUpdatedUtc = model.ClearSecret ? null : DateTime.UtcNow;
        }

        await _resources.UpdateAsync(resource, updateSecret, cancellationToken);
        TempData["StatusMessage"] = "External resource saved.";
        return RedirectToAction(nameof(Edit), new { id });
    }

    private async Task<bool> ResourceHasStoredSecretAsync(int id, CancellationToken cancellationToken)
    {
        var resource = await _resources.GetByIdAsync(id, cancellationToken);
        return !string.IsNullOrWhiteSpace(resource?.SecretCipherText);
    }

    /// <summary>Which fields matter depends on ResourceType, so [Required] alone can't express
    /// it — enforced here instead, mirroring the duplicate-type check already done inline.</summary>
    private void ValidateTypeSpecificFields(ExternalResourceFormViewModel model)
    {
        switch (model.ResourceType)
        {
            case ExternalResourceType.AzureSpeech:
                if (string.IsNullOrWhiteSpace(model.Region))
                {
                    ModelState.AddModelError(nameof(model.Region), "Azure region is required.");
                }
                if (string.IsNullOrWhiteSpace(model.Language))
                {
                    ModelState.AddModelError(nameof(model.Language), "Recognition language is required.");
                }
                break;

            case ExternalResourceType.CallDataApi:
                if (string.IsNullOrWhiteSpace(model.BaseUrl) || !Uri.TryCreate(model.BaseUrl, UriKind.Absolute, out _))
                {
                    ModelState.AddModelError(nameof(model.BaseUrl), "A valid absolute base URL is required.");
                }
                break;
        }
    }

    private static ExternalResource ToResource(ExternalResourceFormViewModel model)
    {
        var configJson = model.ResourceType switch
        {
            ExternalResourceType.CallDataApi => JsonSerializer.Serialize(new { baseUrl = model.BaseUrl!.Trim() }),
            _ => JsonSerializer.Serialize(new { region = model.Region.Trim(), language = model.Language.Trim() })
        };

        return new ExternalResource
        {
            Id = model.Id,
            ResourceKey = model.ResourceType.ToString(),
            ResourceType = model.ResourceType,
            DisplayName = model.DisplayName,
            ConfigJson = configJson,
            IsEnabled = model.IsEnabled
        };
    }

    private static ExternalResourceFormViewModel ToForm(ExternalResource resource)
    {
        var model = new ExternalResourceFormViewModel
        {
            Id = resource.Id,
            ResourceType = resource.ResourceType,
            DisplayName = resource.DisplayName,
            Language = "en-US",
            IsEnabled = resource.IsEnabled,
            HasStoredSecret = !string.IsNullOrWhiteSpace(resource.SecretCipherText)
        };

        if (string.IsNullOrWhiteSpace(resource.ConfigJson))
        {
            return model;
        }

        try
        {
            using var doc = JsonDocument.Parse(resource.ConfigJson);
            if (doc.RootElement.TryGetProperty("region", out var regionEl))
            {
                model.Region = regionEl.GetString() ?? string.Empty;
            }
            if (doc.RootElement.TryGetProperty("language", out var languageEl))
            {
                model.Language = languageEl.GetString() ?? "en-US";
            }
            if (doc.RootElement.TryGetProperty("baseUrl", out var baseUrlEl))
            {
                model.BaseUrl = baseUrlEl.GetString();
            }
        }
        catch (JsonException)
        {
            // Leave defaults — a hand-edited/corrupt ConfigJson shouldn't crash the edit page.
        }

        return model;
    }
}
