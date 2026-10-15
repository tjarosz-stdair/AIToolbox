using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Evaluation;

namespace AIToolbox.Ductwork.Tests;

public class ReportTests
{
    private static DetectionProposal Det(string type, string size, string building, double confidence = 0.9) => new()
    {
        DetectionId = Guid.NewGuid().ToString("N"), Type = type, Size = size, BuildingType = building, Quantity = 1,
        Confidence = confidence, Method = DetectionMethod.Text, Family = DetectorFamily.LabelFlagCount
    };

    [Fact]
    public void Score_exposes_one_row_per_building_type_type_and_size()
    {
        var gold = new[]
        {
            new GoldItem { BuildingType = "A", Type = "Boot", Size = "4x10x7", Quantity = 3 },
            new GoldItem { BuildingType = "A", Type = "Boot", Size = "4x12x7", Quantity = 1 }
        };
        var predicted = new[] { Det("Boot", "4x10x7", "A"), Det("Boot", "4x10x7", "A"), Det("Boot", "4x12x7", "A"), Det("Boot", "4x12x7", "A") };

        var rows = Scoring.Score(gold, predicted, MatchMode.Count).ByKey;

        var small = rows.Single(r => r.Size == "4x10x7");
        Assert.Equal((2, 0, 1), (small.TruePositives, small.FalsePositives, small.FalseNegatives));
        var big = rows.Single(r => r.Size == "4x12x7");
        Assert.Equal((1, 1, 0), (big.TruePositives, big.FalsePositives, big.FalseNegatives));
    }

    [Fact]
    public void The_report_lists_disagreements_and_says_so_when_there_are_none()
    {
        var gold = new[] { new GoldItem { BuildingType = "A", Type = "Boot", Size = "4x10x7", Quantity = 2 } };

        var different = EvalHarness.Evaluate(gold, [Det("Boot", "4x10x7", "A")], MatchMode.Count);
        var md = ReportFormatter.Markdown("Run", different.Score, different.Calibration);
        Assert.Contains("## Disagreements to adjudicate", md);
        Assert.Contains("| a | boot | 4x10x7 | 1 | 0 | 1 |", md);

        var same = EvalHarness.Evaluate(gold, [Det("Boot", "4x10x7", "A"), Det("Boot", "4x10x7", "A")], MatchMode.Count);
        Assert.Contains("None. Every detection matched", ReportFormatter.Markdown("Run", same.Score, same.Calibration));
    }

    [Fact]
    public void The_report_says_when_calibration_cannot_be_trusted()
    {
        var gold = new[] { new GoldItem { BuildingType = "A", Type = "Boot", Size = "4x10x7", Quantity = 5 } };
        var outcome = EvalHarness.Evaluate(gold, Enumerable.Range(0, 5).Select(_ => Det("Boot", "4x10x7", "A")).ToList(), MatchMode.Count);

        var md = ReportFormatter.Markdown("Run", outcome.Score, outcome.Calibration);

        Assert.Contains("not demonstrated", md);   // 5 detections cannot demonstrate any precision at the default support
        Assert.Contains("**Caution:** only 5 detections", md);
    }

    [Fact]
    public void The_report_reports_a_cutoff_when_there_is_enough_evidence()
    {
        var gold = new[] { new GoldItem { BuildingType = "A", Type = "Boot", Size = "4x10x7", Quantity = 400 } };
        var detections = Enumerable.Range(0, 400).Select(i => Det("Boot", "4x10x7", "A", confidence: 0.9)).ToList();

        var outcome = EvalHarness.Evaluate(gold, detections, MatchMode.Count);
        var md = ReportFormatter.Markdown("Run", outcome.Score, outcome.Calibration);

        Assert.Contains("| 90% | 0.90 | 400 |", md);
        Assert.DoesNotContain("**Caution:**", md);
    }

    [Fact]
    public void Count_at_or_above_matches_the_cutoff_semantics()
    {
        var report = new CalibrationReport([new ScoredDetection(0.5, 1), new ScoredDetection(0.7, 1), new ScoredDetection(0.9, 0)]);
        Assert.Equal(2, report.CountAtOrAbove(0.7));
        Assert.Equal(3, report.CountAtOrAbove(0));
        Assert.Equal(0, report.CountAtOrAbove(0.95));
    }
}
