using AIToolbox.Web.Models;
using Microsoft.Extensions.AI;

namespace AIToolbox.Web.Services;

public interface IAiChatClientFactory
{
    /// <summary>
    /// Builds a tool-calling-capable IChatClient bound to the given AiProvider row. Reuses the
    /// same endpoint/auth/header conventions as AiProviderClient (the existing AI Compare path)
    /// but supports the richer message-history + tool-call/tool-result wire shape a real agent
    /// loop needs, which the flat-prompt AiProviderClient API does not. Only AiProviderType.
    /// OpenAiCompatible, AzureOpenAi, and Anthropic are supported for tool calling today.
    /// </summary>
    /// <param name="onToolStep">
    /// Optional callback invoked once per tool call, right before it runs, so a caller streaming
    /// an NDJSON response can report live status ("Looking up part specs…") instead of a static
    /// placeholder for the whole (possibly multi-iteration) tool-calling loop.
    /// </param>
    IChatClient Create(AiProvider provider, Func<ToolStepEvent, CancellationToken, Task>? onToolStep = null);
}
