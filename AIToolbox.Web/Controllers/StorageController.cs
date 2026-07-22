using AIToolbox.Web.Configuration;
using AIToolbox.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AIToolbox.Web.Controllers;

[Authorize(Policy = "AdminOnly")]
[Route("Admin/Storage")]
public sealed class StorageController : Controller
{
    private readonly StorageOptions _options;

    public StorageController(IOptions<StorageOptions> options)
    {
        _options = options.Value;
    }

    [HttpGet("")]
    public IActionResult Index()
    {
        return View(new StorageIndexViewModel
        {
            BasePath = _options.BasePath,
            MaxUploadBytes = _options.MaxUploadBytes,
            ToolSubpaths = _options.ToolSubpaths
        });
    }
}
