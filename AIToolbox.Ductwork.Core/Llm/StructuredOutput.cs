using System.Text.Json;
using AIToolbox.Ductwork.Contracts;

namespace AIToolbox.Ductwork.Core.Llm;

public sealed record StructuredOutputResult<T>(T? Value, string? Json, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0 && Value is not null;
}

/// <summary>
/// Model output is untrusted text. This extracts the JSON a model returned, validates it against the
/// JSON Schema generated from the contract type, and only then deserializes it (plan sections 6.2 and
/// 6.4). A failure returns every reason, so a caller can show them or send them back for one repair
/// attempt; nothing invalid is ever returned as a value.
/// </summary>
public static class StructuredOutput
{
    public const int MaxJsonChars = 2_000_000;

    public static SchemaValidator ValidatorFor(Type type) =>
        FromNode(ContractJson.GenerateSchema(type));

    private static SchemaValidator FromNode(System.Text.Json.Nodes.JsonNode node) =>
        SchemaValidator.FromJson(node.ToJsonString());

    public static StructuredOutputResult<T> Parse<T>(string? responseText) where T : class =>
        Parse<T>(responseText, ValidatorFor(typeof(T)));

    public static StructuredOutputResult<T> Parse<T>(string? responseText, SchemaValidator validator) where T : class
    {
        var json = ExtractJson(responseText);
        if (json is null)
            return new(null, null, ["The response contains no JSON object or array."]);

        if (json.Length > MaxJsonChars)
            return new(null, null, [$"The JSON is {json.Length:N0} characters, over the {MaxJsonChars:N0} limit."]);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return new(null, json, [$"The JSON is malformed: {ex.Message}"]);
        }

        using (document)
        {
            var errors = validator.Validate(document.RootElement);
            if (errors.Count > 0)
                return new(null, json, errors);
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(json, ContractJson.Options);
            return value is null
                ? new(null, json, ["The JSON deserialized to null."])
                : new(value, json, []);
        }
        catch (JsonException ex)
        {
            return new(null, json, [$"The JSON does not match the contract: {ex.Message}"]);
        }
    }

    /// <summary>
    /// Finds the first complete JSON object or array in model text, tolerating markdown code fences
    /// and prose around it. String contents and escapes are respected, so braces inside strings do not
    /// confuse the scan.
    /// </summary>
    public static string? ExtractJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        for (var start = 0; start < text.Length; start++)
        {
            if (text[start] is not ('{' or '[')) continue;
            var end = FindClosing(text, start);
            if (end < 0) continue;

            var candidate = text[start..(end + 1)];
            try
            {
                using var _ = JsonDocument.Parse(candidate);
                return candidate;
            }
            catch (JsonException)
            {
                // Not JSON (for example "[note]" in prose). Keep scanning from the next character.
            }
        }

        return null;
    }

    private static int FindClosing(string text, int start)
    {
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{' or '[': depth++; break;
                case '}' or ']':
                    depth--;
                    if (depth == 0) return i;
                    break;
            }
        }

        return -1;
    }
}
