namespace AIToolbox.Web.Models;

/// <summary>
/// Wire shape for the Hub -&gt; AIToolbox transcript relay leg
/// (POST /api/v1/call-transcripts/segments). UtteranceId is deliberately not a wire field -- the
/// Hub/Listener only ever have SegmentId + a per-segment Sequence counter; AIToolbox derives its
/// own internal utterance identity from those two ("{SegmentId}:{Sequence}") rather than requiring
/// a third, redundant identifier to be kept in sync across three codebases.
/// </summary>
public sealed class TranscriptSegmentRequest
{
    public string CallId { get; set; } = string.Empty;
    public string AgentKey { get; set; } = string.Empty;
    public string SegmentId { get; set; } = string.Empty;
    public int Sequence { get; set; }

    /// <summary>Which side of the call this line came from -- exactly "Agent" or "External",
    /// never a free-text speaker name. Computed once at the source (RingCXStreamListener's
    /// MapParticipantTypeToSide) and carried through unchanged.</summary>
    public string Side { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public long OffsetMs { get; set; }
    public bool IsFinal { get; set; }
    public double? Confidence { get; set; }
    public DateTime CapturedAtUtc { get; set; }
}

/// <summary>
/// "model_or_part" covers both Carrier PIC model numbers and general P21 item ids/supplier part
/// numbers — the regex extractor can't tell those apart from shape alone, only P21 validation can
/// (see ValidationResult.DetectedEntityType). "serial" is a distinct guess based on nearby context
/// words ("serial", "serial number"), never corpus-matched (see the fast-matching design notes)
/// since P21's serial history only covers its own ~10-year retention window.
/// </summary>
public enum IdentifierTypeGuess
{
    ModelOrPart,
    Serial
}

/// <summary>The concrete entity type a candidate resolved to, known only after P21 validation
/// (see ValidationResult.DetectedEntityType) -- drives both the outbound EntityType wire value and
/// which actions IEntityActionCatalog offers (Part Number never gets "Open in PIC Search").</summary>
public enum DetectedEntityType
{
    ModelNumber,
    SerialNumber,
    PartNumber
}

public sealed class IdentifierCandidate
{
    public required string CanonicalValue { get; init; }
    public required string RawSpokenText { get; init; }
    public required IdentifierTypeGuess TypeGuess { get; init; }
    public required string ContextSnippet { get; init; }
    public required double Confidence { get; init; }

    /// <summary>RawSpokenText's position within ContextSnippet -- lets a consumer highlight the
    /// matched span inside the surrounding text without re-searching for it.</summary>
    public required int ContextOffsetStart { get; init; }
    public required int ContextOffsetLength { get; init; }
}

/// <summary>
/// "NotFoundInWindow" is serial-number-specific: a shape-valid serial with no P21 record may
/// simply predate P21's own ~10-year data retention rather than being wrong -- see the parent
/// plan's fast-matching design notes. It is not the same confidence class as an outright miss.
/// </summary>
public enum ValidationStatus
{
    Validated,
    Ambiguous,
    NotFoundInWindow
}

public sealed class ValidationResult
{
    public required ValidationStatus Status { get; init; }
    public required IReadOnlyList<string> CandidateValues { get; init; }
    public required string Source { get; init; }

    /// <summary>Set by P21ValidationResultMapper -- the only place that can actually distinguish
    /// a Model Number from a Part Number (both start out as the same IdentifierTypeGuess.ModelOrPart
    /// guess). Always SerialNumber for a warranty-lookup result.</summary>
    public required DetectedEntityType DetectedEntityType { get; init; }
}

/// <summary>One action offered on a detected entity, e.g. "Search Carrier PIC for this model".
/// Sourced from IEntityActionCatalog -- currently a static placeholder, see that interface's doc.</summary>
public sealed class ActionDescriptor
{
    public required string ActionId { get; init; }
    public required string Label { get; init; }
    public required bool RequiresConfirmation { get; init; }
}

/// <summary>
/// Outbound payload to the Call Data API's entity-detected endpoint. Field names and shape match
/// AIToolbox/TODO_Lists/call-data-api-contract.md -- keep both in sync if either changes.
/// </summary>
public sealed class EntityDetectedNotification
{
    public required string CallId { get; init; }
    public required string EntityId { get; init; }
    public required string UtteranceId { get; init; }
    public required DateTime DetectedAtUtc { get; init; }
    public required string EntityType { get; init; }
    public required string CanonicalValue { get; init; }
    public required string RawSpokenText { get; init; }
    public required double Confidence { get; init; }
    public required EntityDetectedValidation Validation { get; init; }
    public required EntityDetectedSourceUtterance SourceUtterance { get; init; }
    public required IReadOnlyList<ActionDescriptor> Actions { get; init; }
}

public sealed class EntityDetectedValidation
{
    public required string Status { get; init; }
    public required IReadOnlyList<string> CandidateValues { get; init; }
    public required string Source { get; init; }
}

public sealed class EntityDetectedSourceUtterance
{
    public required string Text { get; init; }
    public required int OffsetStart { get; init; }
    public required int OffsetEnd { get; init; }

    /// <summary>Exactly "Agent" or "External" -- see TranscriptSegmentRequest.Side.</summary>
    public required string Side { get; init; }
}

/// <summary>Inbound payload from the Call Data API's disposition webhook. See the same contract doc.</summary>
public sealed class CallEventDispositionRequest
{
    public string EntityId { get; set; } = string.Empty;
    public string CallId { get; set; } = string.Empty;
    public string Disposition { get; set; } = string.Empty;
    public string? SelectedActionId { get; set; }
    public DateTime RespondedAtUtc { get; set; }
}
