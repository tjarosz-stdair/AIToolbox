using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Data;
using AIToolbox.Ductwork.Core.Evaluation;
using AIToolbox.Ductwork.Core.Review;
using AIToolbox.Ductwork.Tests.Fixtures;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Tests;

public class EvalHarnessTests(SqlFixture db) : IClassFixture<SqlFixture>
{
    private sealed class FixedDetector(IReadOnlyList<DetectionProposal> detections, string name = "fixture-detector") : IDetector
    {
        public int Calls { get; private set; }
        public ProducerRef Producer { get; } = ProducerRef.Algorithm(name, "0.1", "p");
        public DetectorFamily Family => DetectorFamily.LabelFlagCount;
        public string TaskKey => "detect-fixture";

        public Task<IReadOnlyList<DetectionProposal>> DetectAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(detections);
        }
    }

    private static IReadOnlyList<DetectionProposal> Perfect(IReadOnlyList<GoldItem> gold) =>
        gold.Select((g, i) => FixtureGenerator.Make(g, 0.95, $"p{i}")).ToList();

    private async Task<int> StoreGoldAsync(string name, GoldRole role, IReadOnlyList<GoldItem> gold)
    {
        await using var connection = await db.OpenAsync();
        return await EvalHarness.CreateGoldSetAsync(connection, name, role, gold);
    }

    // ---- Pure scoring on the generated fixtures

    [Fact]
    public void A_perfect_detector_scores_one_on_every_fixture_in_both_modes()
    {
        foreach (var outlined in new[] { false, true })
        {
            var gold = FixtureGenerator.ResidentialLabels(outlinedText: outlined).AllGold;
            foreach (var mode in new[] { MatchMode.Count, MatchMode.Box })
            {
                var outcome = EvalHarness.Evaluate(gold, Perfect(gold), mode);
                Assert.Equal(1.0, outcome.Score.Overall.Precision);
                Assert.Equal(1.0, outcome.Score.Overall.Recall);
            }
        }
    }

    [Fact]
    public void Scoring_reports_each_building_type_separately()
    {
        var gold = FixtureGenerator.ResidentialLabels().AllGold;
        var predicted = Perfect(gold.Where(g => g.BuildingType != "Type 3").ToList());

        var report = EvalHarness.Evaluate(gold, predicted, MatchMode.Count).Score;

        Assert.Equal(0.0, report.ByBuildingType.Single(r => r.BuildingType == "type 3").Recall);
        Assert.Equal(1.0, report.ByBuildingType.Single(r => r.BuildingType == "type 1").Recall);
        Assert.True(report.Overall.Recall < 1.0);
    }

    [Fact]
    public void A_noisy_detector_with_informative_confidence_calibrates_and_the_review_level_uses_it()
    {
        // Enough items for the calibration to have support, so build a large set from several seeds.
        var gold = Enumerable.Range(1, 12).SelectMany(s => FixtureGenerator.ResidentialLabels(seed: s).AllGold).ToList();
        var predicted = FixtureGenerator.NoisyDetections(gold, seed: 3, missRate: 0.08, falsePositiveRate: 0.30);

        var outcome = EvalHarness.Evaluate(gold, predicted, MatchMode.Box);

        Assert.InRange(outcome.Score.Overall.Precision!.Value, 0.6, 0.95);
        Assert.InRange(outcome.Score.Overall.Recall!.Value, 0.85, 0.97);

        // Confident detections are mostly right and unconfident ones mostly wrong.
        var cutoff = outcome.Calibration.CutoffForPrecision(0.95, minSupport: 30);
        Assert.NotNull(cutoff);

        var calibration = new CalibrationSet(new Dictionary<DetectorFamily, CalibrationReport>
        {
            [DetectorFamily.LabelFlagCount] = outcome.Calibration
        });
        var settings = new ReviewLevelSettings();

        var confident = FixtureGenerator.Make(gold[0], 0.97, "c");
        var doubtful = FixtureGenerator.Make(gold[0], 0.30, "d");
        Assert.Equal(SurfacedAs.Batch, ReviewLevelPolicy.Decide(ReviewLevel.Normal, confident, calibration, settings).Surface);
        Assert.Equal(SurfacedAs.Finding, ReviewLevelPolicy.Decide(ReviewLevel.Normal, doubtful, calibration, settings).Surface);
        Assert.Equal(SurfacedAs.Finding, ReviewLevelPolicy.Decide(ReviewLevel.Sensitive, confident, calibration, settings).Surface);
    }

    // ---- Persistence

    [Fact]
    public async Task Gold_labels_round_trip_through_the_database()
    {
        var gold = FixtureGenerator.ResidentialLabels(seed: 4).AllGold;
        var id = await StoreGoldAsync("round-trip-" + Guid.NewGuid().ToString("N"), GoldRole.Development, gold);

        await using var connection = await db.OpenAsync();
        var loaded = await EvalHarness.LoadGoldAsync(connection, id);

        Assert.Equal(gold.Count, loaded.Count);
        Assert.Equal(gold[0].Box, loaded[0].Box);
        Assert.Equal(gold.Sum(g => g.Quantity), loaded.Sum(g => g.Quantity));
    }

    [Fact]
    public async Task A_run_is_saved_with_results_calibration_and_a_reused_producer()
    {
        var gold = FixtureGenerator.ResidentialLabels(seed: 5).AllGold;
        var id = await StoreGoldAsync("run-" + Guid.NewGuid().ToString("N"), GoldRole.Development, gold);
        var detector = new FixedDetector(Perfect(gold), "reuse-" + Guid.NewGuid().ToString("N"));

        await using var connection = await db.OpenAsync();
        var (run1, outcome) = await EvalHarness.RunAndSaveAsync(connection, id, detector, MatchMode.Box);
        var (run2, _) = await EvalHarness.RunAndSaveAsync(connection, id, detector, MatchMode.Box);

        Assert.Equal(1.0, outcome.Score.Overall.Recall);
        Assert.NotEqual(run1, run2);
        Assert.Equal(2, await db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.EvalRun WHERE GoldSetId = @g;", ("@g", id)));
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(DISTINCT ProducerId) FROM ductwork.EvalRun WHERE GoldSetId = @g;", ("@g", id)));
        Assert.True(await db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.EvalResult WHERE EvalRunId = @r;", ("@r", run1)) >= 1 + 1 + 4);
        Assert.Equal(CalibrationReport.DefaultBins, await db.ScalarAsync<int>(
            "SELECT COUNT(*) FROM ductwork.CalibrationBin b JOIN ductwork.CalibrationRun c ON c.CalibrationRunId = b.CalibrationRunId WHERE c.EvalRunId = @r;", ("@r", run1)));
        Assert.Equal(1.0, await db.ScalarAsync<double>(
            "SELECT RecallRatio FROM ductwork.EvalResult WHERE EvalRunId = @r AND BuildingType IS NULL AND Type IS NULL;", ("@r", run1)));
    }

    [Fact]
    public async Task The_holdout_is_spent_by_one_run_and_the_detector_never_runs_again()
    {
        var gold = FixtureGenerator.ResidentialLabels(seed: 6).AllGold;
        var id = await StoreGoldAsync("holdout-" + Guid.NewGuid().ToString("N"), GoldRole.Holdout, gold);
        var detector = new FixedDetector(Perfect(gold), "holdout-" + Guid.NewGuid().ToString("N"));

        await using var connection = await db.OpenAsync();
        await EvalHarness.RunAndSaveAsync(connection, id, detector, MatchMode.Count);
        Assert.Equal(1, detector.Calls);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => EvalHarness.RunAndSaveAsync(connection, id, detector, MatchMode.Count));
        Assert.Contains("holdout", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, detector.Calls);
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.EvalRun WHERE GoldSetId = @g;", ("@g", id)));
    }

    [Fact]
    public async Task The_database_refuses_a_second_holdout_run_even_if_the_application_check_is_bypassed()
    {
        var gold = FixtureGenerator.ResidentialLabels(seed: 9).AllGold;
        var id = await StoreGoldAsync("holdout-bypass-" + Guid.NewGuid().ToString("N"), GoldRole.Holdout, gold);
        var detector = new FixedDetector(Perfect(gold), "bypass-" + Guid.NewGuid().ToString("N"));

        await using var connection = await db.OpenAsync();
        await EvalHarness.RunAndSaveAsync(connection, id, detector, MatchMode.Count);

        var producerId = await db.ScalarAsync<long>("SELECT TOP 1 ProducerId FROM ductwork.EvalRun WHERE GoldSetId = @g;", ("@g", id));
        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync(
            "INSERT INTO ductwork.EvalRun (GoldSetId, ProducerId, TaskKey, MatchMode, ContractVersion) VALUES (@g, @p, 'x', 'Count', '0.1.0');",
            ("@g", id), ("@p", producerId)));
    }

    // ---- Producer store

    [Fact]
    public async Task Identical_producers_share_one_row_even_when_created_concurrently()
    {
        var producer = ProducerRef.Algorithm("concurrent-" + Guid.NewGuid().ToString("N"), "1.0", "abc");

        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var connection = await db.OpenAsync();
            return await ProducerStore.EnsureProducerAsync(connection, producer);
        }));

        Assert.Single(ids.Distinct());
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.Producer WHERE ProducerKey = @k;", ("@k", producer.Key())));
    }

    [Fact]
    public async Task Provider_snapshots_are_deduplicated_by_content_and_an_invalid_producer_is_refused()
    {
        var info = new ProviderSnapshotInfo(2, "Example.Invalid", "model-x", "2025-01-01", 4096, true, "foundry");
        await using var connection = await db.OpenAsync();

        var a = await ProducerStore.EnsureProviderSnapshotAsync(connection, info);
        var b = await ProducerStore.EnsureProviderSnapshotAsync(connection, info with { EndpointHost = "example.invalid" });
        var c = await ProducerStore.EnsureProviderSnapshotAsync(connection, info with { ModelName = "model-y" });

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);

        var llm = ProducerRef.Llm(directiveSnapshotId: 7, providerSnapshotId: a);
        var id1 = await ProducerStore.EnsureProducerAsync(connection, llm);
        var id2 = await ProducerStore.EnsureProducerAsync(connection, llm);
        Assert.Equal(id1, id2);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProducerStore.EnsureProducerAsync(connection, new ProducerRef { Kind = ProducerKind.Llm }));
    }
}
