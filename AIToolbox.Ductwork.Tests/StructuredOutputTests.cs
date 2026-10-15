using System.Text.Json;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Llm;

namespace AIToolbox.Ductwork.Tests;

public class StructuredOutputTests
{
    private static TaskResultManifest Valid() => new()
    {
        ContractVersion = ContractInfo.Version,
        TaskType = "schedule-extract",
        TaskPublicId = "t-1",
        Producer = ProducerRef.Llm(3, 4),
        Detections =
        [
            new DetectionProposal
            {
                DetectionId = "d1", Type = "Boot", Size = "4x10x7", Quantity = 1, Confidence = 0.9,
                Method = DetectionMethod.Vision, Family = DetectorFamily.OcrVision, Box = new BoundingBox(1, 2, 3, 4)
            }
        ],
        Findings = [new FindingProposal { FindingId = "f1", Kind = FindingKind.AmbiguousLabel, Question = "Which size?", Candidates = ["a"], DetectionId = "d1" }]
    };

    private static string Json(TaskResultManifest m) => JsonSerializer.Serialize(m, ContractJson.Options);

    // ---- Extraction

    [Theory]
    [InlineData("{\"a\":1}", "{\"a\":1}")]
    [InlineData("Here you go:\n```json\n{\"a\":1}\n```\nHope that helps.", "{\"a\":1}")]
    [InlineData("[note] the result is {\"a\":[1,2]} done", "{\"a\":[1,2]}")]
    [InlineData("{\"text\":\"a } brace and a \\\" quote\"}", "{\"text\":\"a } brace and a \\\" quote\"}")]
    public void Json_is_extracted_from_surrounding_text(string input, string expected) =>
        Assert.Equal(expected, StructuredOutput.ExtractJson(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no json here")]
    [InlineData("{\"unterminated\": 1")]
    public void Text_without_complete_json_gives_null(string? input) =>
        Assert.Null(StructuredOutput.ExtractJson(input));

    // ---- Validation through the contract schema

    [Fact]
    public void A_valid_manifest_round_trips_through_extraction_and_validation()
    {
        var text = "Sure!\n```json\n" + Json(Valid()) + "\n```";
        var result = StructuredOutput.Parse<TaskResultManifest>(text);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal("d1", result.Value!.Detections[0].DetectionId);
        Assert.Equal(DetectionMethod.Vision, result.Value.Detections[0].Method);
    }

    [Fact]
    public void Missing_required_properties_are_all_reported()
    {
        var result = StructuredOutput.Parse<TaskResultManifest>("{\"taskType\":\"x\"}");

        Assert.False(result.IsValid);
        Assert.Null(result.Value);
        Assert.Contains(result.Errors, e => e.Contains("'contractVersion'"));
        Assert.Contains(result.Errors, e => e.Contains("'producer'"));
        Assert.Contains(result.Errors, e => e.Contains("'taskPublicId'"));
    }

    [Fact]
    public void Unknown_properties_are_refused()
    {
        var json = Json(Valid()).Replace("\"taskType\"", "\"extra\":true,\"taskType\"");
        var result = StructuredOutput.Parse<TaskResultManifest>(json);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("/extra") && e.Contains("not allowed"));
    }

    [Fact]
    public void Numbers_must_be_numbers_not_numeric_strings()
    {
        var json = Json(Valid()).Replace("\"confidence\":0.9", "\"confidence\":\"0.9\"");
        var result = StructuredOutput.Parse<TaskResultManifest>(json);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("/detections/0/confidence") && e.Contains("expected number"));
    }

    [Fact]
    public void Enum_values_must_be_known_names_not_numbers()
    {
        var json = Json(Valid()).Replace("\"Vision\"", "\"Telepathy\"");
        var result = StructuredOutput.Parse<TaskResultManifest>(json);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("/detections/0/method") && e.Contains("Telepathy"));

        var numeric = Json(Valid()).Replace("\"Vision\"", "5");
        Assert.False(StructuredOutput.Parse<TaskResultManifest>(numeric).IsValid);
    }

    [Fact]
    public void A_null_where_an_object_is_required_is_refused()
    {
        Assert.False(StructuredOutput.Parse<TaskResultManifest>("[1,2,3]").IsValid);
        var result = StructuredOutput.Parse<TaskResultManifest>(Json(Valid()).Replace("\"producer\":{", "\"producer\":null,\"x\":{"));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Nested_errors_carry_their_location()
    {
        var json = Json(Valid()).Replace("\"findingId\":\"f1\"", "\"findingId\":7");
        var result = StructuredOutput.Parse<TaskResultManifest>(json);

        Assert.Contains(result.Errors, e => e.Contains("/findings/0/findingId"));
    }

    [Fact]
    public void Malformed_json_is_reported_not_thrown()
    {
        var result = StructuredOutput.Parse<TaskResultManifest>("{\"a\": }");
        Assert.False(result.IsValid);
    }

    [Fact]
    public void An_oversized_response_is_refused_before_parsing()
    {
        var text = "{\"v\":\"" + new string('x', StructuredOutput.MaxJsonChars) + "\"}";
        var result = StructuredOutput.Parse<TaskResultManifest>(text);
        Assert.Contains(result.Errors, e => e.Contains("over the"));
    }

    // ---- The validator itself

    [Fact]
    public void A_schema_keyword_the_validator_does_not_implement_fails_loudly()
    {
        var ex = Assert.Throws<NotSupportedException>(() => SchemaValidator.FromJson("{\"oneOf\":[{\"type\":\"string\"}]}"));
        Assert.Contains("oneOf", ex.Message);
        Assert.Throws<NotSupportedException>(() => SchemaValidator.FromJson("{\"properties\":{\"a\":{\"$ref\":\"#/x\"}}}"));
    }

    [Fact]
    public void Every_contract_schema_is_fully_supported_by_the_validator()
    {
        foreach (var type in new[] { typeof(TaskResultManifest), typeof(HeartbeatSample), typeof(DetectionProposal), typeof(FindingProposal) })
            StructuredOutput.ValidatorFor(type); // throws if a contract change introduces an unsupported keyword
    }

    [Theory]
    [InlineData("{\"type\":\"integer\"}", "3", true)]
    [InlineData("{\"type\":\"integer\"}", "3.0", true)]
    [InlineData("{\"type\":\"integer\"}", "3.5", false)]
    [InlineData("{\"type\":[\"string\",\"null\"]}", "null", true)]
    [InlineData("{\"type\":[\"string\",\"null\"]}", "5", false)]
    [InlineData("{\"type\":\"number\",\"minimum\":0,\"maximum\":1}", "1.5", false)]
    [InlineData("{\"type\":\"array\",\"minItems\":1}", "[]", false)]
    [InlineData("{\"type\":\"string\",\"pattern\":\"^a+$\"}", "\"aaa\"", true)]
    [InlineData("{\"type\":\"string\",\"pattern\":\"^a+$\"}", "\"ab\"", false)]
    [InlineData("{\"type\":\"string\",\"format\":\"date-time\"}", "\"2026-10-15T09:00:00Z\"", true)]
    [InlineData("{\"type\":\"string\",\"format\":\"date-time\"}", "\"yesterday\"", false)]
    [InlineData("{\"enum\":[\"a\",1]}", "1", true)]
    [InlineData("{\"enum\":[\"a\",1]}", "\"b\"", false)]
    public void Validator_applies_the_supported_keywords(string schema, string instance, bool valid)
    {
        using var doc = JsonDocument.Parse(instance);
        Assert.Equal(valid, SchemaValidator.FromJson(schema).Validate(doc.RootElement).Count == 0);
    }

    // ---- Semantic rules the schema cannot express

    [Fact]
    public void A_clean_manifest_passes_the_semantic_rules()
    {
        Assert.Empty(ManifestRules.Validate(Valid()));
    }

    [Fact]
    public void Semantic_rules_catch_what_the_schema_cannot()
    {
        var m = Valid();
        var bad = m with
        {
            ContractVersion = "9.0.0",
            Producer = new ProducerRef { Kind = ProducerKind.Llm },
            Detections =
            [
                m.Detections[0] with { Confidence = 1.5 },
                m.Detections[0] with { Quantity = -1 },                     // duplicate id as well
                m.Detections[0] with { DetectionId = "d3", Box = new BoundingBox(5, 5, 1, 1) }
            ],
            Findings = [m.Findings[0] with { DetectionId = "nope" }]
        };

        var errors = ManifestRules.Validate(bad);

        Assert.Contains(errors, e => e.Contains("not compatible"));
        Assert.Contains(errors, e => e.Contains("directive snapshot"));
        Assert.Contains(errors, e => e.Contains("outside 0 to 1"));
        Assert.Contains(errors, e => e.Contains("more than once"));
        Assert.Contains(errors, e => e.Contains("quantity"));
        Assert.Contains(errors, e => e.Contains("inverted box"));
        Assert.Contains(errors, e => e.Contains("unknown detection"));
    }

    [Theory]
    [InlineData("0.1.0", true)]
    [InlineData("0.9.3", true)]
    [InlineData("1.0.0", false)]
    [InlineData("garbage", false)]
    [InlineData(null, false)]
    public void Contract_versions_are_compatible_within_a_major(string? version, bool compatible) =>
        Assert.Equal(compatible, ManifestRules.VersionCompatible(version));

    [Fact]
    public void Non_finite_numbers_are_refused()
    {
        var m = Valid();
        var errors = ManifestRules.Validate(m with { Detections = [m.Detections[0] with { Confidence = double.NaN }] });
        Assert.Contains(errors, e => e.Contains("outside 0 to 1"));
    }
}
