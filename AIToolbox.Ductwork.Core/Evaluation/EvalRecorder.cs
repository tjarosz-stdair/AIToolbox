using System.Text.Json;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Llm;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Evaluation;

/// <summary>A refusal to record an evaluation, with the reason in words.</summary>
public sealed class EvalRecordingException(string message) : Exception(message);

/// <summary>One evaluation that was recorded: what was measured, and how it scored.</summary>
public sealed record RecordedEvaluation(int EvalRunId, int GoldSetId, ProducerRef Producer, ScoreRow Overall, int Predictions);

/// <summary>
/// Records an evaluation run in the database from files (plan section 4.5): checked answers go in as a gold set, a detector's result
/// manifests are scored against them, and the run is stored against the producer that made them. A task type can be enabled only for a
/// producer that has such a run, so this is the step that makes a detector or reader available to switch on.
///
/// The manifests are held to the same rules as a result submitted by a worker: valid against the contract schema, and acceptable to
/// the manifest rules. They must all name one producer, because a run measures exactly one.
/// </summary>
public static class EvalRecorder
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<GoldItem> ReadGold(string json)
    {
        try { return JsonSerializer.Deserialize<List<GoldItem>>(json, Web) ?? []; }
        catch (JsonException ex) { throw new EvalRecordingException($"The gold file is not a list of gold items: {ex.Message}"); }
    }

    /// <summary>Parses and checks result manifests, and returns them with the one producer they all name.</summary>
    public static (IReadOnlyList<TaskResultManifest> Manifests, ProducerRef Producer) ReadManifests(IEnumerable<(string Name, string Json)> files)
    {
        var validator = StructuredOutput.ValidatorFor(typeof(TaskResultManifest));
        var manifests = new List<TaskResultManifest>();
        var problems = new List<string>();
        foreach (var (name, json) in files)
        {
            var parsed = StructuredOutput.Parse<TaskResultManifest>(json, validator);
            if (!parsed.IsValid)
            {
                problems.AddRange(parsed.Errors.Take(5).Select(e => $"{name}: {e}"));
                continue;
            }

            var rules = ManifestRules.Validate(parsed.Value!);
            if (rules.Count > 0) problems.AddRange(rules.Take(5).Select(e => $"{name}: {e}"));
            else manifests.Add(parsed.Value!);
        }

        if (problems.Count > 0) throw new EvalRecordingException("Some result files are not acceptable:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Take(15)));
        if (manifests.Count == 0) throw new EvalRecordingException("There are no result files to score.");

        var producers = manifests.Select(m => m.Producer).DistinctBy(p => p.Key()).ToList();
        if (producers.Count > 1)
            throw new EvalRecordingException("The result files name more than one producer (" + string.Join(", ", producers.Select(p => p.Key())) + "). A run measures exactly one, so record them separately.");
        return (manifests, producers[0]);
    }

    public static async Task<int> ImportGoldAsync(SqlConnection connection, string name, GoldRole role, IReadOnlyList<GoldItem> items, string? notes, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new EvalRecordingException("A gold set needs a name.");
        if (items.Count == 0) throw new EvalRecordingException("A gold set needs at least one item.");
        if (await FindGoldAsync(connection, name, cancellationToken) is { } existing)
            throw new EvalRecordingException($"A gold set named “{name}” already exists (id {existing}). Gold sets are never replaced, because runs refer to them; give a new one a new name.");
        return await EvalHarness.CreateGoldSetAsync(connection, name.Trim(), role, items, cancellationToken, notes);
    }

    public static async Task<int?> FindGoldAsync(SqlConnection connection, string name, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT GoldSetId FROM ductwork.GoldSet WHERE Name = @n;";
        command.Parameters.AddWithValue("@n", name.Trim());
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : (int)result;
    }

    /// <summary>Scores the manifests against a stored gold set and records the run. A Holdout gold set is spent by this and refused afterwards.</summary>
    public static async Task<RecordedEvaluation> RecordAsync(
        SqlConnection connection, int goldSetId, IReadOnlyList<TaskResultManifest> manifests, string taskKey, DetectorFamily family, MatchMode mode, string? notes,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(taskKey) || taskKey.Length > 60) throw new EvalRecordingException("A task key is 1 to 60 characters.");
        if (manifests.Count == 0) throw new EvalRecordingException("There are no result files to score.");
        var producer = manifests[0].Producer;
        if (manifests.Any(m => m.Producer.Key() != producer.Key())) throw new EvalRecordingException("The results name more than one producer. A run measures exactly one.");

        var detector = new FixedDetector(producer, family, taskKey, manifests.SelectMany(m => m.Detections).ToList());
        var (runId, outcome) = await EvalHarness.RunAndSaveAsync(connection, goldSetId, detector, mode, cancellationToken, notes);
        return new RecordedEvaluation(runId, goldSetId, producer, outcome.Score.Overall, detector.Count);
    }

    private sealed class FixedDetector(ProducerRef producer, DetectorFamily family, string taskKey, IReadOnlyList<DetectionProposal> detections) : IDetector
    {
        public ProducerRef Producer => producer;
        public DetectorFamily Family => family;
        public string TaskKey => taskKey;
        public int Count => detections.Count;
        public Task<IReadOnlyList<DetectionProposal>> DetectAsync(CancellationToken cancellationToken) => Task.FromResult(detections);
    }
}
