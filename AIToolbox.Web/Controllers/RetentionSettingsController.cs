using AIToolbox.Web.Data;
using AIToolbox.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers;

[Authorize(Policy = "AdminOnly")]
[Route("Admin/RetentionSettings")]
public sealed class RetentionSettingsController : Controller
{
    private readonly IRetentionSettingsRepository _settings;

    public RetentionSettingsController(IRetentionSettingsRepository settings)
    {
        _settings = settings;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken);
        return View(new RetentionSettingsFormViewModel { ConversationRetentionDays = settings.ConversationRetentionDays });
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(RetentionSettingsFormViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        await _settings.UpdateAsync(model.ConversationRetentionDays, cancellationToken);
        TempData["StatusMessage"] = "Retention settings saved.";
        return RedirectToAction(nameof(Index));
    }
}
