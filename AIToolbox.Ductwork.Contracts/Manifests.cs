using System.Text.Json.Serialization;

namespace AIToolbox.Ductwork.Contracts;

/// <summary>Axis-aligned box in canonical sheet space (plan section 6.10): PDF user units, origin top-left, after rotation.</summary>
public sealed record BoundingBox(double X0, double Y0, double X1, double Y1)
{
    [JsonIgnore]
    public double Width => Math.Max(0, X1 - X0);
    [JsonIgnore]
    public double Height => Math.Max(0, Y1 - Y0);
    [JsonIgnore]
    public double Area => Width * Height;

    public double IntersectionOverUnion(BoundingBox other)
    {
        var ix0 = Math.Max(X0, other.X0);
        var iy0 = Math.Max(Y0, other.Y0);
        var ix1 = Math.Min(X1, other.X1);
        var iy1 = Math.Min(Y1, other.Y1);
        var inter = Math.Max(0, ix1 - ix0) * Math.Max(0, iy1 - iy0);
        var union = Area + other.Area - inter;
        return union <= 0 ? 0 : inter / union;
    }
}

/// <summary>One thing a detector claims to have found. Always a proposal until a human verifies it.</summary>
public sealed record DetectionProposal
{
    public required string DetectionId { get; init; }
    public required string Type { get; init; }
    public string? Category { get; init; }
    public string? Tag { get; init; }
    public string? Size { get; init; }
    public required double Quantity { get; init; }
    public QuantityUnit Unit { get; init; } = QuantityUnit.EA;
    public required DetectionMethod Method { get; init; }
    public required DetectorFamily Family { get; init; }
    /// <summary>Detector confidence in [0,1]. Only meaningful for the review level once calibrated.</summary>
    public required double Confidence { get; init; }
    public string? BuildingType { get; init; }
    public string? SheetViewRef { get; init; }
    public BoundingBox? Box { get; init; }
    public string? Evidence { get; init; }
}

/// <summary>A question the detector cannot resolve for the user (plan section 6.11).</summary>
public sealed record FindingProposal
{
    public required string FindingId { get; init; }
    public required FindingKind Kind { get; init; }
    public required string Question { get; init; }
    public IReadOnlyList<string> Candidates { get; init; } = [];
    public string? DetectionId { get; init; }
    public string? Evidence { get; init; }
}

/// <summary>What a worker returns for one processing task. Validated by the web app, the single write path.</summary>
public sealed record TaskResultManifest
{
    public required string ContractVersion { get; init; }
    public required string TaskType { get; init; }
    public required string TaskPublicId { get; init; }
    public required ProducerRef Producer { get; init; }
    public IReadOnlyList<DetectionProposal> Detections { get; init; } = [];
    public IReadOnlyList<FindingProposal> Findings { get; init; } = [];
}

/// <summary>One viewer heartbeat (plan section 10.1). The client reports a state, never a duration.</summary>
public sealed record HeartbeatSample
{
    public required string SessionId { get; init; }
    public required ActivityState State { get; init; }
    /// <summary>Server receive time. Set by the server, never trusted from the client.</summary>
    public DateTimeOffset ReceivedUtc { get; init; }
}
