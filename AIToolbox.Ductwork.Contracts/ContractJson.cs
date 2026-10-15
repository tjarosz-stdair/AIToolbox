using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AIToolbox.Ductwork.Contracts;

/// <summary>
/// The one set of JSON options for the wire contract. The generated JSON Schema, the validator that
/// checks model and worker output, and the serializer all use these, so what the schema promises is
/// what the code accepts. Numbers are strict (no numeric strings), unknown members are refused, and
/// enums are strings.
/// </summary>
public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public static JsonSerializerOptions IndentedOptions { get; } = new(Options) { WriteIndented = true };

    public static JsonSchemaExporterOptions SchemaOptions { get; } = new()
    {
        // A property declared non-nullable must not accept null in the schema.
        TreatNullObliviousAsNonNullable = true
    };

    public static System.Text.Json.Nodes.JsonNode GenerateSchema(Type type) =>
        Options.GetJsonSchemaAsNode(type, SchemaOptions);
}
