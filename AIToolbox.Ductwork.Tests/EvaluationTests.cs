using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Evaluation;
using AIToolbox.Ductwork.Core.Review;

namespace AIToolbox.Ductwork.Tests;

public class EvaluationTests
{
    private static DetectionProposal Det(string type, string? size = null, string? building = null, double qty = 1,
        double confidence = 0.9, BoundingBox? box = null, DetectorFamily family = DetectorFamily.LabelFlagCount) => new()
    {
        DetectionId = Guid.NewGuid().ToString("N"),
        Type = type,
        Size = size,
        BuildingType = building,
        Quantity = qty,
        Confidence = confidence,
        Method = DetectionMethod.Text,
        Family = family,
        Box = box
    };

    private static GoldItem Gold(string type, string? size = null, string? building = null, double qty = 1, BoundingBox? box = null) =>
        new() { Type = type, Size = size, BuildingType = building, Quantity = qty, Box = box };

    [Fact]
    public void Count_mode_scores_quantities_per_key_without_summing_across_keys()
    {
        var gold = new[] { Gold("Boot", "4x10", "Type 1", 6), Gold("Boot", "4x12", "Type 1", 2) };
        var pred = new[] { Det("Boot", "4x10", "Type 1", 5), Det("Boot", "4x12", "Type 1", 3) };

        var report = Scoring.Score(gold, pred, MatchMode.Count);

        // 4x10: tp 5, fn 1. 4x12: tp 2, fp 1. A surplus in one size never offsets a shortfall in another.
        Assert.Equal(7, report.Overall.TruePositives);
        Assert.Equal(1, report.Overall.FalsePositives);
        Assert.Equal(1, report.Overall.FalseNegatives);
        Assert.Equal(7.0 / 8, report.Overall.Precision!.Value, 9);
    }

    [Fact]
    public void Count_mode_reports_by_building_type()
    {
        var gold = new[] { Gold("Boot", null, "A", 4), Gold("Boot", null, "B", 4) };
        var pred = new[] { Det("Boot", null, "A", 4), Det("Boot", null, "B", 2) };

        var report = Scoring.Score(gold, pred, MatchMode.Count);

        var a = report.ByBuildingType.Single(r => r.BuildingType == "a");
        var b = report.ByBuildingType.Single(r => r.BuildingType == "b");
        Assert.Equal(1.0, a.Recall);
        Assert.Equal(0.5, b.Recall);
    }

    [Fact]
    public void Box_mode_matches_each_item_at_most_once()
    {
        var g1 = new BoundingBox(0, 0, 10, 10);
        var g2 = new BoundingBox(100, 100, 110, 110);
        var gold = new[] { Gold("Elbow", box: g1), Gold("Elbow", box: g2) };
        var pred = new[]
        {
            Det("Elbow", box: new BoundingBox(1, 1, 10, 10)),      // matches g1
            Det("Elbow", box: new BoundingBox(0, 0, 9, 9)),        // also overlaps g1, but g1 is taken
            Det("Elbow", box: new BoundingBox(500, 500, 510, 510)) // matches nothing
        };

        var report = Scoring.Score(gold, pred, MatchMode.Box);

        Assert.Equal(1, report.Overall.TruePositives);
        Assert.Equal(2, report.Overall.FalsePositives);
        Assert.Equal(1, report.Overall.FalseNegatives);
        Assert.Equal(1, report.Scored.Count(s => s.Correct == 1));
    }

    [Fact]
    public void Box_mode_does_not_match_across_types()
    {
        var box = new BoundingBox(0, 0, 10, 10);
        var report = Scoring.Score([Gold("Elbow", box: box)], [Det("Tee", box: box)], MatchMode.Box);

        Assert.Equal(0, report.Overall.TruePositives);
        Assert.Equal(1, report.Overall.FalsePositives);
        Assert.Equal(1, report.Overall.FalseNegatives);
    }

    [Fact]
    public void Empty_inputs_give_undefined_rather_than_zero_ratios()
    {
        var report = Scoring.Score([], [], MatchMode.Count);
        Assert.Null(report.Overall.Precision);
        Assert.Null(report.Overall.Recall);
        Assert.Null(report.Overall.F1);
    }

    [Fact]
    public void Count_mode_calibration_carries_the_correct_fraction_per_key()
    {
        var gold = new[] { Gold("Boot", null, null, 2) };
        var pred = new[] { Det("Boot", confidence: 0.9), Det("Boot", confidence: 0.8), Det("Boot", confidence: 0.7), Det("Boot", confidence: 0.6) };

        var report = Scoring.Score(gold, pred, MatchMode.Count);

        Assert.All(report.Scored, s => Assert.Equal(0.5, s.Correct, 9));
    }

    // ---- Calibration

    [Fact]
    public void Calibration_bins_hold_counts_and_precision()
    {
        var scored = new List<ScoredDetection>();
        scored.AddRange(Enumerable.Repeat(new ScoredDetection(0.95, 1), 9));
        scored.Add(new ScoredDetection(0.95, 0));
        scored.AddRange(Enumerable.Repeat(new ScoredDetection(0.15, 0), 4));

        var report = new CalibrationReport(scored);

        var top = report.Bins[9];
        Assert.Equal(10, top.Predicted);
        Assert.Equal(0.9, top.Precision!.Value, 9);
        Assert.Equal(0.0, report.Bins[1].Precision!.Value);
        Assert.Null(report.Bins[5].Precision);
        Assert.Equal(14, report.Total);
    }

    [Fact]
    public void Confidence_of_exactly_one_lands_in_the_last_bin()
    {
        var report = new CalibrationReport([new ScoredDetection(1.0, 1)]);
        Assert.Equal(1, report.Bins[9].Predicted);
    }

    [Fact]
    public void Cutoff_requires_the_target_precision_and_minimum_support()
    {
        // 100 detections: confidence 0.9 are right 98 of 100 times, confidence 0.5 are right half the time.
        var scored = new List<ScoredDetection>();
        for (var i = 0; i < 100; i++) scored.Add(new ScoredDetection(0.9, i < 98 ? 1 : 0));
        for (var i = 0; i < 100; i++) scored.Add(new ScoredDetection(0.5, i < 50 ? 1 : 0));
        var report = new CalibrationReport(scored);

        Assert.Equal(0.9, report.CutoffForPrecision(0.95, minSupport: 30));
        Assert.Equal(0.5, report.CutoffForPrecision(0.70, minSupport: 30));
        Assert.Null(report.CutoffForPrecision(0.999, minSupport: 30));
        // Not enough evidence behind the claim at all.
        Assert.Null(report.CutoffForPrecision(0.95, minSupport: 500));
    }

    [Fact]
    public void Expected_calibration_error_is_zero_for_a_perfectly_calibrated_detector()
    {
        var scored = new List<ScoredDetection>();
        for (var i = 0; i < 100; i++) scored.Add(new ScoredDetection(0.95, i < 95 ? 1 : 0));
        var report = new CalibrationReport(scored);

        Assert.Equal(0, report.ExpectedCalibrationError!.Value, 9);
    }

    // ---- Review level

    private static CalibrationSet Calibrated(double precisionAtTop = 0.98)
    {
        var scored = new List<ScoredDetection>();
        for (var i = 0; i < 200; i++) scored.Add(new ScoredDetection(0.9, i < 200 * precisionAtTop ? 1 : 0));
        for (var i = 0; i < 100; i++) scored.Add(new ScoredDetection(0.4, i < 40 ? 1 : 0));
        return new CalibrationSet(new Dictionary<DetectorFamily, CalibrationReport>
        {
            [DetectorFamily.LabelFlagCount] = new CalibrationReport(scored)
        });
    }

    [Fact]
    public void Sensitive_raises_everything()
    {
        var d = ReviewLevelPolicy.Decide(ReviewLevel.Sensitive, Det("Boot", confidence: 0.99), Calibrated(), new ReviewLevelSettings());
        Assert.Equal(SurfacedAs.Finding, d.Surface);
    }

    [Fact]
    public void Without_calibration_every_level_behaves_as_sensitive()
    {
        foreach (var level in Enum.GetValues<ReviewLevel>())
        {
            var d = ReviewLevelPolicy.Decide(level, Det("Boot", confidence: 0.99), CalibrationSet.Empty, new ReviewLevelSettings());
            Assert.Equal(SurfacedAs.Finding, d.Surface);
        }
    }

    [Fact]
    public void Insensitive_batches_confident_detections_and_raises_unconfident_ones()
    {
        var settings = new ReviewLevelSettings();
        var calibration = Calibrated();

        Assert.Equal(SurfacedAs.Batch, ReviewLevelPolicy.Decide(ReviewLevel.Insensitive, Det("Boot", confidence: 0.9), calibration, settings).Surface);
        Assert.Equal(SurfacedAs.Finding, ReviewLevelPolicy.Decide(ReviewLevel.Insensitive, Det("Boot", confidence: 0.4), calibration, settings).Surface);
    }

    [Fact]
    public void A_detector_that_never_reaches_the_target_is_treated_as_sensitive()
    {
        var d = ReviewLevelPolicy.Decide(ReviewLevel.Normal, Det("Boot", confidence: 0.9), Calibrated(precisionAtTop: 0.6), new ReviewLevelSettings());
        Assert.Equal(SurfacedAs.Finding, d.Surface);
    }

    [Fact]
    public void Hard_stops_are_never_suppressed_at_any_level()
    {
        foreach (var level in Enum.GetValues<ReviewLevel>())
        {
            var d = ReviewLevelPolicy.Decide(level, Det("Boot", confidence: 1.0), Calibrated(), new ReviewLevelSettings(), alwaysAsk: true);
            Assert.Equal(SurfacedAs.Finding, d.Surface);
        }
    }

    [Fact]
    public void Administrator_cap_limits_autonomy()
    {
        var capped = new ReviewLevelSettings { MaxAutonomy = ReviewLevel.Normal };
        Assert.Equal(ReviewLevel.Normal, ReviewLevelPolicy.Effective(ReviewLevel.Insensitive, capped));
        Assert.Equal(ReviewLevel.Sensitive, ReviewLevelPolicy.Effective(ReviewLevel.Sensitive, capped));

        var sensitiveOnly = new ReviewLevelSettings { MaxAutonomy = ReviewLevel.Sensitive };
        var d = ReviewLevelPolicy.Decide(ReviewLevel.Insensitive, Det("Boot", confidence: 0.99), Calibrated(), sensitiveOnly);
        Assert.Equal(SurfacedAs.Finding, d.Surface);
    }
}
