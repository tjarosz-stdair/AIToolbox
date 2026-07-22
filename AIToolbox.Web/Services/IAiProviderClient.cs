using AIToolbox.Web.Models;

namespace AIToolbox.Web.Services;

public interface IAiProviderClient
{
    Task<AiToolCallResult> SendPromptAsync(
        AiProvider provider,
        string prompt,
        CancellationToken cancellationToken = default);

    Task<DocumentPromptResult> SendDocumentPromptAsync(
        DocumentPromptRequest request,
        CancellationToken cancellationToken = default);
}
