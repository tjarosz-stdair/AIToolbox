using System.Globalization;
using System.Text;

namespace AIToolbox.Ductwork.Core.Evaluation;

/// <summary>Renders an evaluation as Markdown for the estimator and the plan: what matched, what did not, and how far confidence can be trusted.</summary>
public static class ReportFormatter
{
    public static readonly double[] DefaultTargets = [0.90, 0.95, 0.98];

    public static string Markdown(
        string title,
        ScoreReport score,
        CalibrationReport calibration,
        IReadOnlyList<double>? targetPrecisions = null,
        int minSupport = CalibrationReport.DefaultMinSupport,
        string? note = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}").AppendLine();
        if (note is not null) sb.AppendLine(note).AppendLine();

        sb.AppendLine($"Match mode: **{score.Mode}**. Detections scored: **{score.Scored.Count}**.").AppendLine();
        sb.AppendLine("## Overall").AppendLine();
        AppendTable(sb, "", [score.Overall with { BuildingType = "all", Type = "all" }], showBuilding: true, showType: true, showSize: false);

        sb.AppendLine("## By building type").AppendLine();
        AppendTable(sb, "", score.ByBuildingType, showBuilding: true, showType: false, showSize: false);

        sb.AppendLine("## By type").AppendLine();
        AppendTable(sb, "", score.ByType, showBuilding: false, showType: true, showSize: false);

        var differences = score.ByKey.Where(r => r.FalsePositives > 0 || r.FalseNegatives > 0).ToList();
        sb.AppendLine("## Disagreements to adjudicate").AppendLine();
        if (differences.Count == 0)
        {
            sb.AppendLine("None. Every detection matched the reference.").AppendLine();
        }
        else
        {
            sb.AppendLine("Each row is a building type, type and size where the detector and the reference differ. A difference is not automatically a detector error: the reference (for example a worksheet) can be wrong, or a convention may apply.").AppendLine();
            AppendTable(sb, "", differences, showBuilding: true, showType: true, showSize: true);
        }

        sb.AppendLine("## Confidence calibration").AppendLine();
        AppendCalibration(sb, calibration, targetPrecisions ?? DefaultTargets, minSupport);
        return sb.ToString();
    }

    private static void AppendTable(StringBuilder sb, string _, IReadOnlyList<ScoreRow> rows, bool showBuilding, bool showType, bool showSize)
    {
        var header = new List<string>();
        if (showBuilding) header.Add("Building type");
        if (showType) header.Add("Type");
        if (showSize) header.Add("Size");
        header.AddRange(["Matched", "Extra (FP)", "Missed (FN)", "Precision", "Recall"]);
        sb.AppendLine("| " + string.Join(" | ", header) + " |");
        sb.AppendLine("|" + string.Concat(header.Select(_ => "---|")));

        foreach (var r in rows)
        {
            var cells = new List<string>();
            if (showBuilding) cells.Add(r.BuildingType ?? "(none)");
            if (showType) cells.Add(r.Type ?? "(none)");
            if (showSize) cells.Add(r.Size ?? "(none)");
            cells.Add(N(r.TruePositives));
            cells.Add(N(r.FalsePositives));
            cells.Add(N(r.FalseNegatives));
            cells.Add(Pct(r.Precision));
            cells.Add(Pct(r.Recall));
            sb.AppendLine("| " + string.Join(" | ", cells) + " |");
        }

        sb.AppendLine();
    }

    private static void AppendCalibration(StringBuilder sb, CalibrationReport calibration, IReadOnlyList<double> targets, int minSupport)
    {
        if (calibration.Total == 0)
        {
            sb.AppendLine("No detections, so nothing can be calibrated.").AppendLine();
            return;
        }

        sb.AppendLine("| Confidence | Detections | Correct | Measured precision |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var bin in calibration.Bins.Where(b => b.Predicted > 0))
            sb.AppendLine($"| {bin.Lower:0.0} to {bin.Upper:0.0} | {bin.Predicted} | {bin.Correct:0.#} | {Pct(bin.Precision)} |");
        sb.AppendLine();

        var ece = calibration.ExpectedCalibrationError;
        sb.AppendLine($"Expected calibration error: **{(ece is null ? "n/a" : (ece.Value * 100).ToString("0.0", CultureInfo.InvariantCulture) + " points")}** over {calibration.Total} detections.").AppendLine();

        sb.AppendLine("Lowest confidence cutoff that demonstrates a target precision, with at least " +
                      $"{minSupport} detections behind it:").AppendLine();
        sb.AppendLine("| Target precision | Cutoff | Detections at or above |");
        sb.AppendLine("|---|---|---|");
        foreach (var target in targets)
        {
            var cutoff = calibration.CutoffForPrecision(target, minSupport);
            var support = cutoff is null ? "-" : calibration.CountAtOrAbove(cutoff.Value).ToString(CultureInfo.InvariantCulture);
            sb.AppendLine($"| {target:P0} | {(cutoff is null ? "not demonstrated" : cutoff.Value.ToString("0.00", CultureInfo.InvariantCulture))} | {support} |");
        }

        sb.AppendLine();
        if (calibration.Total < minSupport * 4)
            sb.AppendLine($"**Caution:** only {calibration.Total} detections. Cutoffs from so few cannot be trusted to a fine precision, and no review level should rely on them until more gold data exists.").AppendLine();
    }

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Pct(double? v) => v is null ? "n/a" : (v.Value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
}
