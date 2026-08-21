using AIToolbox.Web.Models;

namespace AIToolbox.Web.Data;

public interface IConversationRepository
{
    Task<Conversation?> GetActiveConversationAsync(
        string sub,
        string surface,
        string contextKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Any active conversation for this sub+surface regardless of context key — used to detect a
    /// context change (Part 4.5.5) so the stale one can be closed with ClosedContextChange rather
    /// than left orphaned (never closed, so never entering the retention purge countdown).
    /// </summary>
    Task<Conversation?> GetActiveConversationForSurfaceAsync(
        string sub,
        string surface,
        CancellationToken cancellationToken = default);

    Task<Conversation?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<int> CreateConversationAsync(Conversation conversation, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(int conversationId, CancellationToken cancellationToken = default);

    Task<int> AppendMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default);

    Task RecordTurnAsync(int conversationId, int tokensInDelta, int tokensOutDelta, CancellationToken cancellationToken = default);

    Task CloseConversationAsync(int conversationId, ConversationStatus status, CancellationToken cancellationToken = default);

    /// <summary>Closed conversations whose retention countdown (from ClosedUtc) has expired.</summary>
    Task<IReadOnlyList<int>> GetPurgeableConversationIdsAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default);

    Task DeleteConversationAsync(int conversationId, CancellationToken cancellationToken = default);
}
