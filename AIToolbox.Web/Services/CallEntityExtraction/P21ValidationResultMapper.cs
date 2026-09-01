using System.Text.Json;
using AIToolbox.Web.Models;

namespace AIToolbox.Web.Services.CallEntityExtraction;

/// <summary>
/// Reads a resolve_model/get_warranty_by_serial MCP structured-content result by field name and
/// maps it to this pipeline's ValidationResult. Separated from P21IdentifierValidationClient
/// (which owns the network call) so the mapping itself -- the part with real behavioral decisions
/// baked into it (notFound suppresses for model/item but becomes NotFoundInWindow for serials) --
/// is directly unit-testable against JSON fixtures without faking the MCP SDK's client type.
///
/// AIToolbox.Web has no reference to P21Toolbox's C# result types (a different repo/solution), so
/// fields are read generically off the raw JSON rather than deserialized into a shared type.
/// P21 Toolbox's structured-content status enum serializes camelCase (saas_16.2's
/// StringEnumConverter fix), e.g. "ok" / "notFound" / "ambiguous" / "unavailable".
/// </summary>
public static class P21ValidationResultMapper
{
    public const string ResolveModelTool = "resolve_model";
    public const string GetWarrantyBySerialTool = "get_warranty_by_serial";

    public static ValidationResult? MapModelResolution(JsonElement structured, string canonicalValue)
    {
        var status = GetString(structured, "status");
        switch (status)
        {
            case "ok":
                var resolved = structured.TryGetProperty("resolved", out var resolvedElement)
                    && resolvedElement.ValueKind == JsonValueKind.Object
                        ? resolvedElement
                        : (JsonElement?)null;
                var resolvedValue = GetString(resolved, "pimCode") ?? GetString(resolved, "primarySalesPackageId") ?? canonicalValue;
                return new ValidationResult
                {
                    Status = ValidationStatus.Validated,
                    CandidateValues = [resolvedValue],
                    Source = $"p21_mcp:{ResolveModelTool}"
                };

            case "ambiguous":
                var candidateValues = new List<string>();
                if (structured.TryGetProperty("candidates", out var candidatesElement) && candidatesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var candidateElement in candidatesElement.EnumerateArray())
                    {
                        var value = GetString(candidateElement, "pimCode") ?? GetString(candidateElement, "primarySalesPackageId");
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            candidateValues.Add(value);
                        }
                    }
                }

                return candidateValues.Count == 0
                    ? null
                    : new ValidationResult
                    {
                        Status = ValidationStatus.Ambiguous,
                        CandidateValues = candidateValues,
                        Source = $"p21_mcp:{ResolveModelTool}"
                    };

            // "notFound" and "unavailable" both suppress the notification for model/item
            // candidates -- unlike a serial, there's no "predates P21's window" excuse for an
            // item/model P21 has never heard of.
            default:
                return null;
        }
    }

    public static ValidationResult? MapWarrantyResult(JsonElement structured, string canonicalValue)
    {
        var status = GetString(structured, "status");
        return status switch
        {
            "ok" => new ValidationResult
            {
                Status = ValidationStatus.Validated,
                CandidateValues = [GetString(structured, "serialNumber") ?? canonicalValue],
                Source = $"p21_mcp:{GetWarrantyBySerialTool}"
            },

            // The distinguishing case: a shape-valid serial P21 has no record of may simply
            // predate P21's own data retention window, not be wrong. See ValidationStatus's doc.
            "notFound" => new ValidationResult
            {
                Status = ValidationStatus.NotFoundInWindow,
                CandidateValues = [canonicalValue],
                Source = $"p21_mcp:{GetWarrantyBySerialTool}"
            },

            _ => null
        };
    }

    private static string? GetString(JsonElement? element, string propertyName)
    {
        return element is { } value ? GetString(value, propertyName) : null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }
}
