namespace AIToolbox.Web.Services;

public sealed record SpeechTranscodeResult(bool Succeeded, byte[]? WavBytes, string? ErrorMessage, string? FfmpegStdErrExcerpt)
{
    public static SpeechTranscodeResult Success(byte[] wavBytes) => new(true, wavBytes, null, null);

    public static SpeechTranscodeResult Failure(string errorMessage, string? ffmpegStdErrExcerpt = null) =>
        new(false, null, errorMessage, ffmpegStdErrExcerpt);
}

/// <summary>
/// Converts an arbitrary-container audio stream (WebM/Opus from Chrome's MediaRecorder, OGG/Opus
/// from Firefox, etc.) to 16-bit PCM WAV, 16kHz, mono, via an ffmpeg.exe subprocess piped over
/// stdin/stdout — no temp files. Azure's short-audio REST endpoint only documents WAV/PCM 16kHz
/// mono and OGG/Opus 16kHz mono; WebM is not on that list, which is the root cause this transcoding
/// step exists to fix (see SpeechTranscriptionService).
/// </summary>
public interface ISpeechAudioTranscoder
{
    Task<SpeechTranscodeResult> TranscodeToWavAsync(Stream input, CancellationToken cancellationToken = default);
}
