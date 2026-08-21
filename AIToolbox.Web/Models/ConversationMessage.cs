namespace AIToolbox.Web.Models;

/// <summary>
/// ContentJson is the serialized Microsoft.Extensions.AI content list (TextContent,
/// FunctionCallContent, FunctionResultContent, ...) via AIJsonUtilities.DefaultOptions — the full
/// tool-use/tool-result shape, not just display text, since it must round-trip back into a
/// ChatMessage to resend as history on the next turn.
/// </summary>
public sealed class ConversationMessage
{
    public int Id { get; set; }

    public int ConversationId { get; set; }

    public string Role { get; set; } = string.Empty;

    public int Sequence { get; set; }

    public string ContentJson { get; set; } = string.Empty;

    public string? ModelId { get; set; }

    public int? AiProviderId { get; set; }

    public DateTime CreatedUtc { get; set; }
}
