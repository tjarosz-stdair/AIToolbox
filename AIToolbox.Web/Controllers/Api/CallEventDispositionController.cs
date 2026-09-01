using AIToolbox.Web.Models;
using AIToolbox.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers.Api;

/// <summary>
/// Call Data API -> us. Per AIToolbox/TODO_Lists/call-data-api-contract.md, this is purely
/// telemetry/logging -- it never triggers action execution (running a PIC search, adding to an
/// order, etc. is not this pipeline's responsibility in this phase).
///
/// TODO(logging phase): wire this into ConversationUsageRollup's EntitiesPromptedCount/
/// ActionsAcceptedCount/ActionsDismissedCount counters once the Conversation/ConversationMessage
/// logging piece (parent plan's "Logging, replay &amp; admin reporting") lands -- today this only logs.
/// </summary>
[ApiController]
[Authorize(Policy = "ApiConsumer")]
[Route("api/v1/call-events")]
public sealed class CallEventDispositionController : ControllerBase
{
    private static readonly HashSet<string> ValidDispositions = new(StringComparer.OrdinalIgnoreCase)
    {
        "action_selected", "dismissed", "expired", "delivery_failed"
    };

    private readonly ILogger<CallEventDispositionController> _logger;

    public CallEventDispositionController(ILogger<CallEventDispositionController> logger)
    {
        _logger = logger;
    }

    [HttpPost("{entityId}/disposition")]
    public IActionResult PostDisposition(string entityId, [FromBody] CallEventDispositionRequest request, CancellationToken cancellationToken)
    {
        if (!User.IsApiConsumer() || !User.HasApiScope("callevents:disposition"))
        {
            return Forbid();
        }

        if (!string.Equals(entityId, request.EntityId, StringComparison.Ordinal))
        {
            return BadRequest(new { error = "entityId in the route and body must match." });
        }

        if (!ValidDispositions.Contains(request.Disposition))
        {
            return BadRequest(new { error = "disposition must be one of: action_selected, dismissed, expired, delivery_failed." });
        }

        if (string.Equals(request.Disposition, "action_selected", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(request.SelectedActionId))
        {
            return BadRequest(new { error = "selectedActionId is required when disposition is action_selected." });
        }

        _logger.LogInformation(
            "Call event disposition recorded: entity {EntityId}, call {CallId}, disposition {Disposition}, action {SelectedActionId}.",
            request.EntityId,
            request.CallId,
            request.Disposition,
            request.SelectedActionId);

        return Accepted(new { recorded = true });
    }
}
