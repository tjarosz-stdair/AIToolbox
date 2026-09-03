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
                var pimCode = GetString(resolved, "pimCode");
                var resolvedValue = pimCode ?? GetString(resolved, "primarySalesPackageId") ?? canonicalValue;
                return new ValidationResult
                {
                    Status = ValidationStatus.Validated,
                    CandidateValues = [resolvedValue],
                    Source = $"p21_mcp:{ResolveModelTool}",
                    DetectedEntityType = ResolveEntityType(pimCode)
                };

            case "ambiguous":
                var candidateValues = new List<string>();
                var anyPimCode = false;
                if (structured.TryGetProperty("candidates", out var candidatesElement) && candidatesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var candidateElement in candidatesElement.EnumerateArray())
                    {
                        var candidatePimCode = GetString(candidateElement, "pimCode");
                        var value = candidatePimCode ?? GetString(candidateElement, "primarySalesPackageId");
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            candidateValues.Add(value);
                            anyPimCode = anyPimCode || candidatePimCode is not null;
                        }
                    }
                }

                // A mixed set of candidates (some with a pimCode, some without) is treated as a
                // Model Number ambiguity -- PIC search is still the more useful disambiguation
                // action for a rep to offer than Part Number's narrower action set.
                return candidateValues.Count == 0
                    ? null
                    : new ValidationResult
                    {
                        Status = ValidationStatus.Ambiguous,
                        CandidateValues = candidateValues,
                        Source = $"p21_mcp:{ResolveModelTool}",
                        DetectedEntityType = anyPimCode ? DetectedEntityType.ModelNumber : DetectedEntityType.PartNumber
                    };

            // "notFound" and "unavailable" both suppress the notification for model/item
            // candidates -- unlike a serial, there's no "predates P21's window" excuse for an
            // item/model P21 has never heard of.
            default:
                return null;
        }
    }

    /// <summary>
    /// pimCode is a Carrier PIC model code; a resolution carrying one is a Model Number, while a
    /// generic P21 item/supplier part match (primarySalesPackageId only, no pimCode) is a Part
    /// Number. This is a heuristic on today's resolve_model response shape, not a distinct P21
    /// capability -- validate it holds up against real P21 data, since P21ToolboxContract's
    /// resolve_model tool doesn't (yet) return an explicit type field.
    /// </summary>
    private static DetectedEntityType ResolveEntityType(string? pimCode) =>
        pimCode is not null ? DetectedEntityType.ModelNumber : DetectedEntityType.PartNumber;

    public static ValidationResult? MapWarrantyResult(JsonElement structured, string canonicalValue)
    {
        var status = GetString(structured, "status");
        return status switch
        {
            "ok" => new ValidationResult
            {
                Status = ValidationStatus.Validated,
                CandidateValues = [GetString(structured, "serialNumber") ?? canonicalValue],
                Source = $"p21_mcp:{GetWarrantyBySerialTool}",
                DetectedEntityType = DetectedEntityType.SerialNumber
            },

            // The distinguishing case: a shape-valid serial P21 has no record of may simply
            // predate P21's own data retention window, not be wrong. See ValidationStatus's doc.
            "notFound" => new ValidationResult
            {
                Status = ValidationStatus.NotFoundInWindow,
                CandidateValues = [canonicalValue],
                Source = $"p21_mcp:{GetWarrantyBySerialTool}",
                DetectedEntityType = DetectedEntityType.SerialNumber
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
