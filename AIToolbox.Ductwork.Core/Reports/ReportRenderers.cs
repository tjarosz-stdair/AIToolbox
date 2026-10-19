using System.Globalization;
using System.Net;
using System.Text;
using AIToolbox.Ductwork.Core.Counting;
using ClosedXML.Excel;

namespace AIToolbox.Ductwork.Core.Reports;

/// <summary>The words every renderer uses for the same thing, so the Excel file and the HTML file say it identically.</summary>
internal static class ReportText
{
    public static string Qty(decimal value) => value == Math.Truncate(value) ? value.ToString("0", CultureInfo.InvariantCulture) : value.ToString("0.##", CultureInfo.InvariantCulture);

    public static string Extended(CountLine line) => line.State switch
    {
        ExtendedState.Known => Qty(line.ExtendedQuantity ?? 0),
        ExtendedState.NotCounted => "not counted",
        _ => "TBD"
    };

    public static string Status(CountLine line)
    {
        var parts = new List<string> { line.Disposition switch { Contracts.Disposition.FactoryFurnished => "Factory furnished", var d => d.ToString() } };
        if (line.EntityCount > 0) parts.Add(line.ProposedCount == 0 ? "verified" : $"{line.VerifiedCount} verified, {line.ProposedCount} not yet verified");
        return string.Join("; ", parts);
    }

    public static string Notes(CountLine line)
    {
        var notes = new List<string>();
        if (line.State == ExtendedState.MultiplierUnconfirmed) notes.Add("multiplier not confirmed");
        if (line.State == ExtendedState.ItemUndetermined) notes.Add("quantity undetermined");
        if (line.StaleCount > 0) notes.Add($"{line.StaleCount} item(s) may be out of date");
        return string.Join("; ", notes);
    }

    public static string Total(TotalLine total) => total.IsComplete ? Qty(total.Total!.Value) : $"TBD (at least {Qty(total.KnownPart)})";

    public static string Multiplier(CountBlock block) =>
        block.Multiplier is null ? "not set" : $"{block.Multiplier}{(block.MultiplierConfirmed ? "" : " (not confirmed)")}{(block.MultiplierSource is { } s ? $", {s}" : "")}";

    public static string Banner(ReportModel model, ReportStamp stamp) =>
        model.IsFinal ? "FINAL: nothing is open." : "NOT FINAL: " + string.Join(" ", model.NotFinalReasons);

    public static string Kind(ReportStamp stamp) => stamp.IsIssued ? "Issued report" : "Draft (not issued)";
}

/// <summary>
/// The count sheet as a workbook: Summary, Count sheet (by building type), Job totals, Holds, Detail, Rules applied. Quantities are written as
/// numbers and everything else as text; text from the drawings is never allowed to become a formula.
/// </summary>
public sealed class ExcelReportRenderer : IReportRenderer
{
    public string Format => "xlsx";
    public string ContentType => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public string Extension => "xlsx";

    public byte[] Render(ReportModel model, ReportStamp stamp)
    {
        using var book = new XLWorkbook();
        Summary(book.AddWorksheet("Summary"), model, stamp);
        CountSheetPage(book.AddWorksheet("Count sheet"), model);
        Totals(book.AddWorksheet("Job totals"), model);
        Holds(book.AddWorksheet("Holds"), model);
        Detail(book.AddWorksheet("Detail"), model);
        Rules(book.AddWorksheet("Rules applied"), model);

        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>Text, always. A value that starts like a formula is marked as text so a spreadsheet will not run it.</summary>
    private static void T(IXLCell cell, string? text)
    {
        var value = text ?? "";
        cell.SetValue(value);
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') cell.Style.IncludeQuotePrefix = true;
    }

    private static void N(IXLCell cell, decimal value)
    {
        cell.SetValue(value);
        cell.Style.NumberFormat.Format = value == Math.Truncate(value) ? "#,##0" : "#,##0.##";
    }

    private static void Header(IXLWorksheet sheet, int row, params string[] titles)
    {
        for (var i = 0; i < titles.Length; i++)
        {
            var cell = sheet.Cell(row, i + 1);
            T(cell, titles[i]);
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EEF4");
        }
    }

    private static void Finish(IXLWorksheet sheet, int headerRow, int columns)
    {
        sheet.SheetView.FreezeRows(headerRow);
        sheet.Columns(1, columns).AdjustToContents(headerRow, Math.Min(headerRow + 200, sheet.LastRowUsed()?.RowNumber() ?? headerRow));
        foreach (var column in sheet.Columns(1, columns)) if (column.Width > 60) column.Width = 60;
        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sheet.PageSetup.FitToPages(1, 0);
        sheet.PageSetup.SetRowsToRepeatAtTop(headerRow, headerRow);
    }

    private static void Summary(IXLWorksheet sheet, ReportModel model, ReportStamp stamp)
    {
        var j = model.Job;
        var r = 1;
        T(sheet.Cell(r, 1), $"Ductwork count sheet: {j.Name}");
        sheet.Cell(r++, 1).Style.Font.SetBold().Font.SetFontSize(14);
        T(sheet.Cell(r, 1), ReportText.Banner(model, stamp));
        sheet.Cell(r, 1).Style.Font.SetBold();
        sheet.Cell(r, 1).Style.Font.FontColor = model.IsFinal ? XLColor.DarkGreen : XLColor.DarkRed;
        r += 2;

        void Row(string label, string? value)
        {
            T(sheet.Cell(r, 1), label);
            sheet.Cell(r, 1).Style.Font.Bold = true;
            T(sheet.Cell(r, 2), value);
            r++;
        }

        Row("Customer", j.CustomerName);
        Row("Project", j.ProjectName);
        Row("Site", j.SiteAddress);
        Row("Owner", j.OwnerEmail);
        Row("Job state", j.State);
        Row("Completion version", model.ForCompletionVersion.ToString(CultureInfo.InvariantCulture));
        Row("This file", ReportText.Kind(stamp));
        Row("Generated", $"{stamp.GeneratedUtc:yyyy-MM-dd HH:mm} UTC by {stamp.GeneratedBy}");
        r++;
        T(sheet.Cell(r, 1), "Time on this job (all people together)");
        sheet.Cell(r++, 1).Style.Font.Bold = true;
        Row("Active time (hours)", model.Metrics.ActiveHours.ToString("0.00", CultureInfo.InvariantCulture));
        Row("Idle time (hours)", model.Metrics.IdleHours.ToString("0.00", CultureInfo.InvariantCulture));
        Row("Sessions", model.Metrics.Sessions.ToString(CultureInfo.InvariantCulture));
        foreach (var (step, hours) in model.Metrics.ActiveHoursByStep.OrderBy(s => s.Key)) Row($"  {step} (hours)", hours.ToString("0.00", CultureInfo.InvariantCulture));
        if (j.BaselineManualHours is { } baseline) Row("Estimated manual time (hours)", baseline.ToString("0.##", CultureInfo.InvariantCulture));
        r++;
        T(sheet.Cell(r, 1), "This tool counts what is on the drawings. It produces no quote, pricing, waste allowance or order quantity.");
        sheet.Cell(r, 1).Style.Font.Italic = true;
        sheet.Column(1).Width = 34;
        sheet.Column(2).Width = 70;
    }

    private static void CountSheetPage(IXLWorksheet sheet, ReportModel model)
    {
        string[] titles = ["Building type", "Category", "Tag", "Description", "Size", "Manufacturer", "Model", "Customer part code", "Quantity per building", "Unit", "Multiplier", "Extended quantity", "Status", "Notes"];
        Header(sheet, 1, titles);
        var r = 2;
        foreach (var block in model.Sheet.Blocks)
        {
            var name = block.IsUnassigned ? $"{block.Name} (counted once; assign these to a building type)" : block.Name;
            T(sheet.Cell(r, 1), name);
            T(sheet.Cell(r, 11), block.IsUnassigned ? "" : ReportText.Multiplier(block));
            if (!block.IsUnassigned && block.MultiplierEvidence is { Length: > 0 } evidence) T(sheet.Cell(r, 14), evidence);
            sheet.Range(r, 1, r, titles.Length).Style.Font.Bold = true;
            sheet.Range(r, 1, r, titles.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#F4F6F8");
            r++;

            foreach (var line in block.Lines)
            {
                T(sheet.Cell(r, 1), block.Name);
                T(sheet.Cell(r, 2), line.Category);
                T(sheet.Cell(r, 3), line.Tag);
                T(sheet.Cell(r, 4), line.Type);
                T(sheet.Cell(r, 5), line.Size);
                T(sheet.Cell(r, 6), line.Manufacturer);
                T(sheet.Cell(r, 7), line.Model);
                T(sheet.Cell(r, 8), line.CustomerPartCode);
                N(sheet.Cell(r, 9), line.QuantityPerBuilding);
                T(sheet.Cell(r, 10), line.Unit.ToString());
                if (block.Multiplier is { } m && !block.IsUnassigned) sheet.Cell(r, 11).SetValue(m); else T(sheet.Cell(r, 11), block.IsUnassigned ? "" : "TBD");
                if (line.State == ExtendedState.Known) N(sheet.Cell(r, 12), line.ExtendedQuantity ?? 0); else T(sheet.Cell(r, 12), ReportText.Extended(line));
                T(sheet.Cell(r, 13), ReportText.Status(line));
                T(sheet.Cell(r, 14), ReportText.Notes(line));
                r++;
            }
        }

        Finish(sheet, 1, titles.Length);
    }

    private static void Totals(IXLWorksheet sheet, ReportModel model)
    {
        string[] titles = ["Category", "Tag", "Description", "Size", "Manufacturer", "Model", "Customer part code", "Unit", "Job total", "Held up by", "Not yet verified"];
        Header(sheet, 1, titles);
        var r = 2;
        foreach (var t in model.Sheet.Totals)
        {
            T(sheet.Cell(r, 1), t.Category);
            T(sheet.Cell(r, 2), t.Tag);
            T(sheet.Cell(r, 3), t.Type);
            T(sheet.Cell(r, 4), t.Size);
            T(sheet.Cell(r, 5), t.Manufacturer);
            T(sheet.Cell(r, 6), t.Model);
            T(sheet.Cell(r, 7), t.CustomerPartCode);
            T(sheet.Cell(r, 8), t.Unit.ToString());
            if (t.IsComplete) N(sheet.Cell(r, 9), t.Total!.Value); else T(sheet.Cell(r, 9), ReportText.Total(t));
            T(sheet.Cell(r, 10), string.Join(", ", t.PendingBuildingTypes));
            N(sheet.Cell(r, 11), t.ProposedCount);
            r++;
        }

        Finish(sheet, 1, titles.Length);
    }

    private static void Holds(IXLWorksheet sheet, ReportModel model)
    {
        var r = 1;
        void Section(string title, string[] titles, IEnumerable<string?[]> rows)
        {
            T(sheet.Cell(r, 1), title);
            sheet.Cell(r++, 1).Style.Font.SetBold().Font.SetFontSize(12);
            var list = rows.ToList();
            if (list.Count == 0) { T(sheet.Cell(r++, 1), "None."); r++; return; }
            Header(sheet, r++, titles);
            foreach (var row in list)
            {
                for (var i = 0; i < row.Length; i++) T(sheet.Cell(r, i + 1), row[i]);
                r++;
            }

            r++;
        }

        Section("Building types with no confirmed multiplier", ["Building type"], model.Sheet.UnconfirmedBuildingTypes.Select(b => new string?[] { b }));
        Section("Items that cannot be counted yet", ["Building type", "Description", "Tag", "Size", "Quantity", "Unit", "Disposition", "Why"],
            model.Sheet.OpenItems.Select(o => new string?[] { o.BuildingType, o.Type, o.Tag, o.Size, ReportText.Qty(o.Quantity), o.Unit.ToString(), o.Disposition.ToString(), o.Reason }));
        Section("Questions nobody has answered", ["Item", "Kind", "Question"], model.Sheet.OpenFindings.Select(f => new string?[] { f.Item, f.Kind.ToString(), f.Question }));
        Section("Listed but not counted (excluded or factory furnished)", ["Building type", "Description", "Tag", "Size", "Quantity per building", "Unit", "Disposition"],
            model.Sheet.Blocks.SelectMany(b => b.Lines.Where(l => l.State == ExtendedState.NotCounted).Select(l => new string?[] { b.Name, l.Type, l.Tag, l.Size, ReportText.Qty(l.QuantityPerBuilding), l.Unit.ToString(), l.Disposition.ToString() })));
        sheet.Column(1).Width = 30;
        sheet.Columns(2, 8).Width = 24;
        sheet.Column(8).Width = 60;
    }

    private static void Detail(IXLWorksheet sheet, ReportModel model)
    {
        string[] titles = ["Building type", "Description", "Category", "Tag", "Size", "Manufacturer", "Model", "Customer part code", "Quantity", "Unit", "Found by", "Confidence", "Review", "Disposition", "Resolved by", "May be out of date", "Reason", "Evidence", "View"];
        Header(sheet, 1, titles);
        var r = 2;
        foreach (var d in model.Details)
        {
            T(sheet.Cell(r, 1), d.BuildingType);
            T(sheet.Cell(r, 2), d.Type);
            T(sheet.Cell(r, 3), d.Category);
            T(sheet.Cell(r, 4), d.Tag);
            T(sheet.Cell(r, 5), d.Size);
            T(sheet.Cell(r, 6), d.Manufacturer);
            T(sheet.Cell(r, 7), d.Model);
            T(sheet.Cell(r, 8), d.CustomerPartCode);
            N(sheet.Cell(r, 9), d.Quantity);
            T(sheet.Cell(r, 10), d.Unit);
            T(sheet.Cell(r, 11), d.Method);
            sheet.Cell(r, 12).SetValue(Math.Round(d.Confidence, 3));
            sheet.Cell(r, 12).Style.NumberFormat.Format = "0.00";
            T(sheet.Cell(r, 13), d.ReviewStatus);
            T(sheet.Cell(r, 14), d.Disposition);
            T(sheet.Cell(r, 15), d.ResolutionSource);
            T(sheet.Cell(r, 16), d.IsStale ? "yes" : "");
            T(sheet.Cell(r, 17), d.DispositionReason);
            T(sheet.Cell(r, 18), d.Evidence);
            T(sheet.Cell(r, 19), d.ViewRef);
            r++;
        }

        Finish(sheet, 1, titles.Length);
        sheet.Range(1, 1, Math.Max(1, r - 1), titles.Length).SetAutoFilter();
    }

    private static void Rules(IXLWorksheet sheet, ReportModel model)
    {
        string[] titles = ["Rule", "Version", "Applies to", "Status", "What it matches", "What it does", "Why", "Approved by", "Approved"];
        Header(sheet, 1, titles);
        var r = 2;
        foreach (var rule in model.Rules)
        {
            T(sheet.Cell(r, 1), rule.Name);
            sheet.Cell(r, 2).SetValue(rule.Version);
            T(sheet.Cell(r, 3), rule.Scope);
            T(sheet.Cell(r, 4), rule.Status);
            T(sheet.Cell(r, 5), rule.Match);
            T(sheet.Cell(r, 6), rule.Action);
            T(sheet.Cell(r, 7), rule.Description);
            T(sheet.Cell(r, 8), rule.ApprovedBy);
            T(sheet.Cell(r, 9), rule.ApprovedUtc is { } t ? $"{t:yyyy-MM-dd}" : "");
            r++;
        }

        if (model.Rules.Count == 0) T(sheet.Cell(2, 1), "No interpretation rules apply to this job.");
        Finish(sheet, 1, titles.Length);
    }
}

/// <summary>The same sheet as one self-contained web page that prints cleanly (and can be printed to PDF from any browser). Everything from the drawings is encoded.</summary>
public sealed class HtmlReportRenderer : IReportRenderer
{
    public string Format => "html";
    public string ContentType => "text/html; charset=utf-8";
    public string Extension => "html";

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? "");

    public byte[] Render(ReportModel model, ReportStamp stamp)
    {
        var j = model.Job;
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">");
        sb.Append($"<title>Count sheet: {E(j.Name)}</title><style>");
        sb.Append("@page{size:landscape;margin:12mm}body{font:12px/1.4 Segoe UI,Arial,sans-serif;color:#1b2430;margin:16px}h1{font-size:20px;margin:0 0 4px}h2{font-size:15px;margin:22px 0 6px}");
        sb.Append("table{border-collapse:collapse;width:100%;margin-bottom:8px}th,td{border:1px solid #c9d1da;padding:3px 6px;text-align:left;vertical-align:top}th{background:#e8eef4}");
        sb.Append("td.n{text-align:right;font-variant-numeric:tabular-nums}tr.block td{background:#f4f6f8;font-weight:600}.final{color:#146c2e;font-weight:700}.notfinal{color:#a11;font-weight:700}.muted{color:#5b6773}");
        sb.Append("thead{display:table-header-group}tr{page-break-inside:avoid}</style></head><body>");

        sb.Append($"<h1>Ductwork count sheet: {E(j.Name)}</h1>");
        sb.Append($"<p class=\"{(model.IsFinal ? "final" : "notfinal")}\">{E(ReportText.Banner(model, stamp))}</p>");
        sb.Append("<p class=\"muted\">");
        sb.Append(E(string.Join(" · ", new[] { j.CustomerName, j.ProjectName, j.SiteAddress }.Where(s => !string.IsNullOrWhiteSpace(s)))));
        sb.Append($"<br>Owner {E(j.OwnerEmail)} · job {E(j.State)} · completion version {model.ForCompletionVersion} · {E(ReportText.Kind(stamp))} · generated {stamp.GeneratedUtc:yyyy-MM-dd HH:mm} UTC by {E(stamp.GeneratedBy)}</p>");

        sb.Append("<h2>Count sheet, by building type</h2><table><thead><tr>");
        foreach (var t in new[] { "Category", "Tag", "Description", "Size", "Manufacturer", "Model", "Part code", "Qty per building", "Unit", "Multiplier", "Extended", "Status", "Notes" }) sb.Append($"<th>{t}</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var block in model.Sheet.Blocks)
        {
            sb.Append($"<tr class=\"block\"><td colspan=\"13\">{E(block.IsUnassigned ? block.Name + " (counted once; assign these to a building type)" : $"{block.Name}: multiplier {ReportText.Multiplier(block)}")}</td></tr>");
            foreach (var l in block.Lines)
            {
                sb.Append($"<tr><td>{E(l.Category)}</td><td>{E(l.Tag)}</td><td>{E(l.Type)}</td><td>{E(l.Size)}</td><td>{E(l.Manufacturer)}</td><td>{E(l.Model)}</td><td>{E(l.CustomerPartCode)}</td>");
                sb.Append($"<td class=\"n\">{ReportText.Qty(l.QuantityPerBuilding)}</td><td>{l.Unit}</td><td class=\"n\">{(block.IsUnassigned ? "" : block.Multiplier?.ToString(CultureInfo.InvariantCulture) ?? "TBD")}</td>");
                sb.Append($"<td class=\"n\">{E(ReportText.Extended(l))}</td><td>{E(ReportText.Status(l))}</td><td>{E(ReportText.Notes(l))}</td></tr>");
            }
        }

        sb.Append("</tbody></table><h2>Job totals</h2><table><thead><tr>");
        foreach (var t in new[] { "Category", "Tag", "Description", "Size", "Manufacturer", "Model", "Part code", "Unit", "Total", "Held up by", "Not yet verified" }) sb.Append($"<th>{t}</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var t in model.Sheet.Totals)
            sb.Append($"<tr><td>{E(t.Category)}</td><td>{E(t.Tag)}</td><td>{E(t.Type)}</td><td>{E(t.Size)}</td><td>{E(t.Manufacturer)}</td><td>{E(t.Model)}</td><td>{E(t.CustomerPartCode)}</td><td>{t.Unit}</td><td class=\"n\">{E(ReportText.Total(t))}</td><td>{E(string.Join(", ", t.PendingBuildingTypes))}</td><td class=\"n\">{t.ProposedCount}</td></tr>");
        sb.Append("</tbody></table>");

        sb.Append("<h2>Holds</h2>");
        if (model.Sheet.UnconfirmedBuildingTypes.Count == 0 && model.Sheet.OpenItems.Count == 0 && model.Sheet.OpenFindings.Count == 0) sb.Append("<p>None.</p>");
        else
        {
            sb.Append("<ul>");
            foreach (var b in model.Sheet.UnconfirmedBuildingTypes) sb.Append($"<li>Building type <b>{E(b)}</b> has no confirmed multiplier.</li>");
            foreach (var o in model.Sheet.OpenItems) sb.Append($"<li>{E(o.BuildingType)}: {E(o.Type)} {E(o.Tag)} {E(o.Size)} ({ReportText.Qty(o.Quantity)} {o.Unit}): {E(o.Reason)}</li>");
            foreach (var f in model.Sheet.OpenFindings) sb.Append($"<li>Question{(f.Item is null ? "" : $" about {E(f.Item)}")}: {E(f.Question)}</li>");
            sb.Append("</ul>");
        }

        sb.Append("<h2>Rules applied</h2>");
        if (model.Rules.Count == 0) sb.Append("<p>No interpretation rules apply to this job.</p>");
        else
        {
            sb.Append("<table><thead><tr><th>Rule</th><th>Version</th><th>Applies to</th><th>Status</th><th>Matches</th><th>Does</th><th>Approved by</th></tr></thead><tbody>");
            foreach (var r in model.Rules) sb.Append($"<tr><td>{E(r.Name)}</td><td>{r.Version}</td><td>{E(r.Scope)}</td><td>{E(r.Status)}</td><td>{E(r.Match)}</td><td>{E(r.Action)}</td><td>{E(r.ApprovedBy)}</td></tr>");
            sb.Append("</tbody></table>");
        }

        sb.Append($"<h2>Time on this job</h2><p>{model.Metrics.ActiveHours:0.00} active hours and {model.Metrics.IdleHours:0.00} idle hours over {model.Metrics.Sessions} session(s), all people together.</p>");
        sb.Append("<p class=\"muted\">This tool counts what is on the drawings. It produces no quote, pricing, waste allowance or order quantity.</p></body></html>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
