using System.Globalization;
using System.Text;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace AIToolbox.Ductwork.Core.Reports;

/// <summary>
/// The count sheet as a PDF: the same sheet, totals, holds, rules and time as the Excel and web page files, read from the same model and worded by the
/// same <see cref="ReportText"/>. Landscape Letter, header rows repeat on every page, and each page says which report it belongs to and that it is
/// page n of m, so a loose page can be traced back. Text from the drawings is only ever drawn as text.
/// </summary>
public sealed class PdfReportRenderer : IReportRenderer
{
    public string Format => "pdf";
    public string ContentType => "application/pdf";
    public string Extension => "pdf";

    private const string FontName = "Arial";
    private const double BodyPoints = 7.5;

    private static readonly object FontLock = new();
    private static bool _fontsReady;

    /// <summary>
    /// The fonts come from the machine the app runs on (the web server is Windows). The setting is process-wide and is only ever turned on, so
    /// it is set once; on a machine without the font the renderer fails with a message that names it rather than drawing blanks.
    /// </summary>
    private static void EnsureFonts()
    {
        lock (FontLock)
        {
            if (_fontsReady) return;
            if (OperatingSystem.IsWindows()) GlobalFontSettings.UseWindowsFontsUnderWindows = true;
            _fontsReady = true;
        }
    }

    public byte[] Render(ReportModel model, ReportStamp stamp)
    {
        EnsureFonts();
        var j = model.Job;
        var doc = new Document();
        doc.Info.Title = $"Count sheet: {Clean(j.Name)}";
        doc.Info.Author = Clean(stamp.GeneratedBy);
        doc.Info.Subject = ReportText.Kind(stamp);
        Styles(doc);

        var section = doc.AddSection();
        var page = section.PageSetup;
        page.PageFormat = PageFormat.Letter;
        page.Orientation = Orientation.Landscape;
        page.LeftMargin = page.RightMargin = Unit.FromMillimeter(10);
        page.TopMargin = Unit.FromMillimeter(12);
        page.BottomMargin = Unit.FromMillimeter(14);
        // PageSetup reports no width until the page is rendered, so the usable width is worked out from the paper: Letter landscape is 11 inches wide.
        var width = Unit.FromInch(11).Point - page.LeftMargin.Point - page.RightMargin.Point;
        Footer(section, model, stamp);

        section.AddParagraph($"Ductwork count sheet: {Clean(j.Name)}", "Title");
        var banner = section.AddParagraph(Clean(ReportText.Banner(model, stamp)), "Banner");
        banner.Format.Font.Color = model.IsFinal ? new Color(20, 108, 46) : new Color(170, 17, 17);
        var where = string.Join(" · ", new[] { j.CustomerName, j.ProjectName, j.SiteAddress }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(Clean));
        if (where.Length > 0) section.AddParagraph(where, "Muted");
        section.AddParagraph(
            $"Owner {Clean(j.OwnerEmail)} · job {Clean(j.State)} · completion version {model.ForCompletionVersion} · {ReportText.Kind(stamp)} · generated {stamp.GeneratedUtc:yyyy-MM-dd HH:mm} UTC by {Clean(stamp.GeneratedBy)}", "Muted");

        Heading(section, "Count sheet, by building type");
        var lineTable = NewTable(section, width, [8, 5, 14, 8, 8, 8, 7, 6, 4, 5, 6, 12, 9],
            ["Category", "Tag", "Description", "Size", "Manufacturer", "Model", "Part code", "Qty per building", "Unit", "Multiplier", "Extended", "Status", "Notes"], numeric: [7, 9, 10]);
        foreach (var block in model.Sheet.Blocks)
        {
            var label = Clean(block.IsUnassigned ? block.Name + " (counted once; assign these to a building type)" : $"{block.Name}: multiplier {ReportText.Multiplier(block)}");
            var blockRow = lineTable.AddRow();
            blockRow.Shading.Color = new Color(244, 246, 248);
            blockRow.Cells[0].MergeRight = 12;
            blockRow.Cells[0].AddParagraph(label).Format.Font.Bold = true;
            foreach (var l in block.Lines)
                Row(lineTable, [l.Category, l.Tag, l.Type, l.Size, l.Manufacturer, l.Model, l.CustomerPartCode, ReportText.Qty(l.QuantityPerBuilding), l.Unit.ToString(),
                    block.IsUnassigned ? "" : block.Multiplier?.ToString(CultureInfo.InvariantCulture) ?? "TBD", ReportText.Extended(l), ReportText.Status(l), ReportText.Notes(l)], [7, 9, 10]);
        }

        Heading(section, "Job totals");
        var totals = NewTable(section, width, [9, 6, 16, 9, 9, 9, 8, 5, 9, 12, 6],
            ["Category", "Tag", "Description", "Size", "Manufacturer", "Model", "Part code", "Unit", "Total", "Held up by", "Not yet verified"], numeric: [8, 10]);
        foreach (var t in model.Sheet.Totals)
            Row(totals, [t.Category, t.Tag, t.Type, t.Size, t.Manufacturer, t.Model, t.CustomerPartCode, t.Unit.ToString(), ReportText.Total(t), string.Join(", ", t.PendingBuildingTypes), t.ProposedCount.ToString(CultureInfo.InvariantCulture)], [8, 10]);

        Heading(section, "Holds");
        if (model.Sheet.UnconfirmedBuildingTypes.Count == 0 && model.Sheet.OpenItems.Count == 0 && model.Sheet.OpenFindings.Count == 0) section.AddParagraph("None.");
        else
        {
            foreach (var b in model.Sheet.UnconfirmedBuildingTypes) Bullet(section, $"Building type {b} has no confirmed multiplier.");
            foreach (var o in model.Sheet.OpenItems) Bullet(section, $"{o.BuildingType}: {o.Type} {o.Tag} {o.Size} ({ReportText.Qty(o.Quantity)} {o.Unit}): {o.Reason}");
            foreach (var f in model.Sheet.OpenFindings) Bullet(section, $"Question{(f.Item is null ? "" : $" about {f.Item}")}: {f.Question}");
        }

        Heading(section, "Rules applied");
        if (model.Rules.Count == 0) section.AddParagraph("No interpretation rules apply to this job.");
        else
        {
            var rules = NewTable(section, width, [16, 5, 10, 8, 24, 24, 13], ["Rule", "Version", "Applies to", "Status", "Matches", "Does", "Approved by"], numeric: []);
            foreach (var r in model.Rules) Row(rules, [r.Name, r.Version.ToString(CultureInfo.InvariantCulture), r.Scope, r.Status, r.Match, r.Action, r.ApprovedBy], []);
        }

        Heading(section, "Time on this job");
        section.AddParagraph($"{model.Metrics.ActiveHours:0.00} active hours and {model.Metrics.IdleHours:0.00} idle hours over {model.Metrics.Sessions} session(s), all people together.");
        section.AddParagraph("This tool counts what is on the drawings. It produces no quote, pricing, waste allowance or order quantity.", "Muted");

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var stream = new MemoryStream();
        renderer.PdfDocument.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    private static void Styles(Document doc)
    {
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = FontName;
        normal.Font.Size = 9;
        normal.Font.Color = new Color(27, 36, 48);

        var title = doc.Styles.AddStyle("Title", StyleNames.Normal);
        title.Font.Size = 17;
        title.Font.Bold = true;
        title.ParagraphFormat.SpaceAfter = 2;

        var banner = doc.Styles.AddStyle("Banner", StyleNames.Normal);
        banner.Font.Bold = true;
        banner.ParagraphFormat.SpaceAfter = 3;

        var muted = doc.Styles.AddStyle("Muted", StyleNames.Normal);
        muted.Font.Size = 8;
        muted.Font.Color = new Color(91, 103, 115);
        muted.ParagraphFormat.SpaceAfter = 2;

        var heading = doc.Styles.AddStyle("Heading", StyleNames.Normal);
        heading.Font.Size = 12;
        heading.Font.Bold = true;
        heading.ParagraphFormat.SpaceBefore = 12;
        heading.ParagraphFormat.SpaceAfter = 4;
        heading.ParagraphFormat.KeepWithNext = true;

        var cell = doc.Styles.AddStyle("Cell", StyleNames.Normal);
        cell.Font.Size = BodyPoints;
    }

    private static void Footer(Section section, ReportModel model, ReportStamp stamp)
    {
        var footer = section.Footers.Primary.AddParagraph();
        footer.Style = "Muted";
        footer.AddText($"{Clean(model.Job.Name)} · {(stamp.IsIssued ? "Issued" : "Draft")} · completion version {model.ForCompletionVersion} · {(model.IsFinal ? "final" : "NOT FINAL")} · page ");
        footer.AddPageField();
        footer.AddText(" of ");
        footer.AddNumPagesField();
    }

    private static void Heading(Section section, string text) => section.AddParagraph(text, "Heading");

    private static void Bullet(Section section, string text)
    {
        var p = section.AddParagraph("• " + Clean(text));
        p.Format.LeftIndent = Unit.FromMillimeter(4);
        p.Format.FirstLineIndent = Unit.FromMillimeter(-3);
    }

    /// <summary>A table whose columns share the page width in the proportions given, with a header that repeats on every page.</summary>
    private static Table NewTable(Section section, double totalWidth, int[] proportions, string[] headers, int[] numeric)
    {
        var table = section.AddTable();
        table.Style = "Cell";
        table.Borders.Width = 0.25;
        table.Borders.Color = new Color(201, 209, 218);
        table.Rows.LeftIndent = 0;
        table.TopPadding = table.BottomPadding = 1.5;
        table.LeftPadding = table.RightPadding = 2.5;
        var sum = proportions.Sum();
        foreach (var p in proportions) table.AddColumn(Unit.FromPoint(totalWidth * p / sum));
        var header = table.AddRow();
        header.HeadingFormat = true;
        header.Shading.Color = new Color(232, 238, 244);
        header.Format.Font.Bold = true;
        for (var i = 0; i < headers.Length; i++)
        {
            header.Cells[i].AddParagraph(headers[i]);
            if (numeric.Contains(i)) header.Cells[i].Format.Alignment = ParagraphAlignment.Right;
        }

        return table;
    }

    private static void Row(Table table, string?[] values, int[] numeric)
    {
        var row = table.AddRow();
        for (var i = 0; i < values.Length; i++)
        {
            row.Cells[i].AddParagraph(Clean(values[i]));
            if (numeric.Contains(i)) row.Cells[i].Format.Alignment = ParagraphAlignment.Right;
        }
    }

    /// <summary>Control characters from a drawing have no place in a page (and some break the font layer), so they become spaces; everything else is drawn as it was read.</summary>
    private static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text) sb.Append(char.IsControl(ch) ? ' ' : ch);
        return sb.ToString();
    }
}
