using AIToolbox.Ductwork.Contracts;

namespace AIToolbox.Ductwork.Core.Evaluation;

/// <summary>A human-verified correct answer (plan section 4.5).</summary>
public sealed record GoldItem
{
    public string? BuildingType { get; init; }
    public required string Type { get; init; }
    public string? Category { get; init; }
    public string? Size { get; init; }
    public double Quantity { get; init; } = 1;
    public BoundingBox? Box { get; init; }
}

public sealed record ScoreRow(string? BuildingType, string? Type, double TruePositives, double FalsePositives, double FalseNegatives)
{
    public double? Precision => TruePositives + FalsePositives == 0 ? null : TruePositives / (TruePositives + FalsePositives);
    public double? Recall => TruePositives + FalseNegatives == 0 ? null : TruePositives / (TruePositives + FalseNegatives);

    public double? F1
    {
        get
        {
            if (Precision is not { } p || Recall is not { } r || p + r == 0) return null;
            return 2 * p * r / (p + r);
        }
    }
}

/// <summary>One prediction with whether (or how much of it) was correct, used to calibrate confidence.</summary>
public sealed record ScoredDetection(double Confidence, double Correct);

public sealed record ScoreReport(
    ScoreRow Overall,
    IReadOnlyList<ScoreRow> ByType,
    IReadOnlyList<ScoreRow> ByBuildingType,
    IReadOnlyList<ScoredDetection> Scored,
    MatchMode Mode);

public enum MatchMode { Count, Box }

/// <summary>
/// Scores detections against gold labels. Two modes, because the detector families differ:
/// <see cref="MatchMode.Count"/> compares quantities per (building type, type, size) and needs no
/// locations (the residential label counter, whose reference is a worksheet), and
/// <see cref="MatchMode.Box"/> matches individual items by overlap (geometry and symbol detectors).
/// Quantities are never summed across units.
/// </summary>
public static class Scoring
{
    public const double DefaultIouThreshold = 0.3;

    public static ScoreReport Score(
        IReadOnlyList<GoldItem> gold,
        IReadOnlyList<DetectionProposal> predicted,
        MatchMode mode,
        double iouThreshold = DefaultIouThreshold)
    {
        var rows = new Dictionary<Key, Tally>();
        var scored = new List<ScoredDetection>();

        if (mode == MatchMode.Count) ScoreCount(gold, predicted, rows, scored);
        else ScoreBox(gold, predicted, iouThreshold, rows, scored);

        var all = rows.Values.ToList();
        var overall = new ScoreRow(null, null, all.Sum(t => t.Tp), all.Sum(t => t.Fp), all.Sum(t => t.Fn));

        return new ScoreReport(
            overall,
            Group(rows, k => (null, k.Type)),
            Group(rows, k => (k.BuildingType, null)),
            scored,
            mode);
    }

    private static void ScoreCount(
        IReadOnlyList<GoldItem> gold,
        IReadOnlyList<DetectionProposal> predicted,
        Dictionary<Key, Tally> rows,
        List<ScoredDetection> scored)
    {
        var goldByKey = gold.GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));
        var predByKey = predicted.GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var key in goldByKey.Keys.Union(predByKey.Keys))
        {
            var g = goldByKey.GetValueOrDefault(key);
            var detections = predByKey.GetValueOrDefault(key) ?? [];
            var p = detections.Sum(d => d.Quantity);
            var tp = Math.Min(p, g);

            RowFor(rows, key).Add(tp, Math.Max(0, p - g), Math.Max(0, g - p));

            // Which detections were right is not knowable from counts, so each carries the key's
            // correct fraction. This is unbiased within a key and does not flatter the detector.
            var fraction = p == 0 ? 0 : tp / p;
            foreach (var d in detections)
                scored.Add(new ScoredDetection(d.Confidence, fraction));
        }
    }

    private static void ScoreBox(
        IReadOnlyList<GoldItem> gold,
        IReadOnlyList<DetectionProposal> predicted,
        double iouThreshold,
        Dictionary<Key, Tally> rows,
        List<ScoredDetection> scored)
    {
        var goldByKey = gold.GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.ToList());
        var predByKey = predicted.GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var key in goldByKey.Keys.Union(predByKey.Keys))
        {
            var golds = goldByKey.GetValueOrDefault(key) ?? [];
            var preds = predByKey.GetValueOrDefault(key) ?? [];
            var usedGold = new bool[golds.Count];
            var usedPred = new bool[preds.Count];

            var pairs = new List<(double Iou, int G, int P)>();
            for (var gi = 0; gi < golds.Count; gi++)
            {
                if (golds[gi].Box is not { } gb) continue;
                for (var pi = 0; pi < preds.Count; pi++)
                {
                    if (preds[pi].Box is not { } pb) continue;
                    var iou = gb.IntersectionOverUnion(pb);
                    if (iou >= iouThreshold) pairs.Add((iou, gi, pi));
                }
            }

            // Greedy by overlap, best first. Each gold and each prediction matches at most once.
            foreach (var (_, gi, pi) in pairs.OrderByDescending(x => x.Iou))
            {
                if (usedGold[gi] || usedPred[pi]) continue;
                usedGold[gi] = true;
                usedPred[pi] = true;
            }

            var tp = usedPred.Count(u => u);
            RowFor(rows, key).Add(tp, preds.Count - tp, golds.Count - usedGold.Count(u => u));

            for (var pi = 0; pi < preds.Count; pi++)
                scored.Add(new ScoredDetection(preds[pi].Confidence, usedPred[pi] ? 1 : 0));
        }
    }

    private static List<ScoreRow> Group(Dictionary<Key, Tally> rows, Func<Key, (string? Bt, string? Type)> selector) =>
        rows.GroupBy(r => selector(r.Key))
            .Select(g => new ScoreRow(g.Key.Bt, g.Key.Type, g.Sum(x => x.Value.Tp), g.Sum(x => x.Value.Fp), g.Sum(x => x.Value.Fn)))
            .OrderBy(r => r.BuildingType).ThenBy(r => r.Type)
            .ToList();

    private static Key KeyOf(GoldItem i) => new(Norm(i.BuildingType), Norm(i.Type), Norm(i.Size));
    private static Key KeyOf(DetectionProposal d) => new(Norm(d.BuildingType), Norm(d.Type), Norm(d.Size));
    private static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToLowerInvariant();

    private static Tally RowFor(Dictionary<Key, Tally> rows, Key key)
    {
        if (!rows.TryGetValue(key, out var t)) rows[key] = t = new Tally();
        return t;
    }

    private readonly record struct Key(string? BuildingType, string? Type, string? Size);

    private sealed class Tally
    {
        public double Tp { get; private set; }
        public double Fp { get; private set; }
        public double Fn { get; private set; }

        public void Add(double tp, double fp, double fn)
        {
            Tp += tp;
            Fp += fp;
            Fn += fn;
        }
    }
}
