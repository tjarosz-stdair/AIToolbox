using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using AIToolbox.Ductwork.Contracts;

namespace AIToolbox.Ductwork.Tests;

/// <summary>
/// The wire contract is C# types. The JSON Schema committed under
/// AIToolbox.Ductwork.Contracts/schemas is generated from them so a non-.NET worker (the Python
/// sidecar) can validate against it. A change to a contract type without regenerating the schema
/// fails here. Regenerate with DUCTWORK_UPDATE_SCHEMAS=1 and commit the result.
/// </summary>
public class SchemaDriftTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly (string File, Type Type)[] Schemas =
    [
        ("task-result-manifest.schema.json", typeof(TaskResultManifest)),
        ("heartbeat-sample.schema.json", typeof(HeartbeatSample))
    ];

    [Fact]
    public void Committed_schemas_match_the_contract_types()
    {
        var dir = FindSchemasDirectory();
        var update = Environment.GetEnvironmentVariable("DUCTWORK_UPDATE_SCHEMAS") == "1";

        foreach (var (file, type) in Schemas)
        {
            var generated = Normalize(JsonSchemaExporter.GetJsonSchemaAsNode(Options, type).ToJsonString(Options));
            var path = Path.Combine(dir, file);

            if (update)
            {
                File.WriteAllText(path, generated);
                continue;
            }

            Assert.True(File.Exists(path), $"Missing schema {file}. Run with DUCTWORK_UPDATE_SCHEMAS=1 to generate it.");
            Assert.Equal(Normalize(File.ReadAllText(path)), generated);
        }
    }

    [Fact]
    public void Every_contract_enum_serializes_as_a_string()
    {
        var enums = typeof(ContractInfo).Assembly.GetTypes().Where(t => t.IsEnum && t.IsPublic);
        foreach (var e in enums)
        {
            var first = Enum.GetValues(e).GetValue(0)!;
            var json = JsonSerializer.Serialize(first, e, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.StartsWith("\"", json);
        }
    }

    [Fact]
    public void Manifest_round_trips()
    {
        var manifest = new TaskResultManifest
        {
            ContractVersion = ContractInfo.Version,
            TaskType = "detect-residential",
            TaskPublicId = "t-1",
            Producer = ProducerRef.Algorithm("residential-counter", "0.1", "abc"),
            Detections =
            [
                new DetectionProposal
                {
                    DetectionId = "d1", Type = "Boot", Size = "4x10x7", Quantity = 1, Confidence = 0.93,
                    Method = DetectionMethod.FlagNote, Family = DetectorFamily.LabelFlagCount,
                    Box = new BoundingBox(1, 2, 3, 4)
                }
            ],
            Findings = [new FindingProposal { FindingId = "f1", Kind = FindingKind.AmbiguousLabel, Question = "?", Candidates = ["a", "b"] }]
        };

        var json = JsonSerializer.Serialize(manifest, Options);
        var back = JsonSerializer.Deserialize<TaskResultManifest>(json, Options)!;

        Assert.Equal(manifest.Producer, back.Producer);
        Assert.Equal(manifest.Detections[0].Box, back.Detections[0].Box);
        Assert.Equal(DetectionMethod.FlagNote, back.Detections[0].Method);
        Assert.Contains("\"FlagNote\"", json);
    }

    [Fact]
    public void Worker_with_a_different_major_version_is_detectable()
    {
        Assert.Equal(0, ContractInfo.Major);
    }

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(Options).Replace("\r\n", "\n").TrimEnd() + "\n";

    private static string FindSchemasDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "AIToolbox.Ductwork.Contracts");
            if (Directory.Exists(candidate))
            {
                var schemas = Path.Combine(candidate, "schemas");
                Directory.CreateDirectory(schemas);
                return schemas;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find AIToolbox.Ductwork.Contracts above the test output directory.");
    }
}
