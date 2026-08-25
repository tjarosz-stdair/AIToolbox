using System.Text.Json;
using AIToolbox.Web.Data;
using AIToolbox.Web.Models;
using AIToolbox.Web.Security;
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
public sealed class PicSearchChatController : Controller, IDisposable
{
    private const string SessionTokenHeaderName = "X-P21-Session-Token";
    private const long MaxSpeechAudioBytes = 5 * 1024 * 1024;
    public const string RateLimiterPolicyName = "picsearch-chat";
    public const string SpeechRateLimiterPolicyName = "picsearch-speech";

    private readonly IPicSearchChatService _chatService;
    private readonly IMarkdownRenderer _markdownRenderer;
    private readonly ISessionTokenValidator _sessionTokens;
    private readonly IExternalResourceRepository _externalResources;
    private readonly ISpeechTranscriptionService _speechTranscription;
    private readonly ILogger<PicSearchChatController> _logger;

    // AiChatClientFactory allows concurrent tool invocation (AllowConcurrentInvocation = true), so
    // the toolStep callback below can fire from two tool calls at once — Response.Body is not
    // safe for concurrent writers, so every WriteEventAsync call (toolStep included) is
    // serialized through this.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public PicSearchChatController(
        IPicSearchChatService chatService,
        IMarkdownRenderer markdownRenderer,
        ISessionTokenValidator sessionTokens,
        IExternalResourceRepository externalResources,
        ISpeechTranscriptionService speechTranscription,
        ILogger<PicSearchChatController> logger)
    {
        _chatService = chatService;
        _markdownRenderer = markdownRenderer;
        _sessionTokens = sessionTokens;
        _externalResources = externalResources;
        _speechTranscription = speechTranscription;
        _logger = logger;
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
                onToolStep: (step, ct) => WriteEventAsync(new { type = "toolStep", name = step.ToolName, description = step.Description }, ct),
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

    [HttpPost("history")]
    public async Task<IActionResult> History([FromBody] PicSearchResetRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Ok(new { success = false, code = PicSearchErrorCode.BadRequest, message = "A context is required." });
        }

        var sessionToken = Request.Headers.TryGetValue(SessionTokenHeaderName, out var headerValues)
            ? headerValues.ToString()
            : null;

        var result = await _chatService.GetActiveConversationHistoryAsync(sessionToken, request.ContextKey, cancellationToken);

        if (!result.Succeeded)
        {
            return Ok(new { success = false, code = result.Code, message = result.ErrorMessage });
        }

        if (result.Conversation is null)
        {
            return Ok(new { success = true, hasActiveConversation = false });
        }

        var messages = result.Messages.Select(m => new
        {
            role = m.Role,
            text = m.Text,
            html = string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? _markdownRenderer.Render(m.Text).ToString()
                : null
        });

        return Ok(new
        {
            success = true,
            hasActiveConversation = true,
            conversationId = result.Conversation.Id,
            turnCount = result.Conversation.UserTurnCount,
            lastActiveUtc = result.Conversation.LastActiveUtc,
            messages
        });
    }

    [HttpPost("speech-to-text")]
    [EnableRateLimiting(SpeechRateLimiterPolicyName)]
    public async Task<IActionResult> SpeechToText(CancellationToken cancellationToken)
    {
        var sessionToken = Request.Headers.TryGetValue(SessionTokenHeaderName, out var headerValues)
            ? headerValues.ToString()
            : null;

        var tokenResult = await _sessionTokens.ValidateAsync(sessionToken, cancellationToken);
        if (!tokenResult.Present)
        {
            return Ok(new { success = false, code = PicSearchErrorCode.SessionTokenRequired, message = "A valid P21 Toolbox session token is required." });
        }
        if (!tokenResult.IsValid)
        {
            return Ok(new { success = false, code = PicSearchErrorCode.SessionTokenRejected, message = $"Session token rejected ({tokenResult.RejectReason})." });
        }

        if (Request.ContentLength is > MaxSpeechAudioBytes)
        {
            return Ok(new { success = false, code = PicSearchErrorCode.BadRequest, message = "That recording is too long." });
        }

        var resource = await _externalResources.GetEnabledByTypeAsync(ExternalResourceType.AzureSpeech, cancellationToken);
        if (resource is null)
        {
            // Deliberately not logged on every /embed/chat page load (EmbedController skips the
            // mic button entirely when unconfigured) — this only fires if the endpoint is hit
            // anyway (a stale cached page, a race, or a hand-crafted request), which is rare
            // enough to be worth a log line rather than routine noise.
            _logger.LogWarning("PIC Search speech-to-text was requested but no enabled Azure Speech external resource is configured.");
            return Ok(new { success = false, code = PicSearchErrorCode.NotConfigured, message = "Voice input isn't configured yet. An admin needs to set this up in Admin > External Resources." });
        }

        // Logged every attempt, not just failures: ContentLength is the one fact that answers
        // "did the browser actually send audio" independently of whatever Azure does with it — a
        // suspiciously tiny value (a few hundred bytes for what should be a several-second clip)
        // points at the capture/relay path, not Azure, before ever looking at the response.
        _logger.LogInformation(
            "PIC Search speech-to-text request: {ContentLength} bytes, contentType={ContentType}",
            Request.ContentLength, Request.ContentType);

        var result = await _speechTranscription.TranscribeAsync(resource, Request.Body, Request.ContentType, cancellationToken);
        _logger.LogInformation("PIC Search speech-to-text result: succeeded={Succeeded}", result.Succeeded);
        return Ok(result.Succeeded
            ? new { success = true, text = result.Text }
            : new { success = false, code = PicSearchErrorCode.TranscriptionFailed, message = result.ErrorMessage });
    }

    public new void Dispose()
    {
        _writeLock.Dispose();
    }

    private async Task WriteEventAsync(object payload, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await Response.WriteAsync(JsonSerializer.Serialize(payload), cancellationToken);
            await Response.WriteAsync("\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
