using System.Text.Json;

namespace AIToolbox.Web.Services;

public sealed record PicSearchTurnRequest(
    string ContextKey,
    string? SessionToken,
    string UserMessage,
    IReadOnlyList<string> UiSurfaces,
    JsonElement? PageContext,
    bool AutoNavigate);

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

/// <summary>One ui_ tool call the model actually made this turn and that queued successfully
/// (not one the 2-per-turn cap rejected). Arguments values are JsonElement clones, same as
/// FunctionCallContent.Arguments — directly serializable into the "intent" NDJSON event.</summary>
public sealed record UiIntentDescriptor(string Name, string Kind, string Surface, IDictionary<string, object?> Arguments);

/// <summary>Result of a manual conversation reset (Part 4.5.6's ClosedManual trigger — defined on
/// ConversationStatus since that part was written, never wired to a caller until now). Closing a
/// conversation that's already gone (or was never started) is treated as success: the caller's
/// desired end state — no active conversation for this context — already holds either way.</summary>
public sealed record PicSearchResetResult(bool Succeeded, string? Code, string? ErrorMessage);

public sealed record PicSearchTurnResult(
    int ConversationId,
    bool ConversationWasReset,
    string? ResetMessage,
    string AssistantText,
    IReadOnlyList<string> ToolsUsed,
    IReadOnlyList<UiIntentDescriptor> Intents,
    bool SessionTokenPresent,
    bool SessionTokenValid,
    string? Code,
    string? ErrorMessage)
{
    public bool Succeeded => ErrorMessage is null;
}
