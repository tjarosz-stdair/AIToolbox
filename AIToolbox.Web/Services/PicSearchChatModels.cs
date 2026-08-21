using System.Text.Json;

namespace AIToolbox.Web.Services;

public sealed record PicSearchTurnRequest(
    string ContextKey,
    string? SessionToken,
    string UserMessage,
    IReadOnlyList<string> UiSurfaces,
    JsonElement? PageContext);

/// <summary>
/// Machine-readable codes for a failed turn — AIToolbox's own internal contract (nothing on the
/// P21 Toolbox side reads this), added so the embed panel can react to a rejected/missing token
/// without substring-matching human-readable error text.
/// </summary>
public static class PicSearchErrorCode
{
    public const string SessionTokenRequired = "session_token_required";
    public const string SessionTokenRejected = "session_token_rejected";
    public const string NotConfigured = "not_configured";
    public const string TurnFailed = "turn_failed";
    public const string BadRequest = "bad_request";
}

public sealed record PicSearchTurnResult(
    int ConversationId,
    bool ConversationWasReset,
    string? ResetMessage,
    string AssistantText,
    IReadOnlyList<string> ToolsUsed,
    bool SessionTokenPresent,
    bool SessionTokenValid,
    string? Code,
    string? ErrorMessage)
{
    public bool Succeeded => ErrorMessage is null;
}
