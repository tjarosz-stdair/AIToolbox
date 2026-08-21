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
}
