using System.Collections.Concurrent;

namespace AIToolbox.Web.Services.CallEntityExtraction;

/// <summary>
/// Per-call sliding transcript state. Azure STT can finalize a segment boundary mid-identifier on
/// a pause, not just insert spaces mid-token, so candidate extraction runs against the stitched
/// tail of recent final segments rather than each segment in isolation. Not persisted — this is
/// live-call scratch state; the durable record is the Conversation/ConversationMessage rows
/// written by the logging piece (see the parent plan's "Logging, replay & admin reporting").
/// </summary>
/// <summary>One recognized transcript line, retained in a bounded per-call ring buffer purely for
/// admin-facing replay (the Live Call Monitor) -- not used by candidate extraction, which only
/// ever needs the stitched word window below. CallElapsedMs is computed once at append time from
/// CapturedAtUtc minus the window's StartedAtUtc -- deliberately not the wire contract's
/// per-segment OffsetMs (relative to that segment's own Azure session, which starts at a
/// different real time per side/segment and so wouldn't give Agent/External lines a shared
/// clock).</summary>
public sealed record TranscriptLine(
    string UtteranceId, string Side, string Text, int Sequence, DateTime CapturedAtUtc, double CallElapsedMs);

public sealed class CallTranscriptWindow
{
    private const int MaxWindowWords = 30;

    // Raised from 50 -- there's no durable per-call transcript store, so this ring buffer is the
    // only record admin replay (GetSnapshot) has; 50 was losing a long call's earliest lines.
    private const int MaxRecentLines = 1000;

    private readonly object _gate = new();
    private readonly List<string> _recentWords = [];
    private readonly List<TranscriptLine> _recentLines = [];
    private readonly HashSet<string> _emittedCanonicalValues = new(StringComparer.OrdinalIgnoreCase);

    public string CallId { get; }

    /// <summary>When this window was created, i.e. when the first transcript segment for this
    /// call reached AIToolbox -- an approximation of the call's real start (RingCX's own dialog
    /// start precedes this by however long audio capture + transcription + relay took for the
    /// first segment), but the only "call started" signal this pipeline has.</summary>
    public DateTime StartedAtUtc { get; }

    public DateTime LastActivityUtc { get; private set; }

    /// <summary>Set once the Listener reports its gRPC stream closed -- null while the call is
    /// still active. Not used to reclaim memory (idle cleanup still does that); purely so the
    /// admin Live Call Monitor can show a call as ended without losing its transcript.</summary>
    public DateTime? EndedAtUtc { get; private set; }

    /// <summary>UtteranceId/side of the most recently appended segment -- a best-effort
    /// provenance tag for candidates extracted from the current window, not a precise per-word
    /// mapping (the window stitches multiple segments together and doesn't retain per-word
    /// utterance origin).</summary>
    public string LastUtteranceId { get; private set; } = string.Empty;
    public string LastSide { get; private set; } = string.Empty;

    /// <summary>Set once, from whichever segment first supplies a non-empty value -- an agentKey
    /// doesn't change mid-call, so later segments never overwrite it.</summary>
    public string AgentKey { get; private set; } = string.Empty;

    public CallTranscriptWindow(string callId)
    {
        CallId = callId;
        StartedAtUtc = DateTime.UtcNow;
        LastActivityUtc = StartedAtUtc;
    }

    /// <summary>Appends a final segment's text and returns the stitched window to extract from.</summary>
    public string AppendFinalSegment(string text, string utteranceId, string side, string agentKey, int sequence, DateTime capturedAtUtc)
    {
        lock (_gate)
        {
            LastActivityUtc = DateTime.UtcNow;
            LastUtteranceId = utteranceId;
            LastSide = side;

            if (AgentKey.Length == 0 && !string.IsNullOrWhiteSpace(agentKey))
            {
                AgentKey = agentKey;
            }

            // Clamped at zero -- the Listener and AIToolbox hosts' clocks aren't guaranteed
            // perfectly synchronized, and relay latency alone can put a very first line's
            // capturedAtUtc a few ms before this window's own StartedAtUtc (set when AIToolbox,
            // downstream, received that same first segment).
            var callElapsedMs = Math.Max(0, (capturedAtUtc - StartedAtUtc).TotalMilliseconds);
            _recentLines.Add(new TranscriptLine(utteranceId, side, text, sequence, capturedAtUtc, callElapsedMs));
            var lineOverflow = _recentLines.Count - MaxRecentLines;
            if (lineOverflow > 0)
            {
                _recentLines.RemoveRange(0, lineOverflow);
            }

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

    /// <summary>Marks this call ended. Idempotent -- a late duplicate end signal, or the idle
    /// sweep later finding an already-ended window, just no-ops on the second call.</summary>
    public void MarkEnded()
    {
        lock (_gate)
        {
            EndedAtUtc ??= DateTime.UtcNow;
        }
    }

    /// <summary>Snapshot of the most recent recognized lines, oldest first -- for admin replay only.</summary>
    public IReadOnlyList<TranscriptLine> RecentLines()
    {
        lock (_gate)
        {
            return _recentLines.ToList();
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

/// <summary>Snapshot of one currently-tracked call, for the admin Live Call Monitor's active-calls list.</summary>
public sealed record ActiveCallSummary(string CallId, string AgentKey, DateTime StartedAtUtc, DateTime LastActivityUtc, DateTime? EndedAtUtc);

public interface ICallTranscriptWindowStore
{
    CallTranscriptWindow GetOrCreate(string callId);

    /// <summary>Same as GetOrCreate(string), but also reports whether this call was previously
    /// untracked -- used to fire an "active call started" signal exactly once per call.</summary>
    CallTranscriptWindow GetOrCreate(string callId, out bool wasCreated);

    /// <summary>Marks a tracked call ended (see CallTranscriptWindow.MarkEnded) without removing
    /// it -- the window (and its transcript) stays available for admin replay until idle cleanup
    /// reclaims it. No-ops if the call isn't tracked (nothing to mark).</summary>
    void MarkEnded(string callId);

    /// <summary>Drops call state that's seen no activity for longer than <paramref name="idleThreshold"/>.
    /// Returns the dropped callIds (not just a count) so a caller can signal "call ended" per one.</summary>
    IReadOnlyList<string> RemoveIdle(TimeSpan idleThreshold);

    /// <summary>All calls currently tracked, most recently active first -- for the admin Live Call
    /// Monitor's active-calls list.</summary>
    IReadOnlyList<ActiveCallSummary> ListActive();
}

public sealed class CallTranscriptWindowStore : ICallTranscriptWindowStore
{
    private readonly ConcurrentDictionary<string, CallTranscriptWindow> _windows = new(StringComparer.OrdinalIgnoreCase);

    public CallTranscriptWindow GetOrCreate(string callId) => GetOrCreate(callId, out _);

    public CallTranscriptWindow GetOrCreate(string callId, out bool wasCreated)
    {
        var created = false;
        var window = _windows.GetOrAdd(callId, id =>
        {
            created = true;
            return new CallTranscriptWindow(id);
        });
        wasCreated = created;
        return window;
    }

    public void MarkEnded(string callId)
    {
        if (_windows.TryGetValue(callId, out var window))
        {
            window.MarkEnded();
        }
    }

    public IReadOnlyList<string> RemoveIdle(TimeSpan idleThreshold)
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

        return idleCallIds;
    }

    public IReadOnlyList<ActiveCallSummary> ListActive()
    {
        return _windows.Values
            .Select(w => new ActiveCallSummary(w.CallId, w.AgentKey, w.StartedAtUtc, w.LastActivityUtc, w.EndedAtUtc))
            .OrderByDescending(s => s.LastActivityUtc)
            .ToList();
    }
}
