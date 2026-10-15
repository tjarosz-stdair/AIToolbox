using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Evaluation;

namespace AIToolbox.Ductwork.Core.Review;

public sealed record SurfaceDecision(SurfacedAs Surface, string Reason);

/// <summary>
/// Instance setting (plan section 6.13): the precision each level asks a detector to demonstrate
/// before its detections go to the batch queue instead of being raised individually. Sensitive has no
/// target because it raises everything. The Normal and Insensitive values are placeholders and must
/// be replaced from measured calibration before any pilot.
/// </summary>
public sealed record ReviewLevelSettings
{
    public double NormalTargetPrecision { get; init; } = 0.95;
    public double InsensitiveTargetPrecision { get; init; } = 0.90;

    /// <summary>The most autonomy an administrator allows. Defaults to no cap.</summary>
    public ReviewLevel MaxAutonomy { get; init; } = ReviewLevel.Insensitive;

    public int MinCalibrationSupport { get; init; } = CalibrationReport.DefaultMinSupport;

    public double? TargetFor(ReviewLevel level) => level switch
    {
        ReviewLevel.Normal => NormalTargetPrecision,
        ReviewLevel.Insensitive => InsensitiveTargetPrecision,
        _ => null
    };
}

/// <summary>Latest calibration per detector family. A family with no entry is uncalibrated.</summary>
public sealed class CalibrationSet
{
    private readonly Dictionary<DetectorFamily, CalibrationReport> _byFamily;

    public CalibrationSet(IDictionary<DetectorFamily, CalibrationReport>? reports = null)
    {
        _byFamily = reports is null ? [] : new Dictionary<DetectorFamily, CalibrationReport>(reports);
    }

    public static CalibrationSet Empty { get; } = new();

    public CalibrationReport? For(DetectorFamily family) => _byFamily.GetValueOrDefault(family);
}

public static class ReviewLevelPolicy
{
    /// <summary>Clamps the user's chosen level to the administrator's cap. Order: Sensitive, Normal, Insensitive.</summary>
    public static ReviewLevel Effective(ReviewLevel requested, ReviewLevelSettings settings) =>
        (ReviewLevel)Math.Min((int)requested, (int)settings.MaxAutonomy);

    /// <summary>
    /// Decides whether one detection is raised to the user as a finding or goes to the batch queue.
    /// <paramref name="alwaysAsk"/> carries the hard stops that no level may suppress: ambiguity with
    /// no candidate, rule conflicts, reference disagreements, conflicted overrides, holds, and
    /// anything a user rule marks "always ask". Without calibration for the detector family, every
    /// level behaves as Sensitive.
    /// </summary>
    public static SurfaceDecision Decide(
        ReviewLevel requested,
        DetectionProposal detection,
        CalibrationSet calibration,
        ReviewLevelSettings settings,
        bool alwaysAsk = false)
    {
        if (alwaysAsk)
            return new(SurfacedAs.Finding, "Always asked: a hard stop that no review level suppresses.");

        var level = Effective(requested, settings);
        if (level == ReviewLevel.Sensitive)
            return new(SurfacedAs.Finding, "Sensitive review level raises every detection.");

        var report = calibration.For(detection.Family);
        if (report is null)
            return new(SurfacedAs.Finding, $"No calibration for {detection.Family}, so it is treated as Sensitive.");

        var target = settings.TargetFor(level)!.Value;
        var cutoff = report.CutoffForPrecision(target, settings.MinCalibrationSupport);
        if (cutoff is null)
            return new(SurfacedAs.Finding, $"{detection.Family} has not demonstrated {target:P0} precision, so it is treated as Sensitive.");

        return detection.Confidence >= cutoff.Value
            ? new(SurfacedAs.Batch, $"Confidence {detection.Confidence:0.00} is at or above the {level} cutoff {cutoff.Value:0.00}.")
            : new(SurfacedAs.Finding, $"Confidence {detection.Confidence:0.00} is below the {level} cutoff {cutoff.Value:0.00}.");
    }
}
