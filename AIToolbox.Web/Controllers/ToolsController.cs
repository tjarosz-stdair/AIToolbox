using AIToolbox.Web.Data;
using AIToolbox.Web.Models;
using AIToolbox.Web.Security;
using AIToolbox.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers;

[Authorize(Policy = "AdminOnly")]
[Route("Admin/Tools")]
public sealed class ToolsController : Controller
{
    private readonly IPlatformRepository _platform;
    private readonly IAiProviderRepository _providers;

    public ToolsController(IPlatformRepository platform, IAiProviderRepository providers)
    {
        _platform = platform;
        _providers = providers;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        return View(await _platform.GetToolsAsync(cancellationToken));
    }

    [HttpGet("Create")]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        return View("Edit", await CreateViewModelAsync(new ToolDefinition(), cancellationToken));
    }

    [HttpGet("Edit/{id:int}")]
    public async Task<IActionResult> Edit(int id, CancellationToken cancellationToken)
    {
        var tool = await _platform.GetToolByIdAsync(id, cancellationToken);
        return tool is null ? NotFound() : View(await CreateViewModelAsync(tool, cancellationToken));
    }

    [HttpPost("Edit/{id:int?}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int? id, ToolEditViewModel model, CancellationToken cancellationToken)
    {
        if (id is not null && id.Value != model.Id)
        {
            return BadRequest();
        }

        if (!ModelState.IsValid)
        {
            model.Providers = await _providers.GetAllAsync(includeDisabled: false, cancellationToken);
            model.AllowList = string.IsNullOrWhiteSpace(model.ToolKey)
                ? []
                : await _platform.GetAllowListAsync(model.ToolKey, cancellationToken);
            return View(model);
        }

        var tool = new ToolDefinition
        {
            Id = model.Id,
            ToolKey = model.ToolKey,
            DisplayName = model.DisplayName,
            Description = model.Description,
            IsEnabled = model.IsEnabled,
            AccessMode = model.AccessMode
        };
        var toolId = await _platform.SaveToolAsync(tool, model.ActiveProviderId, cancellationToken);
        TempData["StatusMessage"] = "Tool saved.";
        return RedirectToAction(nameof(Edit), new { id = toolId });
    }

    [HttpPost("{toolKey}/AllowList")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddAllowList(string toolKey, string newAllowListEmail, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(newAllowListEmail))
        {
            await _platform.AddAllowListEntryAsync(toolKey, newAllowListEmail, User.GetEmailAddress(), cancellationToken);
            TempData["StatusMessage"] = "Tool allow-list user added.";
        }

        var tool = await _platform.GetToolAsync(toolKey, cancellationToken);
        return tool is null ? RedirectToAction(nameof(Index)) : RedirectToAction(nameof(Edit), new { id = tool.Id });
    }

    [HttpPost("AllowList/{id:int}/Disable")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DisableAllowListEntry(int id, string toolKey, CancellationToken cancellationToken)
    {
        await _platform.SetAllowListEntryEnabledAsync(id, false, cancellationToken);
        var tool = await _platform.GetToolAsync(toolKey, cancellationToken);
        return tool is null ? RedirectToAction(nameof(Index)) : RedirectToAction(nameof(Edit), new { id = tool.Id });
    }

    private async Task<ToolEditViewModel> CreateViewModelAsync(ToolDefinition tool, CancellationToken cancellationToken)
    {
        return new ToolEditViewModel
        {
            Id = tool.Id,
            ToolKey = tool.ToolKey,
            DisplayName = tool.DisplayName,
            Description = tool.Description,
            IsEnabled = tool.Id == 0 || tool.IsEnabled,
            AccessMode = tool.AccessMode,
            ActiveProviderId = tool.ActiveProviderId,
            Providers = await _providers.GetAllAsync(includeDisabled: false, cancellationToken),
            AllowList = string.IsNullOrWhiteSpace(tool.ToolKey)
                ? []
                : await _platform.GetAllowListAsync(tool.ToolKey, cancellationToken)
        };
    }
}
