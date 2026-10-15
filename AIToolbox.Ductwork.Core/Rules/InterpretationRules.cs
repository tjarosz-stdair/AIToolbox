using System.Text.RegularExpressions;
using AIToolbox.Ductwork.Contracts;

namespace AIToolbox.Ductwork.Core.Rules;

/// <summary>
/// What a rule matches. Every field that is set must match; unset fields match anything. Matching
/// is deterministic and case-insensitive. A regex is allowed for the size and tag only, with a
/// timeout, so a rule cannot hang the evaluator.
/// </summary>
public sealed record RuleMatch
{
    public string? Type { get; init; }
    public string? Size { get; init; }
    public string? SizePattern { get; init; }

    /// <summary>The tag exactly, for example "100 CFM" on a terminal that carries no size.</summary>
    public string? Tag { get; init; }
    public string? BuildingType { get; init; }
    public string? Method { get; init; }
}

/// <summary>What a rule does to a matching detection. Only these four actions exist.</summary>
public sealed record RuleAction
{
    public string? SetSize { get; init; }

    /// <summary>Appended to the existing size, for example "x7" turns "4x12" into "4x12x7". Needs a size to append to.</summary>
    public string? SizeSuffix { get; init; }
    public string? SetType { get; init; }
    public double? MultiplyQuantity { get; init; }
}

/// <summary>
/// An interpretation rule (plan section 6.11): the user's decision about how to read something the
/// drawing leaves ambiguous, for example "a two-dimension boot label means depth 7" or "a pass-through
/// flag means two grilles". It is data with a scope, a deterministic match and a deterministic action.
/// An AI may help a user draft one, but only an approved rule can change a detection.
/// </summary>
public sealed record InterpretationRule
{
    public required string RuleId { get; init; }
    public int Version { get; init; } = 1;
    public required string Description { get; init; }
    public RuleMatch Match { get; init; } = new();
    public required RuleAction Action { get; init; }
}

public sealed record RuleApplication(string RuleId, int RuleVersion, string DetectionId, string Change);

public sealed record RuleResult(IReadOnlyList<DetectionProposal> Detections, IReadOnlyList<RuleApplication> Applications);

public static class InterpretationRuleEngine
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Applies the rules in order. Each rule sees the detection as the earlier rules left it. Every
    /// change is recorded, so a rule's effect can be previewed, undone and reproduced. The input is
    /// never modified.
    /// </summary>
    public static RuleResult Apply(IReadOnlyList<DetectionProposal> detections, IReadOnlyList<InterpretationRule> rules)
    {
        Validate(rules);
        var applications = new List<RuleApplication>();
        var result = new List<DetectionProposal>(detections.Count);

        foreach (var original in detections)
        {
            var current = original;
            foreach (var rule in rules)
            {
                if (!Matches(rule.Match, current)) continue;

                var (next, change) = Act(rule.Action, current);
                if (change is null) continue;

                applications.Add(new RuleApplication(rule.RuleId, rule.Version, current.DetectionId, change));
                current = next;
            }

            result.Add(current);
        }

        return new RuleResult(result, applications);
    }

    /// <summary>Preview: which detections a rule would change, without changing anything.</summary>
    public static IReadOnlyList<RuleApplication> Preview(IReadOnlyList<DetectionProposal> detections, InterpretationRule rule) =>
        Apply(detections, [rule]).Applications;

    public static void Validate(IReadOnlyList<InterpretationRule> rules)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.RuleId)) throw new ArgumentException("A rule needs an id.");
            if (!seen.Add(rule.RuleId)) throw new ArgumentException($"Rule id '{rule.RuleId}' is used more than once.");

            var a = rule.Action;
            if (a.SetSize is null && a.SizeSuffix is null && a.SetType is null && a.MultiplyQuantity is null)
                throw new ArgumentException($"Rule '{rule.RuleId}' has no action.");
            if (a.MultiplyQuantity is { } m && (!double.IsFinite(m) || m < 0))
                throw new ArgumentException($"Rule '{rule.RuleId}' multiplies by {m}, which must be zero or more.");

            var match = rule.Match;
            if (match.Type is null && match.Size is null && match.SizePattern is null && match.Tag is null && match.BuildingType is null && match.Method is null)
                throw new ArgumentException($"Rule '{rule.RuleId}' matches everything. Give it at least one condition.");
            if (match.SizePattern is not null)
                _ = new Regex(match.SizePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
    }

    private static bool Matches(RuleMatch m, DetectionProposal d)
    {
        if (m.Type is not null && !Eq(m.Type, d.Type)) return false;
        if (m.BuildingType is not null && !Eq(m.BuildingType, d.BuildingType)) return false;
        if (m.Method is not null && !Eq(m.Method, d.Method.ToString())) return false;
        if (m.Size is not null && !Eq(m.Size, d.Size)) return false;
        if (m.Tag is not null && !Eq(m.Tag, d.Tag)) return false;
        if (m.SizePattern is not null)
        {
            if (d.Size is null) return false;
            try
            {
                if (!Regex.IsMatch(d.Size, m.SizePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout))
                    return false;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        return true;
    }

    private static (DetectionProposal Next, string? Change) Act(RuleAction action, DetectionProposal d)
    {
        var next = d;
        var changes = new List<string>();

        if (action.SetType is { } type && !Eq(type, d.Type))
        {
            next = next with { Type = type };
            changes.Add($"type {d.Type} -> {type}");
        }

        if (action.SetSize is { } size && !Eq(size, d.Size))
        {
            next = next with { Size = size };
            changes.Add($"size {d.Size ?? "(none)"} -> {size}");
        }

        if (!string.IsNullOrEmpty(action.SizeSuffix) && next.Size is { } existing
            && !existing.EndsWith(action.SizeSuffix, StringComparison.OrdinalIgnoreCase))
        {
            next = next with { Size = existing + action.SizeSuffix };
            changes.Add($"size {existing} -> {next.Size}");
        }

        if (action.MultiplyQuantity is { } factor && factor != 1)
        {
            next = next with { Quantity = d.Quantity * factor };
            changes.Add($"quantity {d.Quantity:0.##} -> {next.Quantity:0.##}");
        }

        return (next, changes.Count == 0 ? null : string.Join("; ", changes));
    }

    private static bool Eq(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
