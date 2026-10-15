using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIToolbox.Ductwork.Core.Llm;

/// <summary>
/// A deliberately small JSON Schema validator for the subset the contract schemas use. It exists
/// instead of a third-party library because the widely used .NET validator is distributed under a
/// revenue-based maintenance-fee EULA, and a recurring fee is not something to adopt by accident.
///
/// It fails loudly rather than quietly: a schema that uses a keyword this validator does not
/// implement throws <see cref="NotSupportedException"/> when the validator is built, so a contract
/// change that introduces new schema features is caught by the tests instead of silently passing
/// everything.
/// </summary>
public sealed class SchemaValidator
{
    private static readonly HashSet<string> Annotations = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "title", "description", "default", "examples"
    };

    private static readonly HashSet<string> Implemented = new(StringComparer.Ordinal)
    {
        "type", "properties", "required", "items", "enum", "pattern", "format",
        "additionalProperties", "minimum", "maximum", "minItems", "maxItems"
    };

    private readonly JsonElement _schema;

    public SchemaValidator(JsonElement schema)
    {
        _schema = schema.Clone();
        EnsureSupported(_schema, "#");
    }

    public static SchemaValidator FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new SchemaValidator(document.RootElement);
    }

    public IReadOnlyList<string> Validate(JsonElement instance)
    {
        var errors = new List<string>();
        Check(_schema, instance, "", errors);
        return errors;
    }

    private static void EnsureSupported(JsonElement schema, string path)
    {
        if (schema.ValueKind == JsonValueKind.True || schema.ValueKind == JsonValueKind.False) return;
        if (schema.ValueKind != JsonValueKind.Object)
            throw new NotSupportedException($"Schema at {path} is not an object.");

        foreach (var property in schema.EnumerateObject())
        {
            if (Annotations.Contains(property.Name)) continue;
            if (!Implemented.Contains(property.Name))
                throw new NotSupportedException($"Schema keyword '{property.Name}' at {path} is not supported by the validator.");

            switch (property.Name)
            {
                case "properties":
                    foreach (var child in property.Value.EnumerateObject())
                        EnsureSupported(child.Value, $"{path}/properties/{child.Name}");
                    break;
                case "items":
                    EnsureSupported(property.Value, $"{path}/items");
                    break;
                case "additionalProperties" when property.Value.ValueKind == JsonValueKind.Object:
                    EnsureSupported(property.Value, $"{path}/additionalProperties");
                    break;
            }
        }
    }

    private static void Check(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        if (schema.ValueKind == JsonValueKind.True) return;
        if (schema.ValueKind == JsonValueKind.False)
        {
            errors.Add($"{Loc(path)}: no value is allowed here.");
            return;
        }

        if (schema.TryGetProperty("type", out var type) && !TypeMatches(type, value))
        {
            errors.Add($"{Loc(path)}: expected {DescribeType(type)} but found {Describe(value)}.");
            return; // Further keywords assume the type matched.
        }

        if (schema.TryGetProperty("enum", out var allowed) && !allowed.EnumerateArray().Any(a => Equal(a, value)))
            errors.Add($"{Loc(path)}: {Short(value)} is not one of {string.Join(", ", allowed.EnumerateArray().Select(Short))}.");

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                CheckObject(schema, value, path, errors);
                break;
            case JsonValueKind.Array:
                CheckArray(schema, value, path, errors);
                break;
            case JsonValueKind.String:
                CheckString(schema, value, path, errors);
                break;
            case JsonValueKind.Number:
                CheckNumber(schema, value, path, errors);
                break;
        }
    }

    private static void CheckObject(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        var declared = schema.TryGetProperty("properties", out var props) ? props : default;

        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray().Select(r => r.GetString()!))
            {
                if (!value.TryGetProperty(name, out _))
                    errors.Add($"{Loc(path)}: missing required property '{name}'.");
            }
        }

        foreach (var member in value.EnumerateObject())
        {
            var childPath = $"{path}/{member.Name}";
            if (declared.ValueKind == JsonValueKind.Object && declared.TryGetProperty(member.Name, out var childSchema))
            {
                Check(childSchema, member.Value, childPath, errors);
            }
            else if (schema.TryGetProperty("additionalProperties", out var additional))
            {
                if (additional.ValueKind == JsonValueKind.False)
                    errors.Add($"{Loc(childPath)}: property is not allowed.");
                else if (additional.ValueKind == JsonValueKind.Object)
                    Check(additional, member.Value, childPath, errors);
            }
        }
    }

    private static void CheckArray(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        var length = value.GetArrayLength();
        if (schema.TryGetProperty("minItems", out var min) && length < min.GetInt32())
            errors.Add($"{Loc(path)}: needs at least {min.GetInt32()} items but has {length}.");
        if (schema.TryGetProperty("maxItems", out var max) && length > max.GetInt32())
            errors.Add($"{Loc(path)}: allows at most {max.GetInt32()} items but has {length}.");

        if (!schema.TryGetProperty("items", out var items)) return;
        var index = 0;
        foreach (var item in value.EnumerateArray())
            Check(items, item, $"{path}/{index++}", errors);
    }

    private static void CheckString(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        var text = value.GetString()!;

        if (schema.TryGetProperty("pattern", out var pattern)
            && !Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            errors.Add($"{Loc(path)}: {Short(value)} does not match the required pattern.");

        if (schema.TryGetProperty("format", out var format) && format.GetString() == "date-time"
            && !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            errors.Add($"{Loc(path)}: {Short(value)} is not a date-time.");
    }

    private static void CheckNumber(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        var number = value.GetDouble();
        if (schema.TryGetProperty("minimum", out var min) && number < min.GetDouble())
            errors.Add($"{Loc(path)}: {number} is below the minimum {min.GetDouble()}.");
        if (schema.TryGetProperty("maximum", out var max) && number > max.GetDouble())
            errors.Add($"{Loc(path)}: {number} is above the maximum {max.GetDouble()}.");
    }

    private static bool TypeMatches(JsonElement type, JsonElement value) =>
        type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Any(t => TypeMatches(t.GetString()!, value))
            : TypeMatches(type.GetString()!, value);

    private static bool TypeMatches(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && IsInteger(value),
        _ => throw new NotSupportedException($"Schema type '{type}' is not supported by the validator.")
    };

    private static bool IsInteger(JsonElement number) =>
        number.TryGetInt64(out _) || (number.TryGetDecimal(out var d) && d == decimal.Truncate(d));

    private static bool Equal(JsonElement a, JsonElement b) =>
        a.ValueKind == b.ValueKind && (a.ValueKind == JsonValueKind.String
            ? a.GetString() == b.GetString()
            : a.GetRawText() == b.GetRawText());

    private static string DescribeType(JsonElement type) =>
        type.ValueKind == JsonValueKind.Array ? string.Join(" or ", type.EnumerateArray().Select(t => t.GetString())) : type.GetString()!;

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => value.ValueKind.ToString().ToLowerInvariant()
    };

    private static string Short(JsonElement value)
    {
        var raw = value.GetRawText();
        return raw.Length <= 40 ? raw : raw[..37] + "...";
    }

    private static string Loc(string path) => path.Length == 0 ? "(root)" : path;
}
