using System.Text.Json;
using AIToolbox.Web.Models;
using Microsoft.Extensions.AI;

namespace AIToolbox.Web.Services;

/// <summary>
/// The only correct way to round-trip a ConversationMessage's ContentJson: it's
/// JsonSerializer.Serialize(message.Contents, AIJsonUtilities.DefaultOptions) where Contents is a
/// polymorphic IList&lt;AIContent&gt; (TextContent, FunctionCallContent, FunctionResultContent, ...),
/// not plain text — AIJsonUtilities.DefaultOptions carries the type-discriminator contract that
/// makes Deserialize&lt;List&lt;AIContent&gt;&gt; work at all. Extracted out of PicSearchChatService so the
/// admin "AI Chat" transcript viewer can reuse it instead of duplicating (or hand-parsing) this.
/// </summary>
public static class ConversationMessageCodec
{
    public static ConversationMessage Serialize(
        int conversationId,
        ChatMessage message,
        int sequence,
        string? modelId,
        int? aiProviderId)
    {
        return new ConversationMessage
        {
            ConversationId = conversationId,
            Role = message.Role.Value,
            Sequence = sequence,
            ContentJson = JsonSerializer.Serialize(message.Contents, AIJsonUtilities.DefaultOptions),
            ModelId = modelId,
            AiProviderId = aiProviderId
        };
    }

    public static ChatMessage Deserialize(ConversationMessage stored)
    {
        var contents = JsonSerializer.Deserialize<List<AIContent>>(stored.ContentJson, AIJsonUtilities.DefaultOptions) ?? [];
        return new ChatMessage(new ChatRole(stored.Role), contents);
    }
}
