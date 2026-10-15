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

    /// <summary>
    /// Sends images and a directive to a vision-enabled provider. Throws
    /// <see cref="ImagePromptRejectedException"/> before any network call when the provider is not
    /// vision-enabled, the provider type has no image route, or an image is missing, too large or of
    /// an unsupported type. Approval for hosted use is enforced by the Ductwork gateway, not here.
    /// </summary>
    Task<DocumentPromptResult> SendImagePromptAsync(
        ImagePromptRequest request,
        CancellationToken cancellationToken = default);
}
