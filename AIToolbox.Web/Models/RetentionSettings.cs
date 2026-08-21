namespace AIToolbox.Web.Models;

/// <summary>
/// Single row per instance. Governs full-text retention for Conversations/ConversationMessages
/// only — the indefinite ConversationUsageRollup statistics are unaffected by this setting.
/// </summary>
public sealed class RetentionSettings
{
    public int ConversationRetentionDays { get; set; } = 14;

    public DateTime UpdatedUtc { get; set; }
}
