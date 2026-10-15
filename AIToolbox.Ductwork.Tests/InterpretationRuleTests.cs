using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Evaluation;
using AIToolbox.Ductwork.Core.Rules;

namespace AIToolbox.Ductwork.Tests;

public class InterpretationRuleTests
{
    private static DetectionProposal Det(string id, string type, string? size, string? building = "Type 1", double qty = 1,
        DetectionMethod method = DetectionMethod.Text) => new()
    {
        DetectionId = id, Type = type, Size = size, BuildingType = building, Quantity = qty, Confidence = 0.9,
        Method = method, Family = DetectorFamily.LabelFlagCount
    };

    private static readonly InterpretationRule TwoDimensionDepth = new()
    {
        RuleId = "depth-7",
        Description = "A boot labeled WxH with no depth is depth 7.",
        Match = new RuleMatch { Type = "Boot", SizePattern = @"^\d+x\d+$" },
        Action = new RuleAction { SetSize = "4x12x7" }
    };

    private static readonly InterpretationRule PassThroughPair = new()
    {
        RuleId = "pass-through-x2",
        Description = "One pass-through flag stands for two grilles.",
        Match = new RuleMatch { Type = "Grille", Size = "14x9 Pass Through", Method = "FlagNote" },
        Action = new RuleAction { MultiplyQuantity = 2 }
    };

    [Fact]
    public void A_rule_changes_only_what_it_matches_and_records_each_change()
    {
        var input = new[]
        {
            Det("a", "Boot", "4x12"),
            Det("b", "Boot", "4x10x7"),
            Det("c", "Fan", "EF-1")
        };

        var result = InterpretationRuleEngine.Apply(input, [TwoDimensionDepth]);

        Assert.Equal("4x12x7", result.Detections[0].Size);
        Assert.Equal("4x10x7", result.Detections[1].Size);
        Assert.Equal("EF-1", result.Detections[2].Size);
        var application = Assert.Single(result.Applications);
        Assert.Equal(("depth-7", 1, "a"), (application.RuleId, application.RuleVersion, application.DetectionId));
        Assert.Contains("4x12 -> 4x12x7", application.Change);
    }

    [Fact]
    public void A_size_suffix_extends_the_drawn_size_and_is_not_applied_twice()
    {
        var rule = new InterpretationRule
        {
            RuleId = "depth", Description = "x",
            Match = new RuleMatch { Type = "Boot", SizePattern = @"^\d+x\d+$" },
            Action = new RuleAction { SizeSuffix = "x7" }
        };

        var once = InterpretationRuleEngine.Apply([Det("a", "Boot", "4x12"), Det("b", "Boot", null)], [rule]);
        Assert.Equal("4x12x7", once.Detections[0].Size);
        Assert.Null(once.Detections[1].Size);                  // nothing to append to, and it did not match the pattern anyway

        var twice = InterpretationRuleEngine.Apply(once.Detections, [rule]);
        Assert.Equal("4x12x7", twice.Detections[0].Size);      // does not match the pattern any more
        Assert.Empty(twice.Applications);

        var unconditional = rule with { Match = new RuleMatch { Type = "Boot" } };
        Assert.Empty(InterpretationRuleEngine.Apply(once.Detections, [unconditional]).Applications);   // already ends in x7
    }

    [Fact]
    public void A_terminal_with_no_size_can_be_resolved_by_its_tag_and_the_building_type()
    {
        var rule = new InterpretationRule
        {
            RuleId = "cfm-100", Description = "x",
            Match = new RuleMatch { Type = "CfmOnlyTerminal", Tag = "100 CFM", BuildingType = "Office" },
            Action = new RuleAction { SetType = "Boot", SetSize = "4x12x7" }
        };
        var orphan = Det("a", "CfmOnlyTerminal", null, "Office") with { Tag = "100 CFM" };
        var otherTag = Det("b", "CfmOnlyTerminal", null, "Office") with { Tag = "75 CFM" };
        var otherBuilding = Det("c", "CfmOnlyTerminal", null, "House") with { Tag = "100 CFM" };

        var result = InterpretationRuleEngine.Apply([orphan, otherTag, otherBuilding], [rule]);

        Assert.Equal(("Boot", "4x12x7"), (result.Detections[0].Type, result.Detections[0].Size));
        Assert.Equal("CfmOnlyTerminal", result.Detections[1].Type);
        Assert.Equal("CfmOnlyTerminal", result.Detections[2].Type);
        Assert.Single(result.Applications);
    }

    [Fact]
    public void The_input_is_never_modified()
    {
        var input = new[] { Det("a", "Boot", "4x12") };
        InterpretationRuleEngine.Apply(input, [TwoDimensionDepth]);
        Assert.Equal("4x12", input[0].Size);
    }

    [Fact]
    public void Multiplying_a_quantity_is_explicit_and_only_for_the_matching_method()
    {
        var input = new[]
        {
            Det("a", "Grille", "14x9 Pass Through", method: DetectionMethod.FlagNote),
            Det("b", "Grille", "14x9 Pass Through", method: DetectionMethod.Text)
        };

        var result = InterpretationRuleEngine.Apply(input, [PassThroughPair]);

        Assert.Equal(2, result.Detections[0].Quantity);
        Assert.Equal(1, result.Detections[1].Quantity);
    }

    [Fact]
    public void Rules_apply_in_order_and_each_sees_the_previous_result()
    {
        var first = new InterpretationRule
        {
            RuleId = "r1", Description = "x", Match = new RuleMatch { Type = "Boot", Size = "4x12" }, Action = new RuleAction { SetSize = "4x12x7" }
        };
        var second = new InterpretationRule
        {
            RuleId = "r2", Description = "y", Match = new RuleMatch { Type = "Boot", Size = "4x12x7" }, Action = new RuleAction { MultiplyQuantity = 3 }
        };

        var result = InterpretationRuleEngine.Apply([Det("a", "Boot", "4x12")], [first, second]);

        Assert.Equal("4x12x7", result.Detections[0].Size);
        Assert.Equal(3, result.Detections[0].Quantity);
        Assert.Equal(["r1", "r2"], result.Applications.Select(a => a.RuleId));
    }

    [Fact]
    public void A_rule_that_would_change_nothing_records_nothing()
    {
        var noOp = new InterpretationRule
        {
            RuleId = "noop", Description = "x", Match = new RuleMatch { Type = "Boot" }, Action = new RuleAction { SetSize = "4x10x7" }
        };
        var result = InterpretationRuleEngine.Apply([Det("a", "Boot", "4x10x7")], [noOp]);
        Assert.Empty(result.Applications);
    }

    [Fact]
    public void Preview_lists_the_effect_of_one_rule_without_applying_it()
    {
        var input = new[] { Det("a", "Boot", "4x12"), Det("b", "Boot", "6x10") };
        var preview = InterpretationRuleEngine.Preview(input, TwoDimensionDepth);

        Assert.Equal(["a", "b"], preview.Select(p => p.DetectionId));
        Assert.Equal("4x12", input[0].Size);
    }

    [Fact]
    public void Building_type_scope_limits_a_rule()
    {
        var scoped = TwoDimensionDepth with { Match = TwoDimensionDepth.Match with { BuildingType = "Type 2" } };
        var result = InterpretationRuleEngine.Apply([Det("a", "Boot", "4x12", "Type 1"), Det("b", "Boot", "4x12", "Type 2")], [scoped]);

        Assert.Equal("4x12", result.Detections[0].Size);
        Assert.Equal("4x12x7", result.Detections[1].Size);
    }

    [Fact]
    public void Matching_is_case_and_whitespace_insensitive()
    {
        var rule = new InterpretationRule
        {
            RuleId = "r", Description = "x", Match = new RuleMatch { Type = " boot ", Size = "4X12" }, Action = new RuleAction { SetSize = "4x12x7" }
        };
        var result = InterpretationRuleEngine.Apply([Det("a", "Boot", "4x12")], [rule]);
        Assert.Single(result.Applications);
    }

    [Fact]
    public void Invalid_rules_are_refused_before_anything_is_applied()
    {
        InterpretationRule R(RuleMatch? m = null, RuleAction? a = null, string id = "r") => new()
        {
            RuleId = id, Description = "x", Match = m ?? new RuleMatch { Type = "Boot" }, Action = a ?? new RuleAction { SetSize = "x" }
        };

        Assert.Throws<ArgumentException>(() => InterpretationRuleEngine.Validate([R(id: " ")]));
        Assert.Throws<ArgumentException>(() => InterpretationRuleEngine.Validate([R(), R()]));                                  // duplicate id
        Assert.Throws<ArgumentException>(() => InterpretationRuleEngine.Validate([R(a: new RuleAction())]));                     // no action
        Assert.Throws<ArgumentException>(() => InterpretationRuleEngine.Validate([R(m: new RuleMatch())]));                      // matches everything
        Assert.Throws<ArgumentException>(() => InterpretationRuleEngine.Validate([R(a: new RuleAction { MultiplyQuantity = -1 })]));
        Assert.Throws<ArgumentException>(() => InterpretationRuleEngine.Validate([R(a: new RuleAction { MultiplyQuantity = double.NaN })]));
        Assert.ThrowsAny<ArgumentException>(() => InterpretationRuleEngine.Validate([R(m: new RuleMatch { SizePattern = "(" })]));
    }

    [Fact]
    public void A_rule_turns_a_disagreement_into_a_match_and_the_score_shows_it()
    {
        var gold = new[]
        {
            new GoldItem { BuildingType = "Type 1", Type = "Boot", Size = "4x12x7", Quantity = 2 },
            new GoldItem { BuildingType = "Type 1", Type = "Grille", Size = "14x9 Pass Through", Quantity = 4 }
        };
        var detections = new[]
        {
            Det("a", "Boot", "4x12"), Det("b", "Boot", "4x12"),
            Det("c", "Grille", "14x9 Pass Through", method: DetectionMethod.FlagNote),
            Det("d", "Grille", "14x9 Pass Through", method: DetectionMethod.FlagNote)
        };

        var before = EvalHarness.Evaluate(gold, detections, MatchMode.Count).Score.Overall;
        var after = EvalHarness.Evaluate(gold, InterpretationRuleEngine.Apply(detections, [TwoDimensionDepth, PassThroughPair]).Detections, MatchMode.Count).Score.Overall;

        Assert.True(before.Recall < 1.0);
        Assert.Equal(1.0, after.Precision);
        Assert.Equal(1.0, after.Recall);
    }
}
