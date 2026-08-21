namespace AIToolbox.Web.Models;

/// <summary>
/// Daily-bucketed aggregates, retained indefinitely, deliberately carrying no customer/order
/// identifiers and no message text — that absence is precisely what makes indefinite retention
/// safe here, unlike Conversations/ConversationMessages.
/// </summary>
public sealed class ConversationUsageRollup
{
    public DateOnly Date { get; set; }

    public string Surface { get; set; } = string.Empty;

    public int ConversationCount { get; set; }

    public int TurnCount { get; set; }

    public int TokensIn { get; set; }

    public int TokensOut { get; set; }

    public decimal EstimatedCostUsd { get; set; }

    public int DegradedTierCount { get; set; }

    public int BudgetExhaustedCount { get; set; }

    public int FailureCount { get; set; }
}
