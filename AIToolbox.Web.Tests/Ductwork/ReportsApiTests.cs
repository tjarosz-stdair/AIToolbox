using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Reports;
using AIToolbox.Web.Controllers.Ductwork;
using static AIToolbox.Web.Tests.Ductwork.ApiHarness;

namespace AIToolbox.Web.Tests.Ductwork;

public class ReportsApiTests(DuctworkDatabase db) : IClassFixture<DuctworkDatabase>
{
    private readonly ApiHarness _h = new(db);

    private async Task<long> JobReadyToCompleteAsync()
    {
        var id = await _h.NewJobAsync();
        await db.ExecAsync("UPDATE ductwork.JobWorkflowStep SET Status = 'Done' WHERE JobId = @j;", ("@j", id));
        return id;
    }

    [Fact]
    public async Task Finishing_completion_issues_the_reports_first_and_they_can_be_listed_and_downloaded()
    {
        var id = await JobReadyToCompleteAsync();
        await RunAsync(() => _h.JobsApi(Owner).BeginComplete(id, default));

        var done = await RunAsync(() => _h.JobsApi(Owner).FinishComplete(id, default));

        Assert.Equal((200, "Complete"), (done.Status, done["state"].GetString()));
        var list = await RunAsync(() => _h.ReportsApi(Viewer).List(id, default));
        Assert.Equal(["html", "pdf", "xlsx"], list.Body.EnumerateArray().Select(r => r.GetProperty("format").GetString()).Order());

        var xlsx = list.Body.EnumerateArray().Single(r => r.GetProperty("format").GetString() == "xlsx");
        var download = await RunAsync(() => _h.ReportsApi(Viewer).Download(id, xlsx.GetProperty("reportId").GetGuid(), default));
        Assert.Contains("spreadsheetml", download["contentType"].GetString());
        Assert.StartsWith($"count-sheet-{id}-v1-", download["downloadName"].GetString());
        Assert.Equal(xlsx.GetProperty("bytes").GetInt64(), download.Stream!.Length);

        var pdf = list.Body.EnumerateArray().Single(r => r.GetProperty("format").GetString() == "pdf");
        var pdfDownload = await RunAsync(() => _h.ReportsApi(Viewer).Download(id, pdf.GetProperty("reportId").GetGuid(), default));
        Assert.Equal("application/pdf", pdfDownload["contentType"].GetString());
        Assert.EndsWith(".pdf", pdfDownload["downloadName"].GetString());
        var header = new byte[5];
        await pdfDownload.Stream!.ReadExactlyAsync(header);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(header));
    }

    [Fact]
    public async Task A_failed_report_run_leaves_the_job_Completing_and_a_retry_completes_it()
    {
        var id = await JobReadyToCompleteAsync();
        await RunAsync(() => _h.JobsApi(Owner).BeginComplete(id, default));

        var broken = _h.Reports([new BrokenRenderer()]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _h.JobsApi(Owner, reports: broken).FinishComplete(id, default));

        Assert.Equal(JobState.Completing, (await _h.JobRepo.GetAsync(id))!.State);
        Assert.Empty((await RunAsync(() => _h.ReportsApi(Owner).List(id, default))).Body.EnumerateArray());
        Assert.Equal("Complete", (await RunAsync(() => _h.JobsApi(Owner).FinishComplete(id, default)))["state"].GetString());
    }

    [Fact]
    public async Task Anyone_on_the_job_can_get_a_draft_and_it_downloads_safely_but_a_stranger_cannot()
    {
        var id = await _h.NewJobAsync();
        var controller = _h.ReportsApi(Viewer);

        var draft = await RunAsync(() => controller.Draft(id, "html", default));

        Assert.Equal(200, draft.Status);
        Assert.Contains("text/html", draft["contentType"].GetString());
        Assert.Contains("draft", draft["downloadName"].GetString());
        Assert.Equal("nosniff", controller.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Contains("default-src 'none'", controller.Response.Headers["Content-Security-Policy"].ToString());
        Assert.Equal(404, (await RunAsync(() => _h.ReportsApi(Stranger).Draft(id, "xlsx", default))).Status);
        Assert.Equal(404, (await RunAsync(() => _h.ReportsApi(Stranger).List(id, default))).Status);
        Assert.Equal(400, (await RunAsync(() => _h.ReportsApi(Owner).Draft(id, "exe", default))).Status);
        Assert.Equal(404, (await RunAsync(() => _h.ReportsApi(Owner).Download(id, Guid.NewGuid(), default))).Status);
        Assert.Empty((await RunAsync(() => _h.ReportsApi(Owner).List(id, default))).Body.EnumerateArray());      // a draft is never kept
    }

    [Fact]
    public async Task One_jobs_report_cannot_be_fetched_through_another_job()
    {
        var a = await JobReadyToCompleteAsync();
        await RunAsync(() => _h.JobsApi(Owner).BeginComplete(a, default));
        await RunAsync(() => _h.JobsApi(Owner).FinishComplete(a, default));
        var reportId = (await RunAsync(() => _h.ReportsApi(Owner).List(a, default))).Body[0].GetProperty("reportId").GetGuid();
        var b = await _h.NewJobAsync();

        Assert.Equal(404, (await RunAsync(() => _h.ReportsApi(Owner).Download(b, reportId, default))).Status);
    }

    private sealed class BrokenRenderer : IReportRenderer
    {
        public string Format => "html";
        public string ContentType => "text/html";
        public string Extension => "html";
        public byte[] Render(ReportModel model, ReportStamp stamp) => throw new InvalidOperationException("the renderer broke");
    }
}
