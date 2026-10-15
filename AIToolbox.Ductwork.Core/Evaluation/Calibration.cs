namespace AIToolbox.Ductwork.Core.Evaluation;

public sealed record CalibrationBinResult(int Index, double Lower, double Upper, int Predicted, double Correct)
{
    public double? Precision => Predicted == 0 ? null : Correct / Predicted;
}

/// <summary>
/// How well a detector's confidence predicts correctness (plan section 6.13). Built from scored
/// detections, and used to turn a target precision into a confidence cutoff.
/// </summary>
public sealed class CalibrationReport
{
    public const int DefaultBins = 10;
    public const int DefaultMinSupport = 30;

    private readonly IReadOnlyList<ScoredDetection> _scored;

    public CalibrationReport(IEnumerable<ScoredDetection> scored, int binCount = DefaultBins)
    {
        if (binCount < 1) throw new ArgumentOutOfRangeException(nameof(binCount));
        _scored = scored.ToList();
        Bins = BuildBins(_scored, binCount);
    }

    public IReadOnlyList<CalibrationBinResult> Bins { get; }

    public int Total => _scored.Count;

    /// <summary>
    /// Expected calibration error: the support-weighted gap between stated confidence (the bin
    /// midpoint) and measured precision. Zero means perfectly calibrated.
    /// </summary>
    public double? ExpectedCalibrationError
    {
        get
        {
            if (Total == 0) return null;
            return Bins.Where(b => b.Predicted > 0)
                .Sum(b => b.Predicted * Math.Abs(b.Precision!.Value - (b.Lower + b.Upper) / 2)) / Total;
        }
    }

    /// <summary>
    /// The lowest confidence cutoff at which the detections at or above it reach
    /// <paramref name="targetPrecision"/>, with at least <paramref name="minSupport"/> detections
    /// behind the claim. Null when no cutoff qualifies, in which case the caller must treat the
    /// detector as uncalibrated for that target.
    /// </summary>
    public double? CutoffForPrecision(double targetPrecision, int minSupport = DefaultMinSupport)
    {
        // Evaluate at each distinct confidence, lowest first, and keep the first that qualifies.
        foreach (var cutoff in _scored.Select(s => s.Confidence).Distinct().OrderBy(c => c))
        {
            var above = _scored.Where(s => s.Confidence >= cutoff).ToList();
            if (above.Count < minSupport) break;
            if (above.Sum(s => s.Correct) / above.Count >= targetPrecision) return cutoff;
        }

        return null;
    }

    /// <summary>How many detections have confidence at or above the cutoff.</summary>
    public int CountAtOrAbove(double cutoff) => _scored.Count(s => s.Confidence >= cutoff);

    private static List<CalibrationBinResult> BuildBins(IReadOnlyList<ScoredDetection> scored, int binCount)
    {
        var predicted = new int[binCount];
        var correct = new double[binCount];
        foreach (var s in scored)
        {
            var c = Math.Clamp(s.Confidence, 0, 1);
            var index = Math.Min((int)(c * binCount), binCount - 1);
            predicted[index]++;
            correct[index] += Math.Clamp(s.Correct, 0, 1);
        }

        return Enumerable.Range(0, binCount)
            .Select(i => new CalibrationBinResult(i, (double)i / binCount, (double)(i + 1) / binCount, predicted[i], correct[i]))
            .ToList();
    }
}
