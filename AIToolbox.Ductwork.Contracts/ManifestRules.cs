namespace AIToolbox.Ductwork.Contracts;

/// <summary>
/// Checks a JSON Schema cannot express. The web app is the single write path: it runs the schema
/// check, then these rules, before anything a worker or a model produced is stored.
/// </summary>
public static class ManifestRules
{
    public static IReadOnlyList<string> Validate(TaskResultManifest manifest)
    {
        var errors = new List<string>();

        if (!VersionCompatible(manifest.ContractVersion))
            errors.Add($"Contract version '{manifest.ContractVersion}' is not compatible with {ContractInfo.Version}.");

        foreach (var producerError in manifest.Producer.Validate())
            errors.Add(producerError);

        var detectionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in manifest.Detections)
        {
            if (string.IsNullOrWhiteSpace(d.DetectionId)) errors.Add("A detection has no id.");
            else if (!detectionIds.Add(d.DetectionId)) errors.Add($"Detection id '{d.DetectionId}' is used more than once.");

            if (string.IsNullOrWhiteSpace(d.Type)) errors.Add($"Detection '{d.DetectionId}' has no type.");
            if (!double.IsFinite(d.Confidence) || d.Confidence is < 0 or > 1)
                errors.Add($"Detection '{d.DetectionId}' has confidence {d.Confidence}, which is outside 0 to 1.");
            if (!double.IsFinite(d.Quantity) || d.Quantity < 0)
                errors.Add($"Detection '{d.DetectionId}' has quantity {d.Quantity}, which must be zero or more.");

            if (d.Box is { } box)
            {
                if (!double.IsFinite(box.X0 + box.Y0 + box.X1 + box.Y1))
                    errors.Add($"Detection '{d.DetectionId}' has a non-finite box.");
                else if (box.X1 < box.X0 || box.Y1 < box.Y0)
                    errors.Add($"Detection '{d.DetectionId}' has an inverted box.");
            }
        }

        var findingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in manifest.Findings)
        {
            if (string.IsNullOrWhiteSpace(f.FindingId)) errors.Add("A finding has no id.");
            else if (!findingIds.Add(f.FindingId)) errors.Add($"Finding id '{f.FindingId}' is used more than once.");

            if (string.IsNullOrWhiteSpace(f.Question)) errors.Add($"Finding '{f.FindingId}' has no question.");
            if (f.DetectionId is not null && !detectionIds.Contains(f.DetectionId))
                errors.Add($"Finding '{f.FindingId}' refers to unknown detection '{f.DetectionId}'.");
        }

        return errors;
    }

    /// <summary>Same major version. Minor and patch differences are tolerated.</summary>
    public static bool VersionCompatible(string? version) =>
        Version.TryParse(version, out var parsed) && parsed.Major == ContractInfo.Major;
}
