namespace AIToolbox.Web.Services;

public interface IPicSearchChatService
{
    Task<PicSearchTurnResult> SubmitTurnAsync(PicSearchTurnRequest request, CancellationToken cancellationToken = default);

    Task<PicSearchResetResult> ResetConversationAsync(string? sessionToken, string contextKey, CancellationToken cancellationToken = default);
}
