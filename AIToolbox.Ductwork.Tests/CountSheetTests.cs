using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Counting;

namespace AIToolbox.Ductwork.Tests;

public class CountSheetTests
{
    private static long _id;

    private static BuildingTypeRecord Type(string name, int? multiplier, bool confirmed = true, MultiplierSource? source = null, long? id = null) => new()
    {
        BuildingTypeId = id ?? Interlocked.Increment(ref _id),
        Name = name,
        Kind = BuildingKind.UnitType,
        Multiplier = multiplier,
        MultiplierSource = multiplier is null ? null : source ?? (confirmed ? MultiplierSource.Manual : MultiplierSource.Suggested),
        Confirmed = confirmed,
        JobId = 1,
        CreatedBy = "t"
    };

    private static EntityRecord Item(BuildingTypeRecord? type, string kind = "Boot", string? size = "4x10x7", decimal qty = 1, QuantityUnit unit = QuantityUnit.EA,
        Disposition disposition = Disposition.Included, ReviewStatus review = ReviewStatus.Proposed, string? category = null, string? tag = null) => new()
    {
        EntityId = Interlocked.Increment(ref _id),
        JobId = 1,
        BuildingTypeId = type?.BuildingTypeId,
        DetectionId = "d" + _id,
        Type = kind,
        Size = size,
        Category = category,
        Tag = tag,
        Quantity = qty,
        Unit = unit,
        Disposition = disposition,
        ReviewStatus = review,
        Method = DetectionMethod.Text,
        Family = DetectorFamily.LabelFlagCount
    };

    private static CountSheet Sheet(IReadOnlyList<BuildingTypeRecord> types, IReadOnlyList<EntityRecord> items) => CountSheetBuilder.Build(1, types, items);

    private static CountBlock Block(CountSheet s, string name) => s.Blocks.Single(b => b.Name == name);

    // ---- extension

    [Fact]
    public void Extended_quantity_is_quantity_per_building_times_the_confirmed_multiplier()
    {
        var t4 = Type("Type 4", 26);
        var sheet = Sheet([t4], Enumerable.Range(0, 7).Select(_ => Item(t4)).ToList());

        var line = Block(sheet, "Type 4").Lines.Single();
        Assert.Equal((7m, 26, 182m, ExtendedState.Known), (line.QuantityPerBuilding, line.Multiplier, line.ExtendedQuantity, line.State));
    }

    [Fact]
    public void A_multiplier_of_zero_is_a_known_count_and_gives_zero_not_to_be_determined()
    {
        var none = Type("Type 7", 0);
        var line = Block(Sheet([none], [Item(none), Item(none)]), "Type 7").Lines.Single();

        Assert.Equal((0m, ExtendedState.Known), (line.ExtendedQuantity, line.State));
    }

    [Fact]
    public void An_unknown_or_merely_suggested_multiplier_gives_to_be_determined_never_zero()
    {
        var unknown = Type("Unknown", null, confirmed: false);
        var suggested = Type("Suggested", 30, confirmed: false);   // a detector's guess is not a confirmation
        var sheet = Sheet([unknown, suggested], [Item(unknown), Item(suggested)]);

        foreach (var name in new[] { "Unknown", "Suggested" })
        {
            var line = Block(sheet, name).Lines.Single();
            Assert.Null(line.ExtendedQuantity);
            Assert.Equal(ExtendedState.MultiplierUnconfirmed, line.State);
        }

        Assert.Equal(["Suggested", "Unknown"], sheet.UnconfirmedBuildingTypes.Order());
    }

    [Fact]
    public void Changing_a_multiplier_recomputes_the_extension_and_the_totals()
    {
        var items = new List<EntityRecord>();
        var before = Type("Type 1", 10, id: 501);
        items.AddRange(Enumerable.Range(0, 3).Select(_ => Item(before)));
        Assert.Equal(30m, Sheet([before], items).Totals.Single().Total);

        var after = Type("Type 1", 12, id: 501);
        Assert.Equal(36m, Sheet([after], items).Totals.Single().Total);
    }

    [Fact]
    public void Quantities_keep_their_decimals()
    {
        var t = Type("Type 1", 3);
        var line = Block(Sheet([t], [Item(t, "Flex", "7in", 12.5m, QuantityUnit.LF), Item(t, "Flex", "7in", 7.25m, QuantityUnit.LF)]), "Type 1").Lines.Single();

        Assert.Equal((19.75m, 59.25m), (line.QuantityPerBuilding, line.ExtendedQuantity));
    }

    // ---- units are never mixed

    [Fact]
    public void The_same_item_in_different_units_is_separate_lines_and_separate_totals()
    {
        var t = Type("Type 1", 2);
        var sheet = Sheet([t], [Item(t, "Duct", "12x8", 10, QuantityUnit.LF), Item(t, "Duct", "12x8", 4, QuantityUnit.EA), Item(t, "Duct", "12x8", 30, QuantityUnit.SF)]);

        Assert.Equal(3, Block(sheet, "Type 1").Lines.Count);
        Assert.Equal(3, sheet.Totals.Count);
        Assert.Equal(new[] { 8m, 20m, 60m }.Order(), sheet.Totals.Select(x => x.Total!.Value).Order());
    }

    // ---- what counts

    [Fact]
    public void Excluded_and_factory_furnished_items_are_listed_but_not_extended_or_totaled()
    {
        var t = Type("Type 1", 5);
        var sheet = Sheet([t], [Item(t, "Boot", "A"), Item(t, "Boot", "B", disposition: Disposition.Excluded), Item(t, "Boot", "C", disposition: Disposition.FactoryFurnished)]);

        var lines = Block(sheet, "Type 1").Lines;
        Assert.Equal(ExtendedState.Known, lines.Single(l => l.Size == "A").State);
        Assert.Equal(ExtendedState.NotCounted, lines.Single(l => l.Size == "B").State);
        Assert.Equal(ExtendedState.NotCounted, lines.Single(l => l.Size == "C").State);
        Assert.All(lines.Where(l => l.Size != "A"), l => Assert.Null(l.ExtendedQuantity));
        Assert.Equal(["A"], sheet.Totals.Select(x => x.Size));
    }

    [Fact]
    public void An_undetermined_item_stays_visible_as_an_open_item_and_is_never_added_to_a_total()
    {
        var t = Type("Office", 1);
        var orphan = Item(t, "CfmOnlyTerminal", null, tag: "100 CFM", disposition: Disposition.TBD);
        var sheet = Sheet([t], [Item(t, "Boot", "4x10x7"), orphan, orphan with { EntityId = orphan.EntityId + 9000, DetectionId = "x" }]);

        var line = Block(sheet, "Office").Lines.Single(l => l.Type == "CfmOnlyTerminal");
        Assert.Equal((2m, ExtendedState.ItemUndetermined), (line.QuantityPerBuilding, line.State));
        Assert.Null(line.ExtendedQuantity);
        var open = Assert.Single(sheet.OpenItems);
        Assert.Equal(("Office", "100 CFM", 2m), (open.BuildingType, open.Tag, open.Quantity));
        Assert.DoesNotContain(sheet.Totals, x => x.Type == "CfmOnlyTerminal");
        Assert.False(sheet.IsFinal);
    }

    [Fact]
    public void Rejected_items_are_not_on_the_sheet_at_all()
    {
        var t = Type("Type 1", 4);
        var sheet = Sheet([t], [Item(t), Item(t, review: ReviewStatus.Rejected), Item(t, "Fan", "EF-1", review: ReviewStatus.Rejected)]);

        var line = Assert.Single(Block(sheet, "Type 1").Lines);
        Assert.Equal(1m, line.QuantityPerBuilding);
    }

    [Fact]
    public void Items_with_no_building_type_are_counted_once_in_a_block_that_says_so()
    {
        var t = Type("Type 1", 10);
        var sheet = Sheet([t], [Item(t, "Boot", "A"), Item(null, "Louver", "Roof"), Item(null, "Louver", "Roof")]);

        var orphans = sheet.Blocks.Single(b => b.IsUnassigned);
        Assert.Equal(CountSheetBuilder.UnassignedName, orphans.Name);
        Assert.Equal(2m, orphans.Lines.Single().ExtendedQuantity);       // not multiplied by anything
        Assert.Equal(2m, sheet.Totals.Single(x => x.Type == "Louver").Total);
        Assert.Empty(sheet.UnconfirmedBuildingTypes);                     // having no type is not an unconfirmed multiplier
    }

    [Fact]
    public void An_item_pointing_at_a_building_type_that_no_longer_exists_is_not_lost()
    {
        var gone = Type("Gone", 3);
        var sheet = Sheet([], [Item(gone)]);
        Assert.Single(sheet.Blocks.Single(b => b.IsUnassigned).Lines);
    }

    // ---- grouping and totals

    [Fact]
    public void Identical_items_are_one_line_ignoring_case_and_spacing_and_different_sizes_are_separate()
    {
        var t = Type("Type 1", 1);
        var sheet = Sheet([t], [Item(t, "Boot", "4x10x7"), Item(t, "BOOT", " 4X10X7 "), Item(t, "Boot", "4x12x7")]);

        var lines = Block(sheet, "Type 1").Lines;
        Assert.Equal(2, lines.Count);
        Assert.Equal(2m, lines.Single(l => l.Size!.Trim().ToLowerInvariant() == "4x10x7").QuantityPerBuilding);
    }

    [Fact]
    public void Job_totals_add_the_extensions_across_building_types()
    {
        var a = Type("Type A", 26);
        var b = Type("Type B", 11);
        var c = Type("Type C", 0);
        var sheet = Sheet([a, b, c],
        [
            ..Enumerable.Range(0, 7).Select(_ => Item(a)),       // 7 x 26 = 182
            ..Enumerable.Range(0, 6).Select(_ => Item(b)),       // 6 x 11 = 66
            ..Enumerable.Range(0, 6).Select(_ => Item(c))        // 6 x 0 = 0
        ]);

        var total = Assert.Single(sheet.Totals);
        Assert.Equal((248m, true, 248m), (total.Total, total.IsComplete, total.KnownPart));
        Assert.Empty(total.PendingBuildingTypes);
    }

    [Fact]
    public void A_total_that_depends_on_an_unconfirmed_multiplier_is_to_be_determined_and_names_what_holds_it_up()
    {
        var known = Type("Known", 10);
        var waiting = Type("Waiting", null, confirmed: false);
        var sheet = Sheet([known, waiting], [Item(known), Item(known), Item(waiting)]);

        var total = Assert.Single(sheet.Totals);
        Assert.Null(total.Total);
        Assert.False(total.IsComplete);
        Assert.Equal(20m, total.KnownPart);
        Assert.Equal(["Waiting"], total.PendingBuildingTypes);
        Assert.False(sheet.IsFinal);
    }

    [Fact]
    public void A_total_that_only_involves_confirmed_types_is_complete_even_if_another_item_is_pending()
    {
        var known = Type("Known", 10);
        var waiting = Type("Waiting", null, confirmed: false);
        var sheet = Sheet([known, waiting], [Item(known, "Boot", "A"), Item(waiting, "Boot", "B")]);

        Assert.Equal(10m, sheet.Totals.Single(x => x.Size == "A").Total);
        Assert.Null(sheet.Totals.Single(x => x.Size == "B").Total);
    }

    [Fact]
    public void Totals_say_how_much_of_them_is_still_an_unverified_proposal()
    {
        var t = Type("Type 1", 2);
        var sheet = Sheet([t], [Item(t, review: ReviewStatus.Verified), Item(t, review: ReviewStatus.Proposed), Item(t, review: ReviewStatus.Manual)]);

        var total = sheet.Totals.Single();
        Assert.Equal(6m, total.Total);
        Assert.Equal(1, total.ProposedCount);
        Assert.False(sheet.IsFinal);
        var line = Block(sheet, "Type 1").Lines.Single();
        Assert.Equal((3, 2, 1), (line.EntityCount, line.VerifiedCount, line.ProposedCount));
    }

    [Fact]
    public void A_sheet_with_everything_verified_confirmed_and_resolved_is_final()
    {
        var t = Type("Type 1", 2);
        var sheet = Sheet([t], [Item(t, review: ReviewStatus.Verified), Item(t, "Fan", "EF-1", review: ReviewStatus.Manual)]);

        Assert.True(sheet.IsFinal);
        Assert.Empty(sheet.OpenItems);
        Assert.Empty(sheet.UnconfirmedBuildingTypes);
    }

    [Fact]
    public void An_empty_job_gives_an_empty_sheet()
    {
        var sheet = Sheet([], []);
        Assert.Empty(sheet.Blocks);
        Assert.Empty(sheet.Totals);
        Assert.True(sheet.IsFinal);
    }

    [Fact]
    public void Blocks_and_lines_come_out_in_a_stable_order()
    {
        var b = Type("Beta", 1);
        var a = Type("alpha", 1);
        var items = new[] { Item(b, "Boot", "Z"), Item(a, "Fan", "EF-1"), Item(a, "Boot", "B"), Item(a, "Boot", "A") };

        var first = Sheet([b, a], items);
        var second = Sheet([a, b], items.Reverse().ToList());

        Assert.Equal(["alpha", "Beta"], first.Blocks.Select(x => x.Name));
        Assert.Equal(first.Blocks.SelectMany(x => x.Lines).Select(l => (l.Type, l.Size)), second.Blocks.SelectMany(x => x.Lines).Select(l => (l.Type, l.Size)));
        Assert.Equal(first.Totals.Select(t => (t.Type, t.Size)), second.Totals.Select(t => (t.Type, t.Size)));
    }
}
