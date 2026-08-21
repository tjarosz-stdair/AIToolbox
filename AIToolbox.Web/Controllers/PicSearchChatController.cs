using System.Text.Json;
using AIToolbox.Web.Services;
using AIToolbox.Web.ViewModels;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers;

/// <summary>
/// The picsearch chat surface's HTTP entry point (Part 4). Deliberately not [Authorize]'d against
/// AIToolbox's own M365 sign-in — the caller is P21 Toolbox's own PIC Search page, embedded
/// cross-origin, carrying only the X-P21-Session-Token header. This endpoint's protection is the
/// explicit CORS origin allowlist (Part 4.2) plus PicSearchChatService's own session-token
/// validation (Part 2) — never a wildcard origin, and never trust the header without validating it.
/// </summary>
[ApiController]
[Route("api/picsearch")]
[EnableCors("PicSearchChat")]
public sealed class PicSearchChatController : Controller
{
    private const string SessionTokenHeaderName = "X-P21-Session-Token";

    private readonly IPicSearchChatService _chatService;

    public PicSearchChatController(IPicSearchChatService chatService)
    {
        _chatService = chatService;
    }

    [HttpPost("ask")]
    public async Task Ask([FromBody] PicSearchAskRequest request, CancellationToken cancellationToken)
    {
        Response.ContentType = "application/x-ndjson";
        Response.Headers["Cache-Control"] = "no-cache";

        if (!ModelState.IsValid)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteEventAsync(new { type = "error", message = "A context and message are required." }, cancellationToken);
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
                new PicSearchTurnRequest(request.ContextKey, sessionToken, request.Message),
                cancellationToken);

            if (!result.Succeeded)
            {
                await WriteEventAsync(new { type = "error", message = result.ErrorMessage }, cancellationToken);
                return;
            }

            if (result.ConversationWasReset)
            {
                await WriteEventAsync(new { type = "conversationReset", message = result.ResetMessage }, cancellationToken);
            }

            await WriteEventAsync(new
            {
                type = "complete",
                conversationId = result.ConversationId,
                message = result.AssistantText,
                sessionTokenPresent = result.SessionTokenPresent,
                sessionTokenValid = result.SessionTokenValid
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            await WriteEventAsync(new { type = "error", message = ex.Message }, cancellationToken);
        }
    }

    private async Task WriteEventAsync(object payload, CancellationToken cancellationToken)
    {
        await Response.WriteAsync(JsonSerializer.Serialize(payload), cancellationToken);
        await Response.WriteAsync("\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }
}
