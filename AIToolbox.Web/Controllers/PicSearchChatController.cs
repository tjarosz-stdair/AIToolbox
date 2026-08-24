using System.Text.Json;
using AIToolbox.Web.Services;
using AIToolbox.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers;

/// <summary>
/// The picsearch chat surface's HTTP entry point. The caller is P21 Toolbox's own PIC Search
/// page (or, once built, the embed panel calling same-origin), carrying the X-P21-Session-Token
/// header — never an AIToolbox-authenticated user. [AllowAnonymous] here is LOAD-BEARING, not
/// redundant: the app registers a global AuthorizeFilter("ActiveUser") in Program.cs that applies
/// to every controller by default, and without this attribute an unauthenticated caller would be
/// redirected/401'd before this action ever runs — which is exactly what happened here until this
/// was added (see plan doc for the incident). This endpoint's real protection is
/// PicSearchChatService's own session-token validation (Part 2) plus the rate limit below — never
/// trust the header without validating it, and never remove [AllowAnonymous] without replacing it
/// with something that still lets a real anonymous-to-AIToolbox caller through.
/// </summary>
[ApiController]
[Route("api/picsearch")]
[AllowAnonymous]
[EnableCors("PicSearchChat")]
[EnableRateLimiting(RateLimiterPolicyName)]
public sealed class PicSearchChatController : Controller
{
    private const string SessionTokenHeaderName = "X-P21-Session-Token";
    public const string RateLimiterPolicyName = "picsearch-chat";

    private readonly IPicSearchChatService _chatService;
    private readonly IMarkdownRenderer _markdownRenderer;

    public PicSearchChatController(IPicSearchChatService chatService, IMarkdownRenderer markdownRenderer)
    {
        _chatService = chatService;
        _markdownRenderer = markdownRenderer;
    }

    [HttpPost("ask")]
    public async Task Ask([FromBody] PicSearchAskRequest request, CancellationToken cancellationToken)
    {
        Response.ContentType = "application/x-ndjson";
        Response.Headers["Cache-Control"] = "no-cache";

        if (!ModelState.IsValid)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteEventAsync(
                new { type = "error", code = PicSearchErrorCode.BadRequest, message = "A context and message are required." },
                cancellationToken);
            return;
        }

        await Response.StartAsync(cancellationToken);

        try
        {
            await WriteEventAsync(new { type = "started" }, cancellationToken);

            var sessionToken = Request.Headers.TryGetValue(SessionTokenHeaderName, out var headerValues)
                ? headerValues.ToString()
                : null;

            var result = await _chatService.SubmitTurnAsync(
                new PicSearchTurnRequest(
                    request.ContextKey,
                    sessionToken,
                    request.Message,
                    request.UiSurfaces ?? [],
                    request.PageContext,
                    request.AutoNavigate),
                cancellationToken);

            if (!result.Succeeded)
            {
                await WriteEventAsync(new
                {
                    type = "error",
                    code = result.Code,
                    message = result.ErrorMessage,
                    sessionTokenPresent = result.SessionTokenPresent,
                    sessionTokenValid = result.SessionTokenValid
                }, cancellationToken);
                return;
            }

            if (result.ConversationWasReset)
            {
                await WriteEventAsync(new { type = "conversationReset", message = result.ResetMessage }, cancellationToken);
            }

            foreach (var intent in result.Intents)
            {
                await WriteEventAsync(new
                {
                    type = "intent",
                    name = intent.Name,
                    kind = intent.Kind,
                    surface = intent.Surface,
                    arguments = intent.Arguments
                }, cancellationToken);
            }

            // Rendered server-side via the same Markdig pipeline (DisableHtml + a link-scheme
            // allowlist) already used for AI Compare's responses — the model's output is grounded
            // in markdown, and the embed panel has no client-side markdown interpreter (nor
            // should it: this page's CSP has no 'unsafe-inline', so a client-side renderer would
            // need its own injection-safety story instead of reusing this already-proven one).
            var messageHtml = _markdownRenderer.Render(result.AssistantText).ToString();

            await WriteEventAsync(new
            {
                type = "complete",
                conversationId = result.ConversationId,
                message = result.AssistantText,
                messageHtml,
                toolsUsed = result.ToolsUsed,
                sessionTokenPresent = result.SessionTokenPresent,
                sessionTokenValid = result.SessionTokenValid
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            await WriteEventAsync(new { type = "error", code = PicSearchErrorCode.TurnFailed, message = ex.Message }, cancellationToken);
        }
    }

    [HttpPost("reset")]
    public async Task<IActionResult> Reset([FromBody] PicSearchResetRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Ok(new { success = false, code = PicSearchErrorCode.BadRequest, message = "A context is required." });
        }

        var sessionToken = Request.Headers.TryGetValue(SessionTokenHeaderName, out var headerValues)
            ? headerValues.ToString()
            : null;

        var result = await _chatService.ResetConversationAsync(sessionToken, request.ContextKey, cancellationToken);

        return Ok(result.Succeeded
            ? new { success = true }
            : new { success = false, code = result.Code, message = result.ErrorMessage });
    }

    private async Task WriteEventAsync(object payload, CancellationToken cancellationToken)
    {
        await Response.WriteAsync(JsonSerializer.Serialize(payload), cancellationToken);
        await Response.WriteAsync("\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }
}
