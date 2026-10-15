namespace AIToolbox.Web.Models;

/// <summary>
/// A prompt that carries images (page crops, tables) to a vision-capable provider. Sending drawing
/// content to a hosted model is a data-governance event: callers other than the Ductwork LLM
/// gateway must not use this directly, and the gateway refuses it without a valid approval
/// (Ductwork plan section 6.12). This request type only describes the call.
/// </summary>
public sealed class ImagePromptRequest
{
    public required AiProvider Provider { get; init; }
    public required IReadOnlyList<ImagePromptImage> Images { get; init; }
    public required string Directive { get; init; }
}

public sealed class ImagePromptImage
{
    public required byte[] Bytes { get; init; }
    public required string MediaType { get; init; }
    public string? Label { get; init; }
}

public static class ImagePromptLimits
{
    public const int MaxImagesPerRequest = 20;

    /// <summary>The strictest per-image limit among the supported providers (Anthropic, 5 MB).</summary>
    public const int MaxImageBytes = 5 * 1024 * 1024;

    public static readonly IReadOnlySet<string> AllowedMediaTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/webp", "image/gif" };
}

/// <summary>Raised before any network call when an image request cannot or must not be sent.</summary>
public sealed class ImagePromptRejectedException(string message) : InvalidOperationException(message);
