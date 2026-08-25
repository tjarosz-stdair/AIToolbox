namespace AIToolbox.Web.Models;

/// <summary>
/// No TenantId column: this instance itself is the tenant boundary (Part 0.1), so isolation
/// doesn't need a data-level key. Sub is the verified session-token subject, never Upn.
/// </summary>
public sealed class Conversation
{
    public int Id { get; set; }

    public string Sub { get; set; } = string.Empty;

    public string Surface { get; set; } = string.Empty;

    /// <summary>The order/customer identity this conversation is bound to (Part 4.5.5). A
    /// conversation may never span two context keys.</summary>
    public string ContextKey { get; set; } = string.Empty;

    public ConversationStatus Status { get; set; } = ConversationStatus.Active;

    public int UserTurnCount { get; set; }

    public int TokensIn { get; set; }

    public int TokensOut { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime LastActiveUtc { get; set; }

    /// <summary>Set the moment a reset trigger fires. The retention purge countdown (Part 4.5.3)
    /// starts here, not at CreatedUtc, so an active conversation is never purged mid-use.</summary>
    public DateTime? ClosedUtc { get; set; }

    public bool RolledUp { get; set; }
}

public sealed class ConversationListFilter
{
    public string Surface { get; set; } = "all";

    public string Status { get; set; } = "all";

    /// <summary>Free-text, partial match — an admin's starting point is usually a partial
    /// order/customer number from a support conversation, not an exact match.</summary>
    public string? ContextKey { get; set; }

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 50;
}

public sealed class ConversationListResult
{
    public IReadOnlyList<Conversation> Items { get; set; } = [];

    public int TotalCount { get; set; }

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 50;

    public string Surface { get; set; } = "all";

    public string Status { get; set; } = "all";

    public string? ContextKey { get; set; }

    public IReadOnlyList<string> Surfaces { get; set; } = [];

    public int TotalPages => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
