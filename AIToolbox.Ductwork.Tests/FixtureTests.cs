using System.Text;
using System.Text.RegularExpressions;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Tests.Fixtures;

namespace AIToolbox.Ductwork.Tests;

public partial class FixtureTests
{
    [Fact]
    public void Same_seed_gives_identical_bytes_and_a_different_seed_does_not()
    {
        var a = FixtureGenerator.ResidentialLabels(seed: 7);
        var b = FixtureGenerator.ResidentialLabels(seed: 7);
        var c = FixtureGenerator.ResidentialLabels(seed: 8);

        for (var i = 0; i < a.Pages.Count; i++)
            Assert.Equal(a.Pages[i].Pdf, b.Pages[i].Pdf);
        Assert.NotEqual(a.Pages[0].Pdf, c.Pages[0].Pdf);
    }

    [Fact]
    public void Pdf_cross_reference_table_points_at_the_objects()
    {
        foreach (var page in FixtureGenerator.ResidentialLabels().Pages)
        {
            var text = Encoding.Latin1.GetString(page.Pdf);
            Assert.StartsWith("%PDF-1.4", text);
            Assert.EndsWith("%%EOF\n", text);

            var startxref = long.Parse(StartXref().Match(text).Groups[1].Value);
            Assert.StartsWith("xref", text[(int)startxref..]);

            var entries = XrefEntry().Matches(text[(int)startxref..]).Select(m => long.Parse(m.Groups[1].Value)).ToList();
            for (var obj = 1; obj <= 5; obj++)
                Assert.StartsWith($"{obj} 0 obj", text[(int)entries[obj - 1]..]);
        }
    }

    [Fact]
    public void Each_labeled_item_is_one_text_object_and_the_gold_matches()
    {
        var set = FixtureGenerator.ResidentialLabels();
        foreach (var page in set.Pages)
        {
            var text = Encoding.Latin1.GetString(page.Pdf);
            var shown = TextShow().Matches(text).Select(m => m.Groups[1].Value).ToList();

            foreach (var size in page.Gold.GroupBy(g => g.Size))
                Assert.Equal(size.Count(), shown.Count(s => s == size.Key));

            // Room names contain digits but are not items.
            Assert.Contains("BEDROOM 2", shown);
            Assert.DoesNotContain(page.Gold, g => g.Size == "BEDROOM 2");
        }
    }

    [Fact]
    public void Outlined_text_pages_have_no_text_objects_at_all()
    {
        var set = FixtureGenerator.ResidentialLabels(outlinedText: true);
        foreach (var page in set.Pages)
        {
            Assert.False(page.HasText);
            var text = Encoding.Latin1.GetString(page.Pdf);
            Assert.DoesNotContain(" Tj", text);
            Assert.DoesNotContain("BT /F1", text);
            Assert.NotEmpty(page.Gold);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Canonical_boxes_stay_inside_the_displayed_page_for_every_rotation(int rotate)
    {
        var pdf = new SyntheticPdf(1728, 1224, rotate);
        var (w, h) = pdf.DisplayedSize;
        var box = pdf.ToCanonical(pdf.Text(100, 200, "4x10x7", 9));

        Assert.InRange(box.X0, 0, w);
        Assert.InRange(box.X1, 0, w);
        Assert.InRange(box.Y0, 0, h);
        Assert.InRange(box.Y1, 0, h);
        Assert.True(box.X1 > box.X0 && box.Y1 > box.Y0);
    }

    [Fact]
    public void Rotation_maps_the_top_left_corner_as_a_clockwise_turn_would()
    {
        // A point at the top-left of an unrotated page goes to the top-right after a 90 degree
        // clockwise turn, the bottom-right after 180, and the bottom-left after 270.
        var corner = new BoundingBox(0, 0, 1, 1);
        var p90 = new SyntheticPdf(1000, 600, 90).ToCanonical(corner);
        var p180 = new SyntheticPdf(1000, 600, 180).ToCanonical(corner);
        var p270 = new SyntheticPdf(1000, 600, 270).ToCanonical(corner);

        Assert.True(p90.X0 > 590 && p90.Y0 < 1);   // displayed 600 wide, so top-right
        Assert.True(p180.X0 > 990 && p180.Y0 > 590);
        Assert.True(p270.X0 < 1 && p270.Y0 > 990);
    }

    [Fact]
    public void Canonical_rotation_preserves_box_area()
    {
        foreach (var rotate in new[] { 0, 90, 180, 270 })
        {
            var pdf = new SyntheticPdf(1000, 600, rotate);
            var b = new BoundingBox(10, 20, 60, 40);
            Assert.Equal(b.Area, pdf.ToCanonical(b).Area, 6);
        }
    }

    /// <summary>
    /// Writes the fixtures to the directory named by DUCTWORK_FIXTURE_OUT so an external reader can
    /// check them independently of this code. Does nothing when the variable is not set.
    /// </summary>
    [Fact]
    public void Fixtures_are_written_for_external_checking_when_requested()
    {
        var dir = Environment.GetEnvironmentVariable("DUCTWORK_FIXTURE_OUT");
        if (string.IsNullOrWhiteSpace(dir)) return;

        FixtureGenerator.ResidentialLabels(seed: 1).WriteTo(dir);
        FixtureGenerator.ResidentialLabels(seed: 1, outlinedText: true).WriteTo(dir);
        Assert.NotEmpty(Directory.GetFiles(dir, "*.pdf"));
    }

    [GeneratedRegex(@"startxref\n(\d+)\n")]
    private static partial Regex StartXref();

    [GeneratedRegex(@"(\d{10}) 00000 n")]
    private static partial Regex XrefEntry();

    [GeneratedRegex(@"\(([^)]*)\) Tj")]
    private static partial Regex TextShow();
}
