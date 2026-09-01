namespace AIToolbox.Web.Models;

public sealed class TranscriptSegmentRequest
{
    public string CallId { get; set; } = string.Empty;
    public string UtteranceId { get; set; } = string.Empty;
    public int Sequence { get; set; }
    public string Speaker { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public bool IsFinal { get; set; }
    public double? Confidence { get; set; }
    public DateTime CapturedAtUtc { get; set; }
}

/// <summary>
/// "model_or_item" covers both Carrier PIC model numbers and general P21 item ids/supplier part
/// numbers — the regex extractor can't tell those apart from shape alone, only P21 validation can.
/// "serial" is a distinct guess based on nearby context words ("serial", "serial number"), never
/// corpus-matched (see the fast-matching design notes) since P21's serial history only covers its
/// own ~10-year retention window.
/// </summary>
public enum IdentifierTypeGuess
{
    ModelOrItem,
    Serial
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
    public required string Speaker { get; init; }
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
