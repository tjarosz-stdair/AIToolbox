using AIToolbox.Web.Models;

namespace AIToolbox.Web.Services;

public sealed record SpeechTranscriptionResult(bool Succeeded, string? Text, string? ErrorMessage)
{
    public static SpeechTranscriptionResult Success(string text) => new(true, text, null);

    public static SpeechTranscriptionResult Failure(string errorMessage) => new(false, null, errorMessage);
}

public interface ISpeechTranscriptionService
{
    Task<SpeechTranscriptionResult> TranscribeAsync(
        ExternalResource resource,
        Stream audio,
        string? contentType,
        CancellationToken cancellationToken = default);
}
