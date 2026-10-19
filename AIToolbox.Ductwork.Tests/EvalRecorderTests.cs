using System.Text.Json;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Data;
using AIToolbox.Ductwork.Core.Evaluation;
using AIToolbox.Ductwork.Core.Tasks;
using AIToolbox.Ductwork.Tests.Fixtures;

namespace AIToolbox.Ductwork.Tests;

/// <summary>Recording an evaluation run from files: what lets an administrator switch a task type on for a detector.</summary>
public class EvalRecorderTests(SqlFixture db) : IClassFixture<SqlFixture>
{
    private static readonly AuditContext Admin =
        AuditContext.ForUser("admin@example.com", displayName: "admin", sessionId: "s-admin", clientIp: "10.0.0.5", userAgent: "tests", isAdmin: true);

    private static string Unique(string stem) => stem + "-" + Guid.NewGuid().ToString("N")[..8];

    private static string ManifestJson(ProducerRef producer, IEnumerable<DetectionProposal> detections) =>
        JsonSerializer.Serialize(new TaskResultManifest
        {
            ContractVersion = ContractInfo.Version, TaskType = "detect-fixture", TaskPublicId = Guid.NewGuid().ToString("D"), Producer = producer,
            Detections = detections.ToList(), Findings = []
        }, ContractJson.Options);

    private static IReadOnlyList<GoldItem> Gold(bool withBuilding) =>
    [
        new() { BuildingType = withBuilding ? "Plan A" : null, Type = "Fan", Size = "EF-1", Quantity = 2 },
        new() { BuildingType = withBuilding ? "Plan A" : null, Type = "Grille", Size = "12x24 RA", Quantity = 3 }
    ];

    [Fact]
    public async Task A_gold_set_is_stored_once_under_its_name_and_never_replaced()
    {
        var name = Unique("gold");
        await using var connection = await db.OpenAsync();

        var id = await EvalRecorder.ImportGoldAsync(connection, name, GoldRole.Development, Gold(true), "from a test");

        Assert.Equal(id, await EvalRecorder.FindGoldAsync(connection, name));
        Assert.Null(await EvalRecorder.FindGoldAsync(connection, Unique("absent")));
        await Assert.ThrowsAsync<EvalRecordingException>(() => EvalRecorder.ImportGoldAsync(connection, name, GoldRole.Development, Gold(true), null));
        await Assert.ThrowsAsync<EvalRecordingException>(() => EvalRecorder.ImportGoldAsync(connection, Unique("empty"), GoldRole.Development, [], null));
    }

    [Fact]
    public async Task Recording_scores_the_results_and_stores_one_overall_row_even_when_the_items_have_no_building_type()
    {
        var name = Unique("gold");
        var producer = ProducerRef.Algorithm(Unique("rec"), "1.0", "h");
        var gold = Gold(withBuilding: false);
        await using var connection = await db.OpenAsync();
        var goldId = await EvalRecorder.ImportGoldAsync(connection, name, GoldRole.Development, gold, null);
        // One expected item is missed and one that is not in the gold is extra.
        var detections = new[] { FixtureGenerator.Make(gold[0], 0.9, "a"), FixtureGenerator.Make(new GoldItem { Type = "Boot", Size = "4x10x7", Quantity = 1 }, 0.6, "b") };
        var (manifests, found) = EvalRecorder.ReadManifests([("one.json", ManifestJson(producer, detections))]);

        var run = await EvalRecorder.RecordAsync(connection, goldId, manifests, "detect-fixture", DetectorFamily.LabelFlagCount, MatchMode.Count, "a note");

        Assert.Equal(producer.Key(), found.Key());
        Assert.Equal(2, run.Predictions);
        Assert.Equal((2.0, 1.0, 3.0), (run.Overall.TruePositives, run.Overall.FalsePositives, run.Overall.FalseNegatives));
        // The overall row is the one with neither a building nor a type, and there must be exactly one for a run to show a single headline figure.
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.EvalResult WHERE EvalRunId = @r AND BuildingType IS NULL AND Type IS NULL;", ("@r", run.EvalRunId)));
        Assert.Equal("a note", await db.ScalarAsync<string>("SELECT Notes FROM ductwork.EvalRun WHERE EvalRunId = @r;", ("@r", run.EvalRunId)));
    }

    [Fact]
    public async Task A_recorded_run_is_what_lets_a_task_type_be_enabled_for_that_producer()
    {
        var producer = ProducerRef.Algorithm(Unique("bind"), "1.0", "h");
        var gold = Gold(true);
        await using var connection = await db.OpenAsync();
        var goldId = await EvalRecorder.ImportGoldAsync(connection, Unique("gold"), GoldRole.Regression, gold, null);
        var (manifests, _) = EvalRecorder.ReadManifests([("one.json", ManifestJson(producer, gold.Select((g, i) => FixtureGenerator.Make(g, 0.9, $"p{i}"))))]);
        var run = await EvalRecorder.RecordAsync(connection, goldId, manifests, "detect-fixture", DetectorFamily.LabelFlagCount, MatchMode.Count, null);
        var bindings = new TaskBindingRepository(db.ConnectionString);

        var producerId = await db.ScalarAsync<long>("SELECT ProducerId FROM ductwork.EvalRun WHERE EvalRunId = @r;", ("@r", run.EvalRunId));
        var binding = await bindings.BindAsync(Unique("type"), producerId, run.EvalRunId, false, 3, Admin);

        Assert.Equal((producerId, run.EvalRunId), (binding.ProducerId, binding.EvalRunId));
        Assert.Contains(await bindings.ListBindableProducersAsync(), p => p.ProducerId == producerId);
    }

    [Fact]
    public void Results_that_name_more_than_one_producer_or_break_the_contract_are_refused()
    {
        var detection = FixtureGenerator.Make(new GoldItem { Type = "Fan", Size = "EF-1", Quantity = 1 }, 0.9, "a");
        var one = ManifestJson(ProducerRef.Algorithm("first", "1.0", "h"), [detection]);
        var two = ManifestJson(ProducerRef.Algorithm("second", "1.0", "h"), [detection]);

        var mixed = Assert.Throws<EvalRecordingException>(() => EvalRecorder.ReadManifests([("one.json", one), ("two.json", two)]));
        var broken = Assert.Throws<EvalRecordingException>(() => EvalRecorder.ReadManifests([("broken.json", "{\"detections\": 5}")]));
        var none = Assert.Throws<EvalRecordingException>(() => EvalRecorder.ReadManifests([]));

        Assert.Contains("more than one producer", mixed.Message);
        Assert.Contains("broken.json", broken.Message);
        Assert.Contains("no result files", none.Message);
    }

    [Fact]
    public void A_gold_file_that_is_not_a_list_of_items_is_refused_in_words()
    {
        Assert.Throws<EvalRecordingException>(() => EvalRecorder.ReadGold("{\"type\": \"Fan\"}"));
        Assert.Equal(2, EvalRecorder.ReadGold("[{\"type\":\"Fan\",\"size\":\"EF-1\",\"quantity\":2},{\"type\":\"Grille\",\"size\":\"x\",\"quantity\":1}]").Count);
    }
}
