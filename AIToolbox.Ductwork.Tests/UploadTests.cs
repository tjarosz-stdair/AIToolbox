using System.Security.Cryptography;
using System.Text;
using AIToolbox.Ductwork.Core.Drawings;
using AIToolbox.Ductwork.Core.Jobs;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Tests;

public sealed class UploadTests(SqlFixture db) : IClassFixture<SqlFixture>, IDisposable
{
    private const string Owner = "owner@example.com";
    private const string Editor = "editor@example.com";
    private const string Viewer = "viewer@example.com";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ductwork-artifacts-" + Guid.NewGuid().ToString("N"));

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

    private FileSystemArtifactStore Store => new(_root);

    private UploadService Uploads(UploadSettings? settings = null) => new(Open, Store, settings ?? new UploadSettings { ChunkBytes = 1000 });

    private static AuditContext Ctx(string email, string? session = null) =>
        AuditContext.ForUser(email, displayName: email.Split('@')[0], sessionId: session ?? "s-" + email, userAgent: "tests");

    private sealed record Setup(JobRecord Job, EditToken Token);

    private async Task<Setup> JobAsync(string editor = Editor)
    {
        var jobs = new JobRepository(db.ConnectionString);
        var job = await jobs.CreateAsync(new NewJob { Name = "Upload " + Guid.NewGuid().ToString("N")[..8] }, Ctx(Owner));
        await jobs.AddMemberAsync(job.JobId, Editor, JobRole.Editor, Ctx(Owner));
        await jobs.AddMemberAsync(job.JobId, Viewer, JobRole.Viewer, Ctx(Owner));
        var token = (await new EditLockService(db.ConnectionString).AcquireAsync(job.JobId, Ctx(editor))).Token!;
        return new Setup(job, token);
    }

    private static byte[] Pdf(int size, byte seed = 7)
    {
        var bytes = new byte[size];
        Array.Fill(bytes, seed);
        var header = Encoding.ASCII.GetBytes("%PDF-1.7\n");
        header.CopyTo(bytes, 0);
        return bytes;
    }

    private static string Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>Sends a whole file in pieces of the given size and returns the started session.</summary>
    private static async Task SendAsync(UploadService service, Guid id, byte[] data, int piece, AuditContext who, int from = 0, int? to = null)
    {
        for (var offset = from; offset < (to ?? data.Length); offset += piece)
        {
            var length = Math.Min(piece, (to ?? data.Length) - offset);
            await service.AppendAsync(id, offset, new MemoryStream(data, offset, length), null, who);
        }
    }

    private Task<int> CountEventsAsync(long jobId, string action) =>
        db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.JobAuditEvent WHERE JobId = @j AND Action = @a;", ("@j", jobId), ("@a", action));

    // ---- the happy path and resuming

    [Fact]
    public async Task A_file_sent_in_pieces_is_verified_stored_recorded_and_audited()
    {
        var s = await JobAsync();
        var data = Pdf(3500);
        var service = Uploads();
        var session = await service.BeginAsync(s.Job.JobId, new BeginUpload(@"C:\fakepath\M1.2 Plan.PDF", data.Length, Hex(data)), s.Token, Ctx(Editor));
        Assert.Equal(("M1.2 Plan.PDF", 0L, UploadStatus.Open, 1000L), (session.OriginalName, session.ReceivedBytes, session.Status, session.ChunkBytes));

        await SendAsync(service, session.PublicId, data, 1000, Ctx(Editor));
        var file = await service.CompleteAsync(session.PublicId, s.Token, Ctx(Editor));

        Assert.Equal(("M1.2 Plan.PDF", (long)data.Length, Hex(data), "application/pdf", 1), (file.OriginalName, file.Bytes, file.Sha256, file.ContentType, file.RevisionNo));
        Assert.NotNull(file.StorageKey);
        Assert.StartsWith($"jobs/{s.Job.PublicId:N}/", file.StorageKey);
        Assert.DoesNotContain("M1.2", file.StorageKey);                 // the key is built from ids, not the name

        await using var stream = (await service.GetFileContentAsync(file))!;
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        Assert.Equal(data, copy.ToArray());

        Assert.Equal(UploadStatus.Completed, (await service.GetAsync(session.PublicId))!.Status);
        Assert.Equal((1, 1), (await CountEventsAsync(s.Job.JobId, "upload.started"), await CountEventsAsync(s.Job.JobId, "source-file.added")));
        Assert.True((await new JobRepository(db.ConnectionString).VerifyAuditChainAsync(s.Job.JobId)).IsValid);
        Assert.False(Directory.Exists(Path.Combine(_root, "staging")) && Directory.EnumerateFiles(Path.Combine(_root, "staging"), "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task A_dropped_upload_resumes_from_what_the_server_holds_even_from_a_new_process()
    {
        var s = await JobAsync();
        var data = Pdf(2500);
        var session = await Uploads().BeginAsync(s.Job.JobId, new BeginUpload("a.pdf", data.Length, null), s.Token, Ctx(Editor));
        await SendAsync(Uploads(), session.PublicId, data, 1000, Ctx(Editor), to: 2000);

        var resumed = Uploads();                                          // a fresh service, as after a restart
        var where = (await resumed.GetAsync(session.PublicId))!.ReceivedBytes;
        Assert.Equal(2000, where);
        await SendAsync(resumed, session.PublicId, data, 1000, Ctx(Editor), from: (int)where);

        var file = await resumed.CompleteAsync(session.PublicId, s.Token, Ctx(Editor));
        Assert.Equal(Hex(data), file.Sha256);
    }

    [Fact]
    public async Task Bytes_left_in_staging_by_a_crash_beyond_what_was_recorded_are_cut_off()
    {
        var s = await JobAsync();
        var data = Pdf(2000);
        var service = Uploads();
        var session = await service.BeginAsync(s.Job.JobId, new BeginUpload("a.pdf", data.Length, Hex(data)), s.Token, Ctx(Editor));
        await SendAsync(service, session.PublicId, data, 1000, Ctx(Editor), to: 1000);

        // The process died after writing part of the next piece but before recording it.
        var path = Path.Combine(_root, "staging", session.StagingKey.Replace('/', Path.DirectorySeparatorChar));
        await File.AppendAllTextAsync(path, "JUNK-FROM-A-CRASH");

        await SendAsync(service, session.PublicId, data, 1000, Ctx(Editor), from: 1000);
        var file = await service.CompleteAsync(session.PublicId, s.Token, Ctx(Editor));

        Assert.Equal(Hex(data), file.Sha256);
    }

    // ---- refusals while sending

    [Fact]
    public async Task A_piece_at_the_wrong_offset_is_refused_and_says_where_the_upload_is()
    {
        var s = await JobAsync();
        var data = Pdf(3000);
        var service = Uploads();
        var session = await service.BeginAsync(s.Job.JobId, new BeginUpload("a.pdf", data.Length, null), s.Token, Ctx(Editor));
        await SendAsync(service, session.PublicId, data, 1000, Ctx(Editor), to: 1000);

        var skipped = await Assert.ThrowsAsync<UploadOffsetException>(() => service.AppendAsync(session.PublicId, 2000, new MemoryStream(data, 2000, 1000), null, Ctx(Editor)));
        var replayed = await Assert.ThrowsAsync<UploadOffsetException>(() => service.AppendAsync(session.PublicId, 0, new MemoryStream(data, 0, 1000), null, Ctx(Editor)));

        Assert.Equal([1000L, 1000L], [skipped.Expected, replayed.Expected]);
        Assert.Equal(1000, (await service.GetAsync(session.PublicId))!.ReceivedBytes);
    }

    [Fact]
    public async Task A_piece_that_fails_its_checksum_is_discarded_and_can_be_sent_again()
    {
        var s = await JobAsync();
        var data = Pdf(1500);
        var service = Uploads();
        var session = await service.BeginAsync(s.Job.JobId, new BeginUpload("a.pdf", data.Length, null), s.Token, Ctx(Editor));

        await Assert.ThrowsAsync<ChunkIntegrityException>(() =>
            service.AppendAsync(session.PublicId, 0, new MemoryStream(data, 0, 1000), Hex([1, 2, 3]), Ctx(Editor)));
        Assert.Equal(0, (await service.GetAsync(session.PublicId))!.ReceivedBytes);

        var ok = await service.AppendAsync(session.PublicId, 0, new MemoryStream(data, 0, 1000), Hex(data[..1000]), Ctx(Editor));
        Assert.Equal(1000, ok.ReceivedBytes);
    }

    [Fact]
    public async Task A_piece_larger_than_the_chunk_size_or_past_the_end_is_refused_and_changes_nothing()
    {
        var s = await JobAsync();
        var data = Pdf(1200);
        var service = Uploads();
        var session = await service.BeginAsync(s.Job.JobId, new BeginUpload("a.pdf", data.Length, null), s.Token, Ctx(Editor));

        await Assert.ThrowsAsync<ArgumentException>(() => service.AppendAsync(session.PublicId, 0, new MemoryStream(data), null, Ctx(Editor)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AppendAsync(session.PublicId, 0, new MemoryStream([]), null, Ctx(Editor)));
        Assert.Equal(0, (await service.GetAsync(session.PublicId))!.ReceivedBytes);

        await SendAsync(service, session.PublicId, data, 1000, Ctx(Editor));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => service.AppendAsync(session.PublicId, 1200, new MemoryStream([1]), null, Ctx(Editor)));
    }

    [Fact]
    public async Task Only_the_session_that_started_an_upload_can_add_to_it_and_anyone_else_is_told_it_does_not_exist()
    {
        var s = await JobAsync();
        var data = Pdf(500);
        var service = Uploads();
        var session = await service.BeginAsync(s.Job.JobId, new BeginUpload("a.pdf", data.Length, null), s.Token, Ctx(Editor));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.AppendAsync(session.PublicId, 0, new MemoryStream(data), null, Ctx(Editor, "another-tab")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.AppendAsync(session.PublicId, 0, new MemoryStream(data), null, Ctx(Owner)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.AppendAsync(session.PublicId, 0, new MemoryStream(data), null, AuditContext.ForWorker("w")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.AppendAsync(Guid.NewGuid(), 0, new MemoryStream(data), null, Ctx(Editor)));
    }

    [Fact]
    public async Task Losing_edit_permission_or_the_job_being_closed_stops_further_pieces()
    {
        var s = await JobAsync();
        var data = Pdf(2000);
        var service = Uploads();
        var session = await service.BeginAsync(s.Job.JobId, new BeginUpload("a.pdf", data.Length, null), s.Token, Ctx(Editor));
        await SendAsync(service, session.PublicId, data, 1000, Ctx(Editor), to: 1000);

        await new JobRepository(db.ConnectionString).ChangeMemberRoleAsync(s.Job.JobId, Editor, JobRole.Viewer, Ctx(Owner));
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => service.AppendAsync(session.PublicId, 1000, new MemoryStream(data, 1000, 1000), null, Ctx(Editor)));
    }

    // ---- starting

    [Fact]
    public async Task Starting_needs_the_edit_lock_and_edit_permission()
    {
        var s = await JobAsync();
        var service = Uploads();
        var begin = new BeginUpload("a.pdf", 100, null);

        await Assert.ThrowsAsync<StaleEditTokenException>(() => service.BeginAsync(s.Job.JobId, begin, s.Token, Ctx(Editor, "other-tab")));
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => service.BeginAsync(s.Job.JobId, begin, s.Token, Ctx(Viewer)));
        await Assert.ThrowsAsync<JobNotFoundException>(() => service.BeginAsync(long.MaxValue, begin, s.Token, Ctx(Editor)));
    }

    [Theory]
    [InlineData("plan.dwg")]
    [InlineData("plan")]
    [InlineData("   ")]
    [InlineData("..\\")]
    public async Task Only_pdf_names_are_accepted(string name)
    {
        var s = await JobAsync();
        await Assert.ThrowsAsync<JobRuleViolationException>(() => Uploads().BeginAsync(s.Job.JobId, new BeginUpload(name, 100, null), s.Token, Ctx(Editor)));
    }

    [Fact]
    public async Task Size_checksum_and_the_number_of_open_uploads_are_checked_at_the_start()
    {
        var s = await JobAsync();
        var service = Uploads(new UploadSettings { ChunkBytes = 1000, MaxFileBytes = 5000, MaxOpenPerJob = 2 });

        await Assert.ThrowsAsync<JobRuleViolationException>(() => service.BeginAsync(s.Job.JobId, new BeginUpload("a.pdf", 0, null), s.Token, Ctx(Editor)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => service.BeginAsync(s.Job.JobId, new BeginUpload("a.pdf", 5001, null), s.Token, Ctx(Editor)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.BeginAsync(s.Job.JobId, new BeginUpload("a.pdf", 10, "abc"), s.Token, Ctx(Editor)));

        await service.BeginAsync(s.Job.JobId, new BeginUpload("a.pdf", 10, null), s.Token, Ctx(Editor));
        await service.BeginAsync(s.Job.JobId, new BeginUpload("b.pdf", 10, null), s.Token, Ctx(Editor));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => service.BeginAsync(s.Job.JobId, new BeginUpload("c.pdf", 10, null), s.Token, Ctx(Editor)));
    }

    // ---- finishing

    [Fact]
    public async Task Finishing_early_or_with_a_wrong_checksum_or_a_non_pdf_is_refused_and_stores_nothing()
    {
        var s = await JobAsync();
        var service = Uploads();

        var early = await service.BeginAsync(s.Job.JobId, new BeginUpload("early.pdf", 2000, null), s.Token, Ctx(Editor));
        await SendAsync(service, early.PublicId, Pdf(2000), 1000, Ctx(Editor), to: 1000);
        await Assert.ThrowsAsync<JobRuleViolationException>(() => service.CompleteAsync(early.PublicId, s.Token, Ctx(Editor)));

        var data = Pdf(1000);
        var damaged = await service.BeginAsync(s.Job.JobId, new BeginUpload("damaged.pdf", 1000, Hex(Pdf(1000, seed: 9))), s.Token, Ctx(Editor));
        await SendAsync(service, damaged.PublicId, data, 1000, Ctx(Editor));
        var bad = await Assert.ThrowsAsync<JobRuleViolationException>(() => service.CompleteAsync(damaged.PublicId, s.Token, Ctx(Editor)));
        Assert.Contains("checksum", bad.Message);

        var notPdf = await service.BeginAsync(s.Job.JobId, new BeginUpload("fake.pdf", 600, null), s.Token, Ctx(Editor));
        await SendAsync(service, notPdf.PublicId, Encoding.ASCII.GetBytes(new string('x', 600)), 1000, Ctx(Editor));
        var fake = await Assert.ThrowsAsync<JobRuleViolationException>(() => service.CompleteAsync(notPdf.PublicId, s.Token, Ctx(Editor)));
        Assert.Contains("not a PDF", fake.Message);

        Assert.Empty(await service.ListFilesAsync(s.Job.JobId));
        Assert.False(Directory.Exists(Path.Combine(_root, "final")));
    }

    [Fact]
    public async Task A_second_upload_of_identical_bytes_is_recorded_as_a_duplicate_and_stored_once()
    {
        var s = await JobAsync();
        var data = Pdf(1800);
        var service = Uploads();

        async Task<SourceFileRecord> UploadAsync(string name)
        {
            var session = await service.BeginAsync(s.Job.JobId, new BeginUpload(name, data.Length, Hex(data)), s.Token, Ctx(Editor));
            await SendAsync(service, session.PublicId, data, 1000, Ctx(Editor));
            return await service.CompleteAsync(session.PublicId, s.Token, Ctx(Editor));
        }

        var first = await UploadAsync("first.pdf");
        var second = await UploadAsync("copy of first.pdf");

        Assert.Null(first.DuplicateOfPublicId);
        Assert.Equal((first.PublicId, (string?)null), (second.DuplicateOfPublicId, second.StorageKey));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "final"), "*", SearchOption.AllDirectories));
        await using var stream = (await service.GetFileContentAsync(second))!;
        Assert.Equal(data.Length, stream.Length);
        Assert.Equal(1, await CountEventsAsync(s.Job.JobId, "source-file.duplicate"));
        Assert.Equal(2, (await service.ListFilesAsync(s.Job.JobId)).Count);
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.DrawingSetRevision WHERE JobId = @j;", ("@j", s.Job.JobId)));
    }

    [Fact]
    public async Task The_same_file_in_another_job_is_not_a_duplicate_of_this_job()
    {
        var a = await JobAsync();
        var b = await JobAsync();
        var data = Pdf(900);
        var service = Uploads();

        foreach (var s in new[] { a, b })
        {
            var session = await service.BeginAsync(s.Job.JobId, new BeginUpload("x.pdf", data.Length, null), s.Token, Ctx(Editor));
            await SendAsync(service, session.PublicId, data, 1000, Ctx(Editor));
            Assert.NotNull((await service.CompleteAsync(session.PublicId, s.Token, Ctx(Editor))).StorageKey);
        }
    }

    [Fact]
    public async Task Completing_with_an_old_token_is_refused_and_the_upload_stays_open()
    {
        var s = await JobAsync();
        var data = Pdf(500);
        var service = Uploads();
        var session = await service.BeginAsync(s.Job.JobId, new BeginUpload("a.pdf", data.Length, null), s.Token, Ctx(Editor));
        await SendAsync(service, session.PublicId, data, 1000, Ctx(Editor));

        var locks = new EditLockService(db.ConnectionString);
        await locks.ReleaseAsync(s.Token, Ctx(Editor));
        await locks.AcquireAsync(s.Job.JobId, Ctx(Owner));

        await Assert.ThrowsAsync<StaleEditTokenException>(() => service.CompleteAsync(session.PublicId, s.Token, Ctx(Editor)));
        Assert.Equal(UploadStatus.Open, (await service.GetAsync(session.PublicId))!.Status);
        Assert.Empty(await service.ListFilesAsync(s.Job.JobId));
    }

    // ---- cancelling and expiring

    [Fact]
    public async Task Cancelling_frees_the_staged_bytes_and_only_the_starter_or_an_owner_may()
    {
        var s = await JobAsync();
        var service = Uploads();
        var session = await service.BeginAsync(s.Job.JobId, new BeginUpload("a.pdf", 2000, null), s.Token, Ctx(Editor));
        await SendAsync(service, session.PublicId, Pdf(2000), 1000, Ctx(Editor), to: 1000);

        await Assert.ThrowsAsync<JobAccessDeniedException>(() => service.AbortAsync(session.PublicId, Ctx(Viewer)));
        Assert.True(await service.AbortAsync(session.PublicId, Ctx(Owner)));
        Assert.False(await service.AbortAsync(session.PublicId, Ctx(Owner)));

        Assert.Equal(UploadStatus.Aborted, (await service.GetAsync(session.PublicId))!.Status);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "staging"), "*", SearchOption.AllDirectories));
        Assert.Equal(1, await CountEventsAsync(s.Job.JobId, "upload.cancelled"));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => service.AppendAsync(session.PublicId, 1000, new MemoryStream(Pdf(1000)), null, Ctx(Editor)));
    }

    [Fact]
    public async Task An_abandoned_upload_expires_and_its_staging_is_removed_by_the_sweep()
    {
        var s = await JobAsync();
        var service = Uploads(new UploadSettings { ChunkBytes = 1000, SessionLifetime = TimeSpan.Zero });
        var session = await service.BeginAsync(s.Job.JobId, new BeginUpload("a.pdf", 2000, null), s.Token, Ctx(Editor));
        await Task.Delay(30);

        var late = await Assert.ThrowsAsync<JobRuleViolationException>(() => service.AppendAsync(session.PublicId, 0, new MemoryStream(Pdf(1000)), null, Ctx(Editor)));
        Assert.Contains("expired", late.Message);

        Assert.True(await service.ExpireStaleAsync(AuditContext.ForSystem("sweep")) >= 1);
        Assert.Equal(UploadStatus.Expired, (await service.GetAsync(session.PublicId))!.Status);
        Assert.Equal(1, await CountEventsAsync(s.Job.JobId, "upload.expired"));
        Assert.Equal(0, await service.ExpireStaleAsync(AuditContext.ForSystem("sweep")));
    }
}

public sealed class ArtifactStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ductwork-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/../../escape")]
    [InlineData("/absolute")]
    [InlineData("C:/windows")]
    [InlineData("UPPER")]
    [InlineData("has space")]
    [InlineData("a//b")]
    [InlineData("")]
    public async Task A_key_that_could_leave_the_store_is_refused(string key)
    {
        var store = new FileSystemArtifactStore(_root);
        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendStagingAsync(key, 0, new MemoryStream([1]), 10, default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.OpenReadAsync(key, default));
    }

    [Fact]
    public async Task A_missing_file_reads_as_nothing_and_a_second_writer_is_turned_away()
    {
        var store = new FileSystemArtifactStore(_root);
        Assert.Null(await store.OpenReadAsync("jobs/none/x.pdf", default));

        await store.AppendStagingAsync("uploads/one", 0, new MemoryStream([1, 2, 3]), 10, default);
        var path = Path.Combine(_root, "staging", "uploads", "one");
        await using var holder = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => store.AppendStagingAsync("uploads/one", 3, new MemoryStream([4]), 10, default));
    }

    [Fact]
    public async Task Appending_beyond_the_end_of_what_exists_is_a_gap_and_is_refused()
    {
        var store = new FileSystemArtifactStore(_root);
        await store.AppendStagingAsync("uploads/two", 0, new MemoryStream([1, 2, 3]), 10, default);
        var gap = await Assert.ThrowsAsync<UploadOffsetException>(() => store.AppendStagingAsync("uploads/two", 10, new MemoryStream([4]), 10, default));
        Assert.Equal(3, gap.Expected);
    }
}
