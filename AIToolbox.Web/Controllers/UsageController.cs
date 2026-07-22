using AIToolbox.Web.Data;
using AIToolbox.Web.Models;
using AIToolbox.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers;

[Authorize(Policy = "AdminOnly")]
[Route("Admin/Usage")]
public sealed class UsageController : Controller
{
    private readonly IPlatformRepository _platform;

    public UsageController(IPlatformRepository platform)
    {
        _platform = platform;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(string? userEmail, string? toolKey, CancellationToken cancellationToken)
    {
        return View(new UsageIndexViewModel
        {
            UserEmail = userEmail,
            ToolKey = toolKey,
            Usage = await _platform.GetUsageAsync(userEmail, toolKey, cancellationToken),
            Pricing = await _platform.GetPricingAsync(cancellationToken)
        });
    }

    [HttpPost("Pricing")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddPricing(ModelPricingFormViewModel model, CancellationToken cancellationToken)
    {
        if (ModelState.IsValid)
        {
            await _platform.AddPricingAsync(new ModelPricing
            {
                ProviderType = model.ProviderType,
                ModelName = model.ModelName,
                InputPricePer1kUsd = model.InputPricePer1kUsd,
                OutputPricePer1kUsd = model.OutputPricePer1kUsd
            }, cancellationToken);
            TempData["StatusMessage"] = "Model pricing saved.";
        }

        return RedirectToAction(nameof(Index));
    }
}
