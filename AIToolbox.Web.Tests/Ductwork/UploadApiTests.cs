using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIToolbox.Web.Controllers.Ductwork;
using static AIToolbox.Web.Tests.Ductwork.ApiHarness;

namespace AIToolbox.Web.Tests.Ductwork;

public class UploadApiTests(DuctworkDatabase db) : IClassFixture<DuctworkDatabase>
{
    private readonly ApiHarness _h = new(db);

    private static byte[] Pdf(int size)
    {
        var bytes = new byte[size];
        Array.Fill(bytes, (byte)3);
        Encoding.ASCII.GetBytes("%PDF-1.7\n").CopyTo(bytes, 0);
        return bytes;
    }

    private static string Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static MemoryStream Slice(byte[] data, int offset, int length) => new(data, offset, length);

    private async Task<(long Job, string Tab, long Token, Guid Upload)> StartedAsync(byte[] data, string name = "M1.2.pdf")
    {
        var job = await _h.NewJobAsync();
        var (tab, token) = await _h.EditorLocksAsync(job);
        var begun = await RunAsync(() => _h.UploadsApi(Editor, tab, token).Begin(job, new BeginUploadRequest(name, data.Length, Hex(data)), default));
        Assert.Equal(201, begun.Status);
        return (job, tab, token, begun["uploadId"].GetGuid());
    }

    [Fact]
    public async Task A_file_goes_up_in_pieces_and_comes_back_identical_and_a_viewer_can_read_it()
    {
        var data = Pdf(2300);
        var (job, tab, token, upload) = await StartedAsync(data);

        var started = await RunAsync(() => _h.UploadsApi(Editor, tab).Status(job, upload, default));
        Assert.Equal((0L, 1000L, "Open"), (started["receivedBytes"].GetInt64(), started["chunkBytes"].GetInt64(), started["status"].GetString()));

        for (var offset = 0; offset < data.Length; offset += 1000)
        {
            var at = offset;
            var length = Math.Min(1000, data.Length - at);
            var step = await RunAsync(() => _h.UploadsApi(Editor, tab, body: Slice(data, at, length), chunkSha256: Hex(data[at..(at + length)])).Append(job, upload, at, default));
            Assert.Equal(at + length, step["receivedBytes"].GetInt64());
        }

        var done = await RunAsync(() => _h.UploadsApi(Editor, tab, token).Complete(job, upload, default));
        Assert.Equal((201, Hex(data), 1), (done.Status, done["sha256"].GetString(), done["revision"].GetInt32()));

        var list = await RunAsync(() => _h.UploadsApi(Viewer, NewTab()).ListFiles(job, default));
        Assert.Equal(["M1.2.pdf"], list.Body.EnumerateArray().Select(f => f.GetProperty("fileName").GetString()));

        var fileId = done["fileId"].GetGuid();
        var download = await RunAsync(() => _h.UploadsApi(Viewer, NewTab()).Content(job, fileId, false, default));
        await using var stream = download.Stream!;
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        Assert.Equal((200, "application/pdf", "M1.2.pdf"), (download.Status, download["contentType"].GetString(), download["downloadName"].GetString()));
        Assert.Equal(data, copy.ToArray());
    }

    [Fact]
    public async Task A_download_is_never_allowed_to_run_as_a_page()
    {
        var data = Pdf(500);
        var (job, tab, token, upload) = await StartedAsync(data);
        await RunAsync(() => _h.UploadsApi(Editor, tab, body: Slice(data, 0, 500)).Append(job, upload, 0, default));
        var fileId = (await RunAsync(() => _h.UploadsApi(Editor, tab, token).Complete(job, upload, default)))["fileId"].GetGuid();

        var controller = _h.UploadsApi(Viewer, NewTab());
        var inline = await RunAsync(() => controller.Content(job, fileId, true, default));
        inline.Stream!.Dispose();

        Assert.Equal("nosniff", controller.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Equal("sandbox", controller.Response.Headers["Content-Security-Policy"].ToString());
        Assert.True(string.IsNullOrEmpty(inline["downloadName"].GetString()));   // no attachment name, so the browser shows it
    }

    [Fact]
    public async Task A_piece_at_the_wrong_offset_is_a_409_that_says_where_to_resume()
    {
        var data = Pdf(3000);
        var (job, tab, _, upload) = await StartedAsync(data);
        await RunAsync(() => _h.UploadsApi(Editor, tab, body: Slice(data, 0, 1000)).Append(job, upload, 0, default));

        var reply = await RunAsync(() => _h.UploadsApi(Editor, tab, body: Slice(data, 2000, 1000)).Append(job, upload, 2000, default));

        Assert.Equal((409, "offset-mismatch", 1000), (reply.Status, reply.Code, reply["expectedOffset"].GetInt32()));
    }

    [Fact]
    public async Task A_damaged_piece_is_a_422_and_an_oversized_piece_is_a_413_before_it_is_read()
    {
        var data = Pdf(3000);
        var (job, tab, _, upload) = await StartedAsync(data);

        var damaged = await RunAsync(() => _h.UploadsApi(Editor, tab, body: Slice(data, 0, 1000), chunkSha256: Hex([9])).Append(job, upload, 0, default));
        Assert.Equal((422, "checksum"), (damaged.Status, damaged.Code));

        var tooBig = await RunAsync(() => _h.UploadsApi(Editor, tab, body: Slice(data, 0, 2000)).Append(job, upload, 0, default));
        Assert.Equal((413, "too-large"), (tooBig.Status, tooBig.Code));
        Assert.Equal(0, (await RunAsync(() => _h.UploadsApi(Editor, tab).Status(job, upload, default)))["receivedBytes"].GetInt32());
    }

    [Fact]
    public async Task Starting_and_finishing_need_the_fencing_token_and_the_pieces_need_the_same_tab()
    {
        var data = Pdf(500);
        var (job, tab, token, upload) = await StartedAsync(data);

        var noToken = await RunAsync(() => _h.UploadsApi(Editor, tab).Begin(job, new BeginUploadRequest("x.pdf", 10, null), default));
        Assert.Equal(400, noToken.Status);
        var oldToken = await RunAsync(() => _h.UploadsApi(Editor, tab, token + 500).Begin(job, new BeginUploadRequest("x.pdf", 10, null), default));
        Assert.Equal("lock-lost", oldToken.Code);

        var otherTab = await RunAsync(() => _h.UploadsApi(Editor, NewTab(), body: Slice(data, 0, 500)).Append(job, upload, 0, default));
        Assert.Equal(404, otherTab.Status);                         // another tab does not own this upload
        var early = await RunAsync(() => _h.UploadsApi(Editor, tab, token).Complete(job, upload, default));
        Assert.Equal((422, "rule"), (early.Status, early.Code));    // nothing has been sent yet
    }

    [Fact]
    public async Task A_stranger_gets_nothing_and_a_viewer_cannot_start()
    {
        var data = Pdf(500);
        var (job, tab, token, upload) = await StartedAsync(data);

        Assert.Equal(404, (await RunAsync(() => _h.UploadsApi(Stranger, NewTab()).ListFiles(job, default))).Status);
        Assert.Equal(404, (await RunAsync(() => _h.UploadsApi(Stranger, NewTab()).Status(job, upload, default))).Status);
        Assert.Equal(404, (await RunAsync(() => _h.UploadsApi(Stranger, NewTab()).Content(job, Guid.NewGuid(), false, default))).Status);
        Assert.Equal(404, (await RunAsync(() => _h.UploadsApi(Editor, tab).Status(job + 12345, upload, default))).Status);

        var viewer = await RunAsync(() => _h.UploadsApi(Viewer, NewTab(), token).Begin(job, new BeginUploadRequest("v.pdf", 10, null), default));
        Assert.False(viewer.Ok);
    }

    [Fact]
    public async Task An_upload_cannot_be_reached_through_another_job()
    {
        var data = Pdf(500);
        var (_, tab, _, upload) = await StartedAsync(data);
        var otherJob = await _h.NewJobAsync();

        Assert.Equal(404, (await RunAsync(() => _h.UploadsApi(Editor, tab, body: Slice(data, 0, 500)).Append(otherJob, upload, 0, default))).Status);
        Assert.Equal(404, (await RunAsync(() => _h.UploadsApi(Editor, tab).Abort(otherJob, upload, default))).Status);
    }

    [Fact]
    public async Task Cancelling_ends_the_upload()
    {
        var data = Pdf(500);
        var (job, tab, _, upload) = await StartedAsync(data);

        Assert.True((await RunAsync(() => _h.UploadsApi(Editor, tab).Abort(job, upload, default)))["cancelled"].GetBoolean());
        Assert.Equal("Aborted", (await RunAsync(() => _h.UploadsApi(Editor, tab).Status(job, upload, default)))["status"].GetString());
    }

    [Fact]
    public async Task A_file_name_that_is_not_a_pdf_name_is_a_422()
    {
        var job = await _h.NewJobAsync();
        var (tab, token) = await _h.EditorLocksAsync(job);

        var reply = await RunAsync(() => _h.UploadsApi(Editor, tab, token).Begin(job, new BeginUploadRequest("plan.exe", 10, null), default));

        Assert.Equal((422, "rule"), (reply.Status, reply.Code));
    }
}
