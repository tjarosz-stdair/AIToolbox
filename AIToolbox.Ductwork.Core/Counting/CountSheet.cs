using AIToolbox.Ductwork.Contracts;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Counting;

/// <summary>Why a line has no extended quantity. An unknown quantity is shown as to be determined, never as zero.</summary>
public enum ExtendedState
{
    /// <summary>The extended quantity is known: quantity per building times a confirmed multiplier.</summary>
    Known,
    /// <summary>The building type's multiplier is not known or has only been suggested, so the extension cannot be stated.</summary>
    MultiplierUnconfirmed,
    /// <summary>The item is itself undetermined (for example a terminal labeled only with its airflow), so it is listed but not extended or totaled.</summary>
    ItemUndetermined,
    /// <summary>Not part of the quantity: excluded, or furnished by someone else.</summary>
    NotCounted
}

public sealed record CountLine
{
    public string? Category { get; init; }
    public string? Tag { get; init; }
    public required string Type { get; init; }
    public string? Size { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? CustomerPartCode { get; init; }
    public QuantityUnit Unit { get; init; }
    public Disposition Disposition { get; init; }

    /// <summary>How many of this item one building of this type has.</summary>
    public decimal QuantityPerBuilding { get; init; }
    public int? Multiplier { get; init; }
    /// <summary>Quantity per building times the multiplier. Null unless <see cref="State"/> is Known. Never a silent zero.</summary>
    public decimal? ExtendedQuantity { get; init; }
    public ExtendedState State { get; init; }

    public int EntityCount { get; init; }
    public int VerifiedCount { get; init; }
    public int ProposedCount { get; init; }
    public int StaleCount { get; init; }
}

public sealed record CountBlock
{
    public long? BuildingTypeId { get; init; }
    public required string Name { get; init; }
    public BuildingKind? Kind { get; init; }
    public int? Multiplier { get; init; }
    public MultiplierSource? MultiplierSource { get; init; }
    public string? MultiplierEvidence { get; init; }
    public bool MultiplierConfirmed { get; init; }
    public bool IsUnassigned => BuildingTypeId is null;
    public required IReadOnlyList<CountLine> Lines { get; init; }
}

/// <summary>A job total: the sum of extended quantities for one item across every building type.</summary>
public sealed record TotalLine
{
    public string? Category { get; init; }
    public string? Tag { get; init; }
    public required string Type { get; init; }
    public string? Size { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? CustomerPartCode { get; init; }
    public QuantityUnit Unit { get; init; }

    /// <summary>The sum, or null while any contributing building type is unconfirmed: the total is then to be determined, not a partial sum presented as final.</summary>
    public decimal? Total { get; init; }
    /// <summary>The part that is known today, for showing "at least this many".</summary>
    public decimal KnownPart { get; init; }
    public bool IsComplete => Total is not null;
    public IReadOnlyList<string> PendingBuildingTypes { get; init; } = [];
    public int ProposedCount { get; init; }
}

/// <summary>Items that cannot be added up yet, listed so they stay visible instead of vanishing from the totals.</summary>
public sealed record OpenItem(string BuildingType, string Type, string? Tag, string? Size, decimal Quantity, QuantityUnit Unit, Disposition Disposition, string Reason);

public sealed record CountSheet
{
    public long JobId { get; init; }
    public required IReadOnlyList<CountBlock> Blocks { get; init; }
    public required IReadOnlyList<TotalLine> Totals { get; init; }
    public required IReadOnlyList<OpenItem> OpenItems { get; init; }
    public IReadOnlyList<string> UnconfirmedBuildingTypes { get; init; } = [];

    public bool IsFinal => OpenItems.Count == 0 && UnconfirmedBuildingTypes.Count == 0 && Totals.All(t => t.IsComplete) && Totals.All(t => t.ProposedCount == 0);
}

/// <summary>
/// Builds the count sheet from building types and entities (plan section 12). It is computed, never
/// stored or typed, so the screen, the Excel file and the PDF cannot disagree. The rules:
/// <list type="bullet">
/// <item>Extended quantity is quantity per building times the building type's confirmed multiplier. A
/// multiplier of zero is a known count and gives zero. An unknown or merely suggested multiplier gives
/// "to be determined", never zero.</item>
/// <item>Units are never added together: EA, LF and SF are separate lines and separate totals.</item>
/// <item>Only items whose disposition is Included and that were not rejected are extended and totaled.
/// Excluded and factory-furnished items are listed but not counted. An undetermined (TBD) item is listed
/// and kept visible in the open items, and is never added into a total.</item>
/// <item>Items with no building type are counted once, in an "unassigned" block, and flagged.</item>
/// <item>A total that depends on an unconfirmed multiplier is to be determined, and names the types holding it up.</item>
/// <item>Proposals count in the totals but are tallied, so a total can say how much of it is unverified.</item>
/// </list>
/// </summary>
public static class CountSheetBuilder
{
    public const string UnassignedName = "Not assigned to a building type";

    public static CountSheet Build(long jobId, IReadOnlyList<BuildingTypeRecord> buildingTypes, IReadOnlyList<EntityRecord> entities)
    {
        var types = buildingTypes.ToDictionary(b => b.BuildingTypeId);
        var blocks = new List<CountBlock>();
        var open = new List<OpenItem>();

        // Rejected items are not part of the sheet at all: a person decided they are not real.
        var live = entities.Where(e => e.ReviewStatus != ReviewStatus.Rejected).ToList();

        foreach (var type in buildingTypes.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
        {
            var items = live.Where(e => e.BuildingTypeId == type.BuildingTypeId).ToList();
            var lines = BuildLines(items, type.Multiplier, type.Confirmed);
            blocks.Add(new CountBlock
            {
                BuildingTypeId = type.BuildingTypeId,
                Name = type.Name,
                Kind = type.Kind,
                Multiplier = type.Multiplier,
                MultiplierSource = type.MultiplierSource,
                MultiplierEvidence = type.MultiplierEvidence,
                MultiplierConfirmed = type.Confirmed,
                Lines = lines
            });
        }

        var orphans = live.Where(e => e.BuildingTypeId is null || !types.ContainsKey(e.BuildingTypeId.Value)).ToList();
        if (orphans.Count > 0)
        {
            // Counted once. Having no building type is itself something to fix, so the block says so.
            blocks.Add(new CountBlock
            {
                Name = UnassignedName,
                Multiplier = 1,
                MultiplierConfirmed = true,
                Lines = BuildLines(orphans, 1, confirmed: true)
            });
        }

        foreach (var block in blocks)
        {
            foreach (var line in block.Lines)
            {
                if (line.State == ExtendedState.ItemUndetermined)
                    open.Add(new OpenItem(block.Name, line.Type, line.Tag, line.Size, line.QuantityPerBuilding, line.Unit, line.Disposition,
                        "Undetermined: needs a decision before it can be counted."));
            }
        }

        var unconfirmed = blocks
            .Where(b => !b.IsUnassigned && !b.MultiplierConfirmed && b.Lines.Any(l => l.State == ExtendedState.MultiplierUnconfirmed))
            .Select(b => b.Name)
            .ToList();

        return new CountSheet
        {
            JobId = jobId,
            Blocks = blocks,
            Totals = BuildTotals(blocks),
            OpenItems = open,
            UnconfirmedBuildingTypes = unconfirmed
        };
    }

    private static List<CountLine> BuildLines(List<EntityRecord> items, int? multiplier, bool confirmed)
    {
        return items
            .GroupBy(e => new LineKey(Norm(e.Category), Norm(e.Tag), e.Type.Trim().ToLowerInvariant(), Norm(e.Size), Norm(e.Manufacturer), Norm(e.Model),
                Norm(e.CustomerPartCode), e.Unit, e.Disposition))
            .Select(g =>
            {
                var first = g.First();
                var perBuilding = g.Sum(e => e.Quantity);
                var counted = first.Disposition == Disposition.Included;
                var (state, extended) = counted
                    ? Extend(perBuilding, multiplier, confirmed)
                    : (first.Disposition == Disposition.TBD ? ExtendedState.ItemUndetermined : ExtendedState.NotCounted, (decimal?)null);

                return new CountLine
                {
                    Category = first.Category,
                    Tag = first.Tag,
                    Type = first.Type,
                    Size = first.Size,
                    Manufacturer = first.Manufacturer,
                    Model = first.Model,
                    CustomerPartCode = first.CustomerPartCode,
                    Unit = first.Unit,
                    Disposition = first.Disposition,
                    QuantityPerBuilding = perBuilding,
                    Multiplier = multiplier,
                    ExtendedQuantity = extended,
                    State = state,
                    EntityCount = g.Count(),
                    VerifiedCount = g.Count(e => e.ReviewStatus is ReviewStatus.Verified or ReviewStatus.Manual),
                    ProposedCount = g.Count(e => e.ReviewStatus == ReviewStatus.Proposed),
                    StaleCount = g.Count(e => e.IsStale)
                };
            })
            .OrderBy(l => l.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.Size, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.Tag, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.Unit)
            .ThenBy(l => l.Disposition)
            .ToList();
    }

    private static (ExtendedState, decimal?) Extend(decimal perBuilding, int? multiplier, bool confirmed) =>
        confirmed && multiplier is { } m
            ? (ExtendedState.Known, perBuilding * m)
            : (ExtendedState.MultiplierUnconfirmed, null);

    private static List<TotalLine> BuildTotals(List<CountBlock> blocks)
    {
        var rows = blocks.SelectMany(b => b.Lines.Where(l => l.Disposition == Disposition.Included).Select(l => (Block: b, Line: l)));

        return rows
            .GroupBy(r => new LineKey(Norm(r.Line.Category), Norm(r.Line.Tag), r.Line.Type.Trim().ToLowerInvariant(), Norm(r.Line.Size), Norm(r.Line.Manufacturer),
                Norm(r.Line.Model), Norm(r.Line.CustomerPartCode), r.Line.Unit, Disposition.Included))
            .Select(g =>
            {
                var first = g.First().Line;
                var pending = g.Where(x => x.Line.State != ExtendedState.Known).Select(x => x.Block.Name).Distinct().Order().ToList();
                var known = g.Where(x => x.Line.State == ExtendedState.Known).Sum(x => x.Line.ExtendedQuantity!.Value);
                return new TotalLine
                {
                    Category = first.Category,
                    Tag = first.Tag,
                    Type = first.Type,
                    Size = first.Size,
                    Manufacturer = first.Manufacturer,
                    Model = first.Model,
                    CustomerPartCode = first.CustomerPartCode,
                    Unit = first.Unit,
                    KnownPart = known,
                    Total = pending.Count == 0 ? known : null,
                    PendingBuildingTypes = pending,
                    ProposedCount = g.Sum(x => x.Line.ProposedCount)
                };
            })
            .OrderBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Size, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Tag, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Unit)
            .ToList();
    }

    private static string Norm(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    private readonly record struct LineKey(string Category, string Tag, string Type, string Size, string Manufacturer, string Model, string PartCode, QuantityUnit Unit, Disposition Disposition);
}

public sealed class CountSheetRepository
{
    private readonly Func<CancellationToken, Task<SqlConnection>> _open;

    public CountSheetRepository(Func<CancellationToken, Task<SqlConnection>> openConnection) => _open = openConnection;

    public CountSheetRepository(string connectionString)
        : this(async ct =>
        {
            var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);
            return connection;
        })
    {
    }

    /// <summary>Reads the building types and entities of one job in one transaction and builds the sheet.</summary>
    public async Task<CountSheet> BuildAsync(long jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        // Repeatable read, so a change made between the two reads cannot give a sheet that matches neither state.
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var types = await BuildingTypeRepository.ListAsync(connection, tx, jobId, cancellationToken);
        var entities = await EntityRepository.ListAsync(connection, tx, jobId, new EntityFilter(), cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return CountSheetBuilder.Build(jobId, types, entities);
    }
}
