namespace AIToolbox.Web.Services;

public sealed record PicSearchTurnRequest(string ContextKey, string? SessionToken, string UserMessage);

public sealed record PicSearchTurnResult(
    int ConversationId,
    bool ConversationWasReset,
    string? ResetMessage,
    string AssistantText,
    bool SessionTokenPresent,
    bool SessionTokenValid,
    string? ErrorMessage)
{
    public bool Succeeded => ErrorMessage is null;
}
