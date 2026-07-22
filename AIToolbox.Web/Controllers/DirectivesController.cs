using AIToolbox.Web.Data;
using AIToolbox.Web.Models;
using AIToolbox.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers;

[Authorize(Policy = "AdminOnly")]
[Route("Admin/Directives")]
public sealed class DirectivesController : Controller
{
    private readonly IPlatformRepository _platform;

    public DirectivesController(IPlatformRepository platform)
    {
        _platform = platform;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        return View(await _platform.GetDirectivesAsync(cancellationToken));
    }

    [HttpGet("Create")]
    public IActionResult Create()
    {
        return View("Edit", new DirectiveEditViewModel
        {
            Name = "Default product extraction",
            TargetSchemaJson = "{\"ProductName\":null,\"ProductCode\":null,\"Description\":null}"
        });
    }

    [HttpGet("Edit/{id:int}")]
    public async Task<IActionResult> Edit(int id, CancellationToken cancellationToken)
    {
        var directive = await _platform.GetDirectiveAsync(id, cancellationToken);
        return directive is null ? NotFound() : View(ToViewModel(directive));
    }

    [HttpPost("Edit/{id:int?}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int? id, DirectiveEditViewModel model, CancellationToken cancellationToken)
    {
        if (id is not null && id.Value != model.Id)
        {
            return BadRequest();
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var directiveId = await _platform.SaveDirectiveAsync(new ToolDirective
        {
            Id = model.Id,
            ToolKey = model.ToolKey,
            Name = model.Name,
            SystemPrompt = model.SystemPrompt,
            TargetSchemaJson = model.TargetSchemaJson,
            Version = model.Version,
            IsActive = model.IsActive
        }, cancellationToken);
        TempData["StatusMessage"] = "Directive saved.";
        return RedirectToAction(nameof(Edit), new { id = directiveId });
    }

    private static DirectiveEditViewModel ToViewModel(ToolDirective directive)
    {
        return new DirectiveEditViewModel
        {
            Id = directive.Id,
            ToolKey = directive.ToolKey,
            Name = directive.Name,
            SystemPrompt = directive.SystemPrompt,
            TargetSchemaJson = directive.TargetSchemaJson,
            Version = directive.Version,
            IsActive = directive.IsActive
        };
    }
}
