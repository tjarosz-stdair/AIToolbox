using AIToolbox.Web.Models;

namespace AIToolbox.Web.Data;

public interface IConversationUsageRollupRepository
{
    /// <summary>
    /// Increments today's (Date, Surface) rollup row by the given deltas, inserting the row first
    /// if it doesn't exist yet. Called in real time as events happen (conversation created, turn
    /// completed, degraded/budget-exhausted/failure detected) rather than reconstructed later at
    /// purge time, so the indefinite rollup never depends on raw transcript data still existing.
    /// </summary>
    Task IncrementAsync(
        DateOnly date,
        string surface,
        int conversationDelta = 0,
        int turnDelta = 0,
        int tokensInDelta = 0,
        int tokensOutDelta = 0,
        decimal estimatedCostUsdDelta = 0m,
        int degradedTierDelta = 0,
        int budgetExhaustedDelta = 0,
        int failureDelta = 0,
        CancellationToken cancellationToken = default);

    /// <summary>The read side that's never existed — this is what remains once individual
    /// conversations age out of the retention window and get hard-deleted; the admin "AI Chat"
    /// review tab's usage summary panel is the only consumer.</summary>
    Task<IReadOnlyList<ConversationUsageRollup>> GetAsync(
        DateOnly fromDate,
        DateOnly toDate,
        string surface,
        CancellationToken cancellationToken = default);
}
