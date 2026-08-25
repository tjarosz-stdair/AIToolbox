using AIToolbox.Web.Data;
using AIToolbox.Web.Models;
using AIToolbox.Web.Security;
using AIToolbox.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers;

/// <summary>
/// Serves the embeddable chat panel (Part C). [AllowAnonymous] is load-bearing here for the same
/// reason as on PicSearchChatController — the app's global AuthorizeFilter("ActiveUser") applies
/// by default, and the caller is a P21 Toolbox page with no AIToolbox login, not an AIToolbox user.
/// FrameGuardMiddleware skips this route entirely (it only guards non-/embed paths), so this
/// controller owns setting its own framing/CSP headers on every response, including the 403 case.
/// </summary>
[AllowAnonymous]
[Route("embed")]
public sealed class EmbedController : Controller
{
    private readonly IEmbedOriginResolver _originResolver;
    private readonly IExternalResourceRepository _externalResources;

    public EmbedController(IEmbedOriginResolver originResolver, IExternalResourceRepository externalResources)
    {
        _originResolver = originResolver;
        _externalResources = externalResources;
    }

    [HttpGet("chat")]
    public async Task<IActionResult> Chat([FromQuery(Name = "parentOrigin")] string? parentOrigin, CancellationToken cancellationToken)
    {
        var resolvedOrigin = _originResolver.Resolve(parentOrigin);
        if (resolvedOrigin is null)
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            ApplyNotEmbeddableHeaders();
            return View("NotEmbeddable");
        }

        ApplyEmbedHeaders(resolvedOrigin);
        var speechResource = await _externalResources.GetEnabledByTypeAsync(ExternalResourceType.AzureSpeech, cancellationToken);
        return View("Chat", new EmbedChatViewModel
        {
            ExpectedParentOrigin = resolvedOrigin,
            SpeechEnabled = speechResource is not null
        });
    }

    private void ApplyEmbedHeaders(string resolvedOrigin)
    {
        // script-src/style-src 'self' with no 'unsafe-inline' makes "no inline <script>, no
        // inline style=" a hard requirement for the view/JS, not just a house-style preference.
        // Deliberately no X-Frame-Options here: ALLOW-FROM is unsupported in Chrome/Safari, and
        // emitting DENY alongside a permissive frame-ancestors would break the embed —
        // frame-ancestors alone is correct and sufficient in every browser this app targets.
        Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; " +
            $"img-src 'self' data:; frame-ancestors {resolvedOrigin}; base-uri 'none'; form-action 'none'; object-src 'none'";
        Response.Headers["Cache-Control"] = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }

    private void ApplyNotEmbeddableHeaders()
    {
        Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
        Response.Headers["Cache-Control"] = "no-store";
    }
}
