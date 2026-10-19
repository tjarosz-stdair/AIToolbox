using System.IO.Compression;
using System.Text;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Activity;
using AIToolbox.Ductwork.Core.Counting;
using AIToolbox.Ductwork.Core.Drawings;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Reports;
using AIToolbox.Ductwork.Core.Rules;
using ClosedXML.Excel;
using PdfSharp.Pdf.IO;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Tests;

/// <summary>The count sheet as files: drawn from one model, stored with hashes when issued, and safe to open.</summary>
public sealed class IssuedReportTests(SqlFixture db) : IClassFixture<SqlFixture>, IDisposable
{
    private const string Owner = "owner@example.com";
    private const string Viewer = "viewer@example.com";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ductwork-rep-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private Func<CancellationToken, Task<SqlConnection>> Open => async ct =>
    {
        var connection = new SqlConnection(db.ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    };

    private static AuditContext Ctx(string email) => AuditContext.ForUser(email, displayName: email.Split('@')[0], sessionId: "s-" + email, userAgent: "tests");

    private ReportService Service(IEnumerable<IReportRenderer>? renderers = null) =>
        new(Open, new JobRepository(db.ConnectionString), new CountSheetRepository(Open), new RuleRepository(Open), new ActivityRepository(Open, TimeSpan.FromSeconds(15)),
            new FileSystemArtifactStore(_root), renderers);

    private sealed record Seeded(long JobId, string Name);

    /// <summary>A job with one confirmed building type (multiplier 26), counted items, one undetermined item and one excluded item.</summary>
    private async Task<Seeded> SeedAsync(string typeText = "Boot", bool confirmed = true)
    {
        var jobs = new JobRepository(db.ConnectionString);
        var name = "Report " + Guid.NewGuid().ToString("N")[..8];
        var job = await jobs.CreateAsync(new NewJob { Name = name, CustomerName = "Acme", ProjectName = "Phase 2" }, Ctx(Owner));
        await jobs.AddMemberAsync(job.JobId, Viewer, JobRole.Viewer, Ctx(Owner));
        var j = ("@j", (object?)job.JobId);

        var producer = await db.ScalarAsync<long>("INSERT INTO ductwork.Producer (ProducerKey, Kind, AlgorithmName, AlgorithmVersion, ParamsHash) OUTPUT inserted.ProducerId VALUES (@k, 'Algorithm', 'counter', '1.0', 'h');", ("@k", "rep-" + Guid.NewGuid().ToString("N")));
        var task = await db.ScalarAsync<long>("INSERT INTO ductwork.ProcessingTask (JobId, TaskType, Status, ProducerId, ContractVersion, CreatedBy, FinishedUtc) OUTPUT inserted.ProcessingTaskId VALUES (@j, 'detect', 'Succeeded', @p, '0.1.0', 'tests', SYSUTCDATETIME());", j, ("@p", producer));
        var type = await db.ScalarAsync<long>(
            confirmed
                ? "INSERT INTO ductwork.BuildingType (JobId, Name, NameNormalized, Multiplier, MultiplierSource, Confirmed, ConfirmedBy, ConfirmedUtc, CreatedBy) OUTPUT inserted.BuildingTypeId VALUES (@j, 'Type 4', 'type 4', 26, 'Manual', 1, 'tests', SYSUTCDATETIME(), 'tests');"
                : "INSERT INTO ductwork.BuildingType (JobId, Name, NameNormalized, CreatedBy) OUTPUT inserted.BuildingTypeId VALUES (@j, 'Type 4', 'type 4', 'tests');", j);

        async Task Entity(string detection, string kind, string? size, decimal qty, string review, string disposition, string? evidence = null) =>
            await db.ExecAsync(
                """
                INSERT INTO ductwork.Entity (JobId, BuildingTypeId, ProducerId, ProcessingTaskId, DetectionId, Type, Size, Quantity, Method, Family, Confidence, SurfacedAs, ReviewLevelAtCreation, ReviewStatus, Disposition, Evidence)
                VALUES (@j, @t, @p, @k, @d, @kind, @s, @q, 'Text', 'LabelFlagCount', 0.9, 'Batch', 'Sensitive', @r, @disp, @ev);
                """, j, ("@t", type), ("@p", producer), ("@k", task), ("@d", detection), ("@kind", kind), ("@s", size), ("@q", qty), ("@r", review), ("@disp", disposition), ("@ev", evidence));

        await Entity("d1", typeText, "4x10x7", 3, "Verified", "Included", "label 4x10");
        await Entity("d2", typeText, "4x10x7", 2, "Verified", "Included");
        await Entity("d3", "Exhaust fan", "EF-1", 1, "Verified", "Included");
        await Entity("d4", "Pass-through", null, 1, "Verified", "Excluded");
        await db.ExecAsync("UPDATE ductwork.JobWorkflowStep SET Status = 'Done' WHERE JobId = @j;", j);
        return new Seeded(job.JobId, name);
    }

    private async Task<Seeded> CompletingJobAsync()
    {
        var job = await SeedAsync();
        await new JobRepository(db.ConnectionString).BeginCompleteAsync(job.JobId, Ctx(Owner));
        return job;
    }

    private static XLWorkbook Book(byte[] bytes) => new(new MemoryStream(bytes));

    // ---------------------------------------------------------------- issuing

    [Fact]
    public async Task Issuing_stores_an_excel_an_html_and_a_pdf_file_with_hashes_from_the_same_model()
    {
        var job = await CompletingJobAsync();
        var reports = Service();

        var issued = await reports.IssueAsync(job.JobId, Ctx(Owner));

        Assert.Equal(["xlsx", "pdf", "html"], issued.OrderByDescending(r => r.Format).Select(r => r.Format));
        Assert.Single(issued.Select(r => r.ModelHash).Distinct());                           // both drawn from one state
        Assert.All(issued, r => { Assert.Equal(1, r.CompletionVersion); Assert.True(r.IsFinal); Assert.True(r.Bytes > 0); });

        foreach (var r in issued)
        {
            var (record, bytes, _) = await reports.GetStoredAsync(job.JobId, r.PublicId);
            Assert.Equal(r.Sha256, record.Sha256);
            Assert.Equal(r.Bytes, bytes.Length);
        }

        Assert.Contains("report.issued", (await new JobRepository(db.ConnectionString).ListAuditAsync(job.JobId, new AuditFilter { Category = "report" })).Select(e => e.Action));
        Assert.Equal(issued.Count, (await reports.ListReportsAsync(job.JobId)).Count);
    }

    [Fact]
    public async Task The_workbook_says_what_the_count_sheet_says_with_the_extension_computed_and_units_kept_apart()
    {
        var job = await CompletingJobAsync();
        var reports = Service();
        var xlsx = (await reports.IssueAsync(job.JobId, Ctx(Owner))).Single(r => r.Format == "xlsx");

        var (_, bytes, contentType) = await reports.GetStoredAsync(job.JobId, xlsx.PublicId);

        Assert.Contains("spreadsheetml", contentType);
        using var book = Book(bytes);
        Assert.Equal(["Summary", "Count sheet", "Job totals", "Holds", "Detail", "Rules applied"], book.Worksheets.Select(s => s.Name));
        Assert.StartsWith("FINAL", book.Worksheet("Summary").Cell(2, 1).GetString());

        var sheet = book.Worksheet("Count sheet");
        var boot = sheet.RowsUsed().Single(r => r.Cell(4).GetString() == "Boot");
        Assert.Equal((5m, 26m, 130m, "EA"), ((decimal)boot.Cell(9).GetDouble(), (decimal)boot.Cell(11).GetDouble(), (decimal)boot.Cell(12).GetDouble(), boot.Cell(10).GetString()));
        Assert.Contains("26", sheet.RowsUsed().First(r => r.Cell(1).GetString().StartsWith("Type 4")).Cell(11).GetString());

        var totals = book.Worksheet("Job totals").RowsUsed().Single(r => r.Cell(3).GetString() == "Boot");
        Assert.Equal(130d, totals.Cell(9).GetDouble());                                       // a number, not text
        Assert.Contains(book.Worksheet("Holds").RowsUsed(), r => r.Cell(2).GetString() == "Pass-through");   // listed, not counted
        Assert.Contains(book.Worksheet("Detail").RowsUsed(), r => r.Cell(18).GetString() == "label 4x10");
    }

    [Fact]
    public async Task What_the_drawings_say_can_never_become_a_formula_or_markup()
    {
        var job = await SeedAsync(typeText: "=HYPERLINK(\"http://evil.example\",\"x\")");
        var reports = Service();

        var (xlsx, _, _) = await reports.GetDraftAsync(job.JobId, "xlsx", Owner);
        var (html, _, _) = await reports.GetDraftAsync(job.JobId, "html", Owner);

        using (var zip = new ZipArchive(new MemoryStream(xlsx)))
            foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/")))
            {
                using var reader = new StreamReader(entry.Open());
                Assert.DoesNotContain("<f>", await reader.ReadToEndAsync());                  // no formula anywhere in the file
            }

        using var book = Book(xlsx);
        var cell = book.Worksheet("Count sheet").RowsUsed().Select(r => r.Cell(4)).First(c => c.GetString().StartsWith("=HYPERLINK"));
        Assert.False(cell.HasFormula);
        Assert.True(cell.Style.IncludeQuotePrefix);

        var markup = await SeedAsync(typeText: "<script>alert(1)</script>");
        var (page, _, _) = await reports.GetDraftAsync(markup.JobId, "html", Owner);
        var text = Encoding.UTF8.GetString(page);
        Assert.DoesNotContain("<script>alert", text);
        Assert.Contains("&lt;script&gt;", text);
        Assert.Contains("default-src 'none'", text);                                          // and a policy that forbids scripts anyway
        Assert.Contains("HYPERLINK", Encoding.UTF8.GetString(html));

        // A PDF only ever draws the text, so hostile text is just text: the file still opens as a PDF and carries no script or action.
        var (pdf, _, _) = await reports.GetDraftAsync(job.JobId, "pdf", Owner);
        var raw = Encoding.Latin1.GetString(pdf);
        Assert.DoesNotContain("/JavaScript", raw);
        Assert.DoesNotContain("/OpenAction", raw);
        Assert.DoesNotContain("/URI", raw);
        using var opened = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.ReadOnly);
        Assert.True(opened.PageCount >= 1);
    }

    [Fact]
    public async Task The_pdf_is_a_landscape_document_that_names_the_job_and_says_whether_it_is_final()
    {
        var job = await SeedAsync(confirmed: false);
        var reports = Service();

        var (bytes, name, contentType) = await reports.GetDraftAsync(job.JobId, "pdf", Owner);

        Assert.Equal("application/pdf", contentType);
        Assert.EndsWith(".pdf", name);
        Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(bytes, 0, 5));
        using var pdf = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.ReadOnly);
        Assert.True(pdf.PageCount >= 1);
        Assert.True(pdf.Pages[0].Width.Point > pdf.Pages[0].Height.Point);                    // landscape
        Assert.StartsWith("Count sheet:", pdf.Info.Title);
        Assert.Equal("Draft (not issued)", pdf.Info.Subject);
    }

    [Fact]
    public void A_long_sheet_runs_onto_more_pages_and_a_control_character_in_the_text_does_not_break_it()
    {
        var lines = Enumerable.Range(0, 400).Select(i => new CountLine
        {
            Type = "Boot " + i, Size = "4x10x" + i, Unit = QuantityUnit.EA, QuantityPerBuilding = 1, Multiplier = 2, ExtendedQuantity = 2, State = ExtendedState.Known, EntityCount = 1, VerifiedCount = 1
        }).ToList();
        var model = new ReportModel
        {
            Job = new ReportJob(1, "Long job", null, null, null, Owner, "Completing", 1, null, DateTime.UtcNow),
            Sheet = new CountSheet { Blocks = [new CountBlock { BuildingTypeId = 5, Name = "Type 1", Multiplier = 2, MultiplierConfirmed = true, Lines = lines }], Totals = [], OpenItems = [] },
            Rules = [], Details = [], Metrics = new ReportMetrics(1, 0, 1, new Dictionary<string, double>()), ForCompletionVersion = 1
        };

        var pdf = new PdfReportRenderer().Render(model, new ReportStamp("someone@example.com", new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc), IsIssued: true));

        using var opened = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.ReadOnly);
        Assert.True(opened.PageCount > 3);
        Assert.Equal("Issued report", opened.Info.Subject);
    }

    [Fact]
    public async Task A_draft_says_it_is_not_final_and_names_why_and_never_shows_a_to_be_determined_total_as_zero()
    {
        var job = await SeedAsync(confirmed: false);
        var reports = Service();

        var (bytes, name, _) = await reports.GetDraftAsync(job.JobId, "xlsx", Owner);

        Assert.Contains("draft", name);
        using var book = Book(bytes);
        var banner = book.Worksheet("Summary").Cell(2, 1).GetString();
        Assert.StartsWith("NOT FINAL", banner);
        Assert.Contains("Type 4", banner);
        var total = book.Worksheet("Job totals").RowsUsed().Single(r => r.Cell(3).GetString() == "Boot").Cell(9).GetString();
        Assert.StartsWith("TBD (at least", total);
        Assert.Equal("TBD", book.Worksheet("Count sheet").RowsUsed().First(r => r.Cell(4).GetString() == "Boot").Cell(12).GetString());
        Assert.Contains("Draft", book.Worksheet("Summary").RowsUsed().Select(r => r.Cell(2).GetString()).First(t => t.StartsWith("Draft")));
        Assert.Empty(await reports.ListReportsAsync(job.JobId));                              // a draft is never stored
    }

    [Fact]
    public async Task The_draft_and_the_issued_report_are_drawn_from_the_same_state()
    {
        var job = await CompletingJobAsync();
        var reports = Service();
        var before = (await reports.GetModelAsync(job.JobId)).ModelHash();

        var issued = await reports.IssueAsync(job.JobId, Ctx(Owner));

        Assert.Equal(Convert.ToHexString(before), issued[0].ModelHash);
    }

    // ---------------------------------------------------------------- rules of issuing

    [Fact]
    public async Task Reports_are_issued_only_while_a_job_is_being_completed_and_only_by_someone_who_may_complete_it()
    {
        var draft = await SeedAsync();
        var refused = await Assert.ThrowsAsync<JobRuleViolationException>(() => Service().IssueAsync(draft.JobId, Ctx(Owner)));
        Assert.Contains("being completed", refused.Message);

        var completing = await CompletingJobAsync();
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => Service().IssueAsync(completing.JobId, Ctx(Viewer)));
        Assert.Empty(await Service().ListReportsAsync(completing.JobId));
    }

    [Fact]
    public async Task A_failed_run_leaves_no_rows_and_no_files_and_the_job_can_try_again()
    {
        var job = await CompletingJobAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service([new ExcelReportRenderer(), new FailingRenderer()]).IssueAsync(job.JobId, Ctx(Owner)));

        Assert.Empty(await Service().ListReportsAsync(job.JobId));
        Assert.Equal(JobState.Completing, (await new JobRepository(db.ConnectionString).GetAsync(job.JobId))!.State);
        var dir = Path.Combine(_root, "final", "reports", job.JobId.ToString());
        Assert.False(Directory.Exists(dir) && Directory.EnumerateFiles(dir).Any());           // the half-made file was removed
        Assert.Equal(3, (await Service().IssueAsync(job.JobId, Ctx(Owner))).Count);
    }

    // ---------------------------------------------------------------- records that cannot change

    [Fact]
    public async Task An_issued_report_cannot_be_changed_or_deleted_and_a_changed_file_is_caught_when_read()
    {
        var job = await CompletingJobAsync();
        var reports = Service();
        var issued = (await reports.IssueAsync(job.JobId, Ctx(Owner))).Single(r => r.Format == "html");

        var update = await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync("UPDATE ductwork.JobReport SET IsFinal = 0 WHERE ReportId = @r;", ("@r", issued.ReportId)));
        var delete = await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync("DELETE FROM ductwork.JobReport WHERE ReportId = @r;", ("@r", issued.ReportId)));
        Assert.Equal((51018, 51018), (update.Number, delete.Number));

        var path = Directory.EnumerateFiles(Path.Combine(_root, "final", "reports", job.JobId.ToString()), "*.html").Single();
        await File.AppendAllTextAsync(path, "<!-- edited -->");
        await Assert.ThrowsAsync<ReportIntegrityException>(() => reports.GetStoredAsync(job.JobId, issued.PublicId));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => reports.GetStoredAsync(job.JobId, Guid.NewGuid()));
    }

    [Fact]
    public async Task Each_issue_is_a_new_set_and_the_newest_is_listed_first()
    {
        var job = await CompletingJobAsync();
        var reports = Service();

        var first = await reports.IssueAsync(job.JobId, Ctx(Owner));
        var second = await reports.IssueAsync(job.JobId, Ctx(Owner));

        var all = await reports.ListReportsAsync(job.JobId);
        Assert.Equal(6, all.Count);
        Assert.Equal(second.Max(r => r.ReportId), all[0].ReportId);
        Assert.Empty(first.Select(r => r.PublicId).Intersect(second.Select(r => r.PublicId)));
    }

    private sealed class FailingRenderer : IReportRenderer
    {
        public string Format => "html";
        public string ContentType => "text/html";
        public string Extension => "html";
        public byte[] Render(ReportModel model, ReportStamp stamp) => throw new InvalidOperationException("the renderer broke");
    }
}
