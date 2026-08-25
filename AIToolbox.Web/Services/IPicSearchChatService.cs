namespace AIToolbox.Web.Services;

public interface IPicSearchChatService
{
    Task<PicSearchTurnResult> SubmitTurnAsync(
        PicSearchTurnRequest request,
        Func<ToolStepEvent, CancellationToken, Task>? onToolStep = null,
        CancellationToken cancellationToken = default);

    Task<PicSearchResetResult> ResetConversationAsync(string? sessionToken, string contextKey, CancellationToken cancellationToken = default);

    Task<PicSearchHistoryResult> GetActiveConversationHistoryAsync(string? sessionToken, string contextKey, CancellationToken cancellationToken = default);
}
