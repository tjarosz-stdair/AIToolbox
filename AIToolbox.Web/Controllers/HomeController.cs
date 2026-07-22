using System.Diagnostics;
using AIToolbox.Web.Data;
using AIToolbox.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers;

public class HomeController : Controller
{
    private readonly IPlatformRepository _platform;
    private readonly ILogger<HomeController> _logger;

    public HomeController(IPlatformRepository platform, ILogger<HomeController> logger)
    {
        _platform = platform;
        _logger = logger;
    }

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var tools = await _platform.GetToolsAsync(cancellationToken);
        return View(tools.Where(tool => tool.IsEnabled).ToList());
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}