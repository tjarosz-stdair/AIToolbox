namespace AIToolbox.Web.Models;

/// <summary>
/// Every value except Active is a reset trigger (Part 4.5.6) that closed the conversation and
/// started the retention countdown (Part 4.5.3) from that moment, not from CreatedUtc.
/// </summary>
public enum ConversationStatus
{
    Active = 0,
    ClosedManual = 1,
    ClosedContextChange = 2,
    ClosedTurnCap = 3,
    ClosedTokenCap = 4,
    ClosedInactivity = 5
}
