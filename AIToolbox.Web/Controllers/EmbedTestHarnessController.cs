using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers;

/// <summary>
/// A manual broker simulator for /embed/chat — admin-only diagnostic tool, not a customer-facing
/// surface. Neither PIC Search's own UI trigger nor the Chrome extension broker exist yet, so
/// this is currently the only way to drive the panel's postMessage protocol at all. See the plan
/// doc for why: PIC Search's surface adapter/self-broker is P21 Toolbox-side work, and the
/// extension broker is deferred pending its own session-token acquisition design (§7).
///
/// Requires this instance's own origin to be added to Embed:ParentOrigins, or FrameGuardMiddleware
/// correctly refuses to let this page frame /embed/chat at all — that's the deny-by-default
/// framing policy working as designed, not something this controller works around.
/// </summary>
[Authorize(Policy = "AdminOnly")]
[Route("Admin/EmbedTestHarness")]
public sealed class EmbedTestHarnessController : Controller
{
    [HttpGet("")]
    public IActionResult Index()
    {
        var ownOrigin = $"{Request.Scheme}://{Request.Host}";
        ViewData["OwnOrigin"] = ownOrigin;
        return View();
    }
}
