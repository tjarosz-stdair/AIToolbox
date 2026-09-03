using AIToolbox.Web.Models;

namespace AIToolbox.Web.Services.CallEntityExtraction;

/// <summary>
/// Mirrors this pipeline's already-in-hand data (incoming transcript lines, outgoing entity
/// notifications, recorded dispositions) to the admin Live Call Monitor's SignalR hub. This is
/// pure fan-out on top of data the pipeline already produces for other reasons -- it does not
/// observe the Hub's or Tray's SignalR traffic, and callers should treat every method as
/// best-effort (never let a broadcast failure affect the pipeline's real work).
/// </summary>
public interface ICallActivityBroadcaster
{
    Task ActiveCallStartedAsync(string callId, string agentKey);
    Task CallEndedAsync(string callId);
    Task TranscriptLineAppendedAsync(string callId, string agentKey, TranscriptLine line);
    Task EntityDetectedAsync(EntityDetectedNotification notification);
    Task DispositionRecordedAsync(CallEventDispositionRequest disposition);
}
