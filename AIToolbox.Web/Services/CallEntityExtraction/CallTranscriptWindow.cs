using System.Collections.Concurrent;

namespace AIToolbox.Web.Services.CallEntityExtraction;

/// <summary>
/// Per-call sliding transcript state. Azure STT can finalize a segment boundary mid-identifier on
/// a pause, not just insert spaces mid-token, so candidate extraction runs against the stitched
/// tail of recent final segments rather than each segment in isolation. Not persisted — this is
/// live-call scratch state; the durable record is the Conversation/ConversationMessage rows
/// written by the logging piece (see the parent plan's "Logging, replay & admin reporting").
/// </summary>
public sealed class CallTranscriptWindow
{
    private const int MaxWindowWords = 30;

    private readonly object _gate = new();
    private readonly List<string> _recentWords = [];
    private readonly HashSet<string> _emittedCanonicalValues = new(StringComparer.OrdinalIgnoreCase);

    public string CallId { get; }

    public DateTime LastActivityUtc { get; private set; }

    /// <summary>UtteranceId/speaker of the most recently appended segment -- a best-effort
    /// provenance tag for candidates extracted from the current window, not a precise per-word
    /// mapping (the window stitches multiple segments together and doesn't retain per-word
    /// utterance origin).</summary>
    public string LastUtteranceId { get; private set; } = string.Empty;
    public string LastSpeaker { get; private set; } = string.Empty;

    public CallTranscriptWindow(string callId)
    {
        CallId = callId;
        LastActivityUtc = DateTime.UtcNow;
    }

    /// <summary>Appends a final segment's text and returns the stitched window to extract from.</summary>
    public string AppendFinalSegment(string text, string utteranceId, string speaker)
    {
        lock (_gate)
        {
            LastActivityUtc = DateTime.UtcNow;
            LastUtteranceId = utteranceId;
            LastSpeaker = speaker;

            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _recentWords.AddRange(words);

            var overflow = _recentWords.Count - MaxWindowWords;
            if (overflow > 0)
            {
                _recentWords.RemoveRange(0, overflow);
            }

            return string.Join(' ', _recentWords);
        }
    }

    /// <summary>The current stitched window text, without appending anything new.</summary>
    public string CurrentText()
    {
        lock (_gate)
        {
            return string.Join(' ', _recentWords);
        }
    }

    /// <summary>True if this is the first time this canonical value has been seen for this call
    /// (and it's now marked seen); false if it already slid through the window before.</summary>
    public bool TryMarkEmitted(string canonicalValue)
    {
        lock (_gate)
        {
            return _emittedCanonicalValues.Add(canonicalValue);
        }
    }
}

public interface ICallTranscriptWindowStore
{
    CallTranscriptWindow GetOrCreate(string callId);

    /// <summary>Drops call state that's seen no activity for longer than <paramref name="idleThreshold"/>.</summary>
    int RemoveIdle(TimeSpan idleThreshold);
}

public sealed class CallTranscriptWindowStore : ICallTranscriptWindowStore
{
    private readonly ConcurrentDictionary<string, CallTranscriptWindow> _windows = new(StringComparer.OrdinalIgnoreCase);

    public CallTranscriptWindow GetOrCreate(string callId)
    {
        return _windows.GetOrAdd(callId, id => new CallTranscriptWindow(id));
    }

    public int RemoveIdle(TimeSpan idleThreshold)
    {
        var cutoffUtc = DateTime.UtcNow - idleThreshold;
        var idleCallIds = _windows
            .Where(pair => pair.Value.LastActivityUtc < cutoffUtc)
            .Select(pair => pair.Key)
            .ToList();

        foreach (var callId in idleCallIds)
        {
            _windows.TryRemove(callId, out _);
        }

        return idleCallIds.Count;
    }
}
