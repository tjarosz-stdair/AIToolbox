using System.Text.Json;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Evaluation;

namespace AIToolbox.Ductwork.Tests.Fixtures;

public sealed record FixturePage(string BuildingType, byte[] Pdf, IReadOnlyList<GoldItem> Gold, int Rotate, bool HasText);

public sealed record FixtureSet(string Name, IReadOnlyList<FixturePage> Pages)
{
    public IReadOnlyList<GoldItem> AllGold => Pages.SelectMany(p => p.Gold).ToList();

    /// <summary>Writes each page as a PDF with a ground-truth JSON beside it, for inspection or for external checkers.</summary>
    public void WriteTo(string directory)
    {
        Directory.CreateDirectory(directory);
        for (var i = 0; i < Pages.Count; i++)
        {
            var page = Pages[i];
            var stem = Path.Combine(directory, $"{Name}-{i + 1:D2}-{page.BuildingType.Replace(' ', '_')}-rot{page.Rotate}{(page.HasText ? "" : "-notext")}");
            File.WriteAllBytes(stem + ".pdf", page.Pdf);
            File.WriteAllText(stem + ".gold.json", JsonSerializer.Serialize(page.Gold, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}

/// <summary>
/// Deterministic synthetic sets that look like a residential CAD export: duct runs as paired lines,
/// a title block, and tagged terminals whose labels are the detectable items. Each page carries the
/// ground truth, in canonical space, for exactly what was drawn. No customer data is involved, so
/// these are safe to commit and to run anywhere.
/// </summary>
public static class FixtureGenerator
{
    private static readonly string[] BootSizes = ["4x10x7", "4x12x7", "6x10x7", "4x10x4"];

    /// <summary>
    /// Several building types, each with its own mix of labeled boots, at different rotations. The
    /// counts differ by building type so that scoring per building type is meaningful.
    /// </summary>
    public static FixtureSet ResidentialLabels(int seed = 1, bool outlinedText = false)
    {
        var rng = new Lcg(seed);
        var pages = new List<FixturePage>();
        var plans = new (string Building, int Rotate, int[] Counts)[]
        {
            ("Type 1", 0, [6, 2, 0, 1]),
            ("Type 2", 90, [4, 4, 2, 0]),
            ("Type 3", 180, [8, 0, 1, 2]),
            ("Type 4", 270, [3, 3, 3, 3])
        };

        foreach (var (building, rotate, counts) in plans)
        {
            var pdf = new SyntheticPdf(1728, 1224, rotate); // 24 x 17 in at 72 pt
            var gold = new List<GoldItem>();

            DrawDistractors(pdf, rng, outlinedText);

            for (var s = 0; s < BootSizes.Length; s++)
            {
                for (var n = 0; n < counts[s]; n++)
                {
                    var x = 150 + rng.Next(1400);
                    var y = 150 + rng.Next(850);
                    var box = outlinedText ? pdf.OutlinedText(x, y, BootSizes[s], 9) : pdf.Text(x, y, BootSizes[s], 9);
                    gold.Add(new GoldItem
                    {
                        BuildingType = building,
                        Type = "Boot",
                        Size = BootSizes[s],
                        Quantity = 1,
                        Box = pdf.ToCanonical(box)
                    });
                }
            }

            pages.Add(new FixturePage(building, pdf.Build(), gold, rotate, HasText: !outlinedText));
        }

        return new FixtureSet(outlinedText ? "residential-notext" : "residential", pages);
    }

    private static void Label(SyntheticPdf pdf, bool outlined, double x, double y, string text, double size)
    {
        if (outlined) pdf.OutlinedText(x, y, text, size);
        else pdf.Text(x, y, text, size);
    }

    private static void DrawDistractors(SyntheticPdf pdf, Lcg rng, bool outlined)
    {
        // Title block and border.
        pdf.Rect(30, 30, pdf.Width - 30, pdf.Height - 30);
        pdf.Rect(pdf.Width - 330, pdf.Height - 150, pdf.Width - 30, pdf.Height - 30);
        Label(pdf, outlined, pdf.Width - 320, pdf.Height - 140, "MECHANICAL FIRST FLOOR PLAN", 12);

        // Paired-line duct runs that are not labeled items.
        for (var i = 0; i < 12; i++)
        {
            var x = 100 + rng.Next(1300);
            var y = 120 + rng.Next(900);
            var length = 100 + rng.Next(300);
            pdf.Line(x, y, x + length, y);
            pdf.Line(x, y + 12, x + length, y + 12);
        }

        // Room-name text that contains digits (BEDROOM 2) and must never be counted as an item.
        Label(pdf, outlined, 200, 140, "BEDROOM 2", 10);
        Label(pdf, outlined, 900, 600, "BATH 2", 10);
    }

    /// <summary>
    /// A deliberately imperfect detector over a fixture set, for exercising the harness: it misses a
    /// fraction of items, invents a fraction, and gives correct detections higher confidence on average.
    /// </summary>
    public static IReadOnlyList<DetectionProposal> NoisyDetections(
        IReadOnlyList<GoldItem> gold, int seed, double missRate, double falsePositiveRate)
    {
        var rng = new Lcg(seed);
        var result = new List<DetectionProposal>();
        var n = 0;

        foreach (var item in gold)
        {
            if (rng.NextDouble() < missRate) continue;
            result.Add(Make(item, 0.75 + rng.NextDouble() * 0.25, $"d{n++}"));

            if (rng.NextDouble() < falsePositiveRate)
            {
                // A bogus nearby item of the same type: low confidence, no overlap with any real one.
                var bogus = item with
                {
                    Box = new BoundingBox(5000 + n * 30, 5000, 5020 + n * 30, 5010)
                };
                result.Add(Make(bogus, 0.2 + rng.NextDouble() * 0.45, $"d{n++}"));
            }
        }

        return result;
    }

    public static DetectionProposal Make(GoldItem item, double confidence, string id) => new()
    {
        DetectionId = id,
        Type = item.Type,
        Category = item.Category,
        Size = item.Size,
        BuildingType = item.BuildingType,
        Quantity = item.Quantity,
        Confidence = confidence,
        Method = DetectionMethod.Text,
        Family = DetectorFamily.LabelFlagCount,
        Box = item.Box
    };

    /// <summary>A tiny deterministic generator, so fixtures never change with the runtime's Random implementation.</summary>
    private sealed class Lcg(int seed)
    {
        private ulong _state = (ulong)seed * 6364136223846793005UL + 1442695040888963407UL;

        public int Next(int maxExclusive)
        {
            _state = _state * 6364136223846793005UL + 1442695040888963407UL;
            return (int)((_state >> 33) % (ulong)maxExclusive);
        }

        public double NextDouble() => Next(1_000_000) / 1_000_000.0;
    }
}
