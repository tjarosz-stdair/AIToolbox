namespace AIToolbox.Web.Services;

/// <summary>
/// Persists the transcoded WAV bytes actually sent to Azure (never the original browser-captured
/// container) to the SpeechAudioStorage tree for future debugging. Archiving is a debugging aid,
/// not a hard requirement: a failure here must never block transcription from proceeding to call
/// Azure — implementations are expected to catch and log their own failures rather than throw.
/// </summary>
public interface ISpeechAudioArchiveService
{
    Task SaveAsync(byte[] wavBytes, CancellationToken cancellationToken = default);
}
