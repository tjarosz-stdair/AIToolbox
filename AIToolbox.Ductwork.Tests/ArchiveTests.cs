using System.IO.Compression;
using System.Text;
using AIToolbox.Ductwork.Core.Archive;
using AIToolbox.Ductwork.Core.Drawings;
using AIToolbox.Ductwork.Core.Jobs;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Tests;

/// <summary>
/// Archive, purge and restore against a real engine. The test that matters most is the round trip: archive and purge a job that has
/// rows in every table, restore it, and find the same rows (compared by a hash over all of them) and the same files.
/// </summary>
public sealed class ArchiveTests(SqlFixture db) : IClassFixture<SqlFixture>, IDisposable
{
    private const string Owner = "owner@example.com";
    private const string Editor = "editor@example.com";
    private const string Viewer = "viewer@example.com";
    private const string Manager = "manager@example.com";
    private const string Stranger = "stranger@example.com";

    private readonly string _drawings = Path.Combine(Path.GetTempPath(), "ductwork-arc-d-" + Guid.NewGuid().ToString("N"));
    private readonly string _archives = Path.Combine(Path.GetTempPath(), "ductwork-arc-a-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var dir in new[] { _drawings, _archives })
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
    }

    private Func<CancellationToken, Task<SqlConnection>> Open => async ct =>
    {
        var connection = new SqlConnection(db.ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    };

    private FileSystemArtifactStore DrawingStore => new(_drawings);
    private FileSystemArchiveStore ArchiveStore => new(_archives);

    private ArchiveService Service(ArchiveSettings? settings = null, IArchiveStore? archives = null) =>
        new(Open, DrawingStore, archives ?? ArchiveStore, settings ?? new ArchiveSettings { PurgeBatchRows = 3 });

    private static AuditContext Ctx(string email, bool admin = false) =>
        AuditContext.ForUser(email, displayName: email.Split('@')[0], sessionId: "s-" + email, userAgent: "tests", isAdmin: admin);

    private static readonly AuditContext Runner = AuditContext.ForSystem("test runner");

    private sealed record Seeded(long JobId, string Name, byte[] FileBytes, string StorageKey);

    /// <summary>A closed job with rows in every table the archive covers, a real source file, and a rule that points back at a sheet and a finding.</summary>
    private async Task<Seeded> SeedClosedJobAsync()
    {
        var jobs = new JobRepository(db.ConnectionString);
        var name = "Archive " + Guid.NewGuid().ToString("N")[..8];
        var job = await jobs.CreateAsync(new NewJob { Name = name }, Ctx(Owner));
        await jobs.AddMemberAsync(job.JobId, Editor, JobRole.Editor, Ctx(Owner));
        await jobs.AddMemberAsync(job.JobId, Viewer, JobRole.Viewer, Ctx(Owner));
        await db.ExecAsync("INSERT INTO ductwork.ToolPermission (UserEmail, Permission, GrantedBy) SELECT @e, 'JobManager', @e WHERE NOT EXISTS (SELECT 1 FROM ductwork.ToolPermission WHERE UserEmail = @e AND Permission = 'JobManager');", ("@e", Manager));

        var token = (await new EditLockService(db.ConnectionString).AcquireAsync(job.JobId, Ctx(Editor))).Token!;
        var data = new byte[3000];
        new Random(7).NextBytes(data);
        Encoding.ASCII.GetBytes("%PDF-1.7\n").CopyTo(data, 0);
        var uploads = new UploadService(Open, DrawingStore, new UploadSettings { ChunkBytes = 1000 });
        var session = await uploads.BeginAsync(job.JobId, new BeginUpload("mech.pdf", data.Length, null), token, Ctx(Editor));
        for (var offset = 0; offset < data.Length; offset += 1000)
            await uploads.AppendAsync(session.PublicId, offset, new MemoryStream(data, offset, Math.Min(1000, data.Length - offset)), null, Ctx(Editor));
        var file = await uploads.CompleteAsync(session.PublicId, token, Ctx(Editor));

        var j = ("@j", (object?)job.JobId);
        var producer = await db.ScalarAsync<long>(
            "INSERT INTO ductwork.Producer (ProducerKey, Kind, AlgorithmName, AlgorithmVersion, ParamsHash) OUTPUT inserted.ProducerId VALUES (@k, 'Algorithm', 'counter', '1.0', 'h');", ("@k", "arc-" + Guid.NewGuid().ToString("N")));
        var task = await db.ScalarAsync<long>(
            "INSERT INTO ductwork.ProcessingTask (JobId, TaskType, Status, ProducerId, ContractVersion, CreatedBy, FinishedUtc) OUTPUT inserted.ProcessingTaskId VALUES (@j, 'detect', 'Succeeded', @p, '0.1.0', 'tests', SYSUTCDATETIME());", j, ("@p", producer));
        var type = await db.ScalarAsync<long>("INSERT INTO ductwork.BuildingType (JobId, Name, NameNormalized, CreatedBy) OUTPUT inserted.BuildingTypeId VALUES (@j, 'Type 4', 'type 4', 'tests');", j);
        var revision = await db.ScalarAsync<long>("SELECT RevisionId FROM ductwork.SourceFile WHERE SourceFileId = @f;", ("@f", file.SourceFileId));
        var sheet1 = await db.ScalarAsync<long>("INSERT INTO ductwork.Sheet (JobId, RevisionId, SourceFileId, PageNo, SheetNumber, WidthPts, HeightPts, CreatedBy) OUTPUT inserted.SheetId VALUES (@j, @r, @f, 1, 'M1.1', 2592, 1728, 'tests');", j, ("@r", revision), ("@f", file.SourceFileId));
        await db.ExecAsync("INSERT INTO ductwork.Sheet (JobId, RevisionId, SourceFileId, PageNo, WidthPts, HeightPts, DuplicateOfSheetId, CreatedBy) VALUES (@j, @r, @f, 2, 2592, 1728, @d, 'tests');", j, ("@r", revision), ("@f", file.SourceFileId), ("@d", sheet1));
        var view = await db.ScalarAsync<long>("INSERT INTO ductwork.SheetView (JobId, SheetId, Title, BuildingTypeId, CreatedBy) OUTPUT inserted.SheetViewId VALUES (@j, @s, 'Level 1', @t, 'tests');", j, ("@s", sheet1), ("@t", type));
        await db.ExecAsync("INSERT INTO ductwork.SheetRegion (JobId, SheetId, Kind, Reason, PolygonJson, CreatedBy) VALUES (@j, @s, 'Exclude', 'legend', '[[0,0],[1,0],[1,1]]', 'tests');", j, ("@s", sheet1));

        long firstEntity = 0;
        for (var i = 1; i <= 7; i++)
        {
            var id = await db.ScalarAsync<long>(
                """
                INSERT INTO ductwork.Entity (JobId, BuildingTypeId, ProducerId, ProcessingTaskId, DetectionId, Type, Size, Quantity, Method, Family, Confidence, SurfacedAs, ReviewLevelAtCreation, SheetViewId, BoxX0, BoxY0, BoxX1, BoxY1, Evidence)
                OUTPUT inserted.EntityId VALUES (@j, @t, @p, @k, @d, 'Boot', '4x10x7', 1.5, 'Text', 'LabelFlagCount', 0.8125, 'Batch', 'Sensitive', @v, 1.25, 2.5, 3.75, 4.125, N'label “4x10” — ünïcode');
                """, j, ("@t", type), ("@p", producer), ("@k", task), ("@d", "d" + i), ("@v", view));
            if (i == 1) firstEntity = id;
            await db.ExecAsync("INSERT INTO ductwork.EntityRevision (JobId, EntityId, RevisionNo, Action, ChangedBy, AfterJson) VALUES (@j, @e, 1, 'Added', 'tests', '{}');", j, ("@e", id));
        }

        var finding = await db.ScalarAsync<long>(
            "INSERT INTO ductwork.Finding (JobId, ProcessingTaskId, ProducerId, EntityId, SourceFindingId, Kind, Question, CandidatesJson) OUTPUT inserted.FindingId VALUES (@j, @k, @p, @e, 'f1', 'GrammarVariant', 'depth?', '[\"7\",\"4\"]');", j, ("@k", task), ("@p", producer), ("@e", firstEntity));
        var rule = await db.ScalarAsync<long>(
            """
            INSERT INTO ductwork.InterpretationRule (RuleKey, Version, Scope, JobId, SheetId, OriginJobId, Name, MatchJson, ActionJson, Status, FromFindingId, CreatedBy, ApprovedBy, ApprovedUtc)
            OUTPUT inserted.RuleId VALUES (NEWID(), 1, 'Sheet', @j, @s, @j, 'Depth 7', '{}', '{}', 'Approved', @f, 'tests', 'tests', SYSUTCDATETIME());
            """, j, ("@s", sheet1), ("@f", finding));
        await db.ExecAsync("UPDATE ductwork.Entity SET ResolvedByRuleId = @r WHERE EntityId = @e;", ("@r", rule), ("@e", firstEntity));
        await db.ExecAsync("INSERT INTO ductwork.RuleApplication (JobId, RuleId, EntityId, EntityRevisionNo, BatchId, Change, AppliedBy) VALUES (@j, @r, @e, 1, NEWID(), 'size', 'tests');", j, ("@r", rule), ("@e", firstEntity));

        await new EditLockService(db.ConnectionString).ReleaseAsync(token, Ctx(Editor));
        await jobs.CloseAsync(job.JobId, "Lost the bid.", Ctx(Editor));
        return new Seeded(job.JobId, name, data, file.StorageKey!);
    }

    private async Task<(string Hash, Dictionary<string, long> Counts)> FingerprintAsync(long jobId)
    {
        await using var c = await Open(default);
        return await ArchiveFingerprint.OfAsync(c, jobId);
    }

    private Task<JobRecord> JobAsync(long id) => new JobRepository(db.ConnectionString).GetAsync(id)!;

    private async Task<int> LiveRowsAsync(long jobId)
    {
        var total = 0;
        foreach (var t in new[] { "DrawingSetRevision", "SourceFile", "UploadSession", "Sheet", "SheetView", "SheetRegion", "Entity", "Finding", "EntityRevision", "RuleApplication" })
            total += await db.ScalarAsync<int>($"SELECT COUNT(*) FROM ductwork.{t} WHERE JobId = @j;", ("@j", jobId));
        return total;
    }

    private async Task<ArchiveOperationRecord> ArchiveAsync(Seeded job, ArchiveKind kind, ArchiveService? service = null)
    {
        service ??= Service();
        var op = await service.RequestArchiveAsync(job.JobId, kind, kind == ArchiveKind.Purge ? job.Name : null, Ctx(Owner));
        Assert.True(await service.RunOperationAsync(op.OperationId, "test", Runner));
        return (await service.GetOperationAsync(op.OperationId))!;
    }

    // ---------------------------------------------------------------- the round trip

    [Fact]
    public async Task Archive_and_purge_then_restore_gives_back_exactly_the_same_job()
    {
        var job = await SeedClosedJobAsync();
        var before = await FingerprintAsync(job.JobId);
        Assert.True(before.Counts["Entity"] == 7 && before.Counts["EntityRevision"] == 7 && before.Counts["Sheet"] == 2);
        var service = Service();

        var archived = await ArchiveAsync(job, ArchiveKind.Purge, service);

        Assert.Equal(ArchiveOperationStatus.Succeeded, archived.Status);
        var purged = await JobAsync(job.JobId);
        Assert.Equal((JobState.Archived, JobState.Closed), (purged.State, purged.StateBeforeArchive));
        Assert.Equal(0, await LiveRowsAsync(job.JobId));                                  // the heavy detail is gone
        Assert.False(File.Exists(Path.Combine(_drawings, "final", job.StorageKey.Replace('/', Path.DirectorySeparatorChar))));
        var record = (await service.ListArchivesAsync(job.JobId)).Single();
        Assert.Equal((ArchiveStatus.Purged, before.Hash), (record.Status, record.DataHash));
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.InterpretationRule WHERE JobId = @j;", ("@j", job.JobId)));    // the rule stays
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.BuildingType WHERE JobId = @j;", ("@j", job.JobId)));
        Assert.Contains("\"Entity\":7", await db.ScalarAsync<string>("SELECT SummaryJson FROM ductwork.Job WHERE JobId = @j;", ("@j", job.JobId)));

        var restore = await service.RequestRestoreAsync(job.JobId, Ctx(Owner));
        Assert.Equal(JobState.Restoring, (await JobAsync(job.JobId)).State);
        Assert.True(await service.RunOperationAsync(restore.OperationId, "test", Runner));

        var after = await FingerprintAsync(job.JobId);
        Assert.Equal(before.Hash, after.Hash);                                              // every row, every value, every id
        Assert.Equal(before.Counts, after.Counts);
        var restored = await JobAsync(job.JobId);
        Assert.Equal(JobState.Closed, restored.State);                                      // back as it was: read-only, ready to reopen
        await using (var file = await DrawingStore.OpenReadAsync(job.StorageKey, default))
        {
            Assert.NotNull(file);
            using var ms = new MemoryStream();
            await file!.CopyToAsync(ms);
            Assert.Equal(job.FileBytes, ms.ToArray());
        }

        Assert.Equal(ArchiveStatus.Ready, (await service.ListArchivesAsync(job.JobId)).Single().Status);
        var verify = await new JobRepository(db.ConnectionString).VerifyAuditChainAsync(job.JobId);
        Assert.True(verify.IsValid, "the audit chain survives archive, purge and restore");
        var events = (await new JobRepository(db.ConnectionString).ListAuditAsync(job.JobId, new AuditFilter { Category = "archive" })).Select(e => e.Action).ToList();
        Assert.Equal(["archive.requested", "archive.purged", "restore.requested", "archive.restored"], events);
    }

    [Fact]
    public async Task A_zip_only_archive_keeps_the_data_and_returns_the_job_to_how_it_was()
    {
        var job = await SeedClosedJobAsync();
        var service = Service();

        await ArchiveAsync(job, ArchiveKind.ZipOnly, service);

        Assert.Equal(JobState.Closed, (await JobAsync(job.JobId)).State);
        Assert.True(await LiveRowsAsync(job.JobId) > 0);
        var record = (await service.ListArchivesAsync(job.JobId)).Single();
        Assert.Equal(ArchiveStatus.Ready, record.Status);
        Assert.True(File.Exists(Path.Combine(_archives, record.StorageKey!.Replace('/', Path.DirectorySeparatorChar))));
        Assert.True((await service.CheckArchiveAsync(record.ArchiveId, Ctx(Owner))).Ok);

        // The same version cannot be archived twice, and it cannot be "restored" because nothing was removed.
        var again = await Assert.ThrowsAsync<JobRuleViolationException>(() => service.RequestArchiveAsync(job.JobId, ArchiveKind.ZipOnly, null, Ctx(Owner)));
        Assert.Contains("already has an archive", again.Message);
        await Assert.ThrowsAsync<JobRuleViolationException>(() => service.RequestRestoreAsync(job.JobId, Ctx(Owner)));
    }

    [Fact]
    public async Task The_zip_is_self_describing_and_holds_every_source_file()
    {
        var job = await SeedClosedJobAsync();
        var service = Service();
        await ArchiveAsync(job, ArchiveKind.ZipOnly, service);
        var record = (await service.ListArchivesAsync(job.JobId)).Single();

        using var zip = ZipFile.OpenRead(Path.Combine(_archives, record.StorageKey!.Replace('/', Path.DirectorySeparatorChar)));
        var names = zip.Entries.Select(e => e.FullName).ToList();

        Assert.Contains("manifest.json", names);
        Assert.Contains("data/Entity.jsonl", names);
        Assert.Contains("reference/InterpretationRule.jsonl", names);
        Assert.Contains("provenance/Producer.jsonl", names);
        Assert.Contains("tags.json", names);
        Assert.Single(names, n => n.StartsWith("sources/"));
        using var entities = new StreamReader(zip.GetEntry("data/Entity.jsonl")!.Open());
        Assert.Equal(7, (await entities.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Matches(@"^ductwork-\d+-v\d+-\d{8}-[0-9a-f]{8}\.zip$", record.FileName!);
        Assert.Equal(1, record.SourceFileCount);
    }

    // ---------------------------------------------------------------- gates: nothing is removed unless everything checks

    [Fact]
    public async Task A_zip_that_is_damaged_after_writing_stops_the_purge_before_anything_is_removed()
    {
        var job = await SeedClosedJobAsync();
        var before = await FingerprintAsync(job.JobId);
        var tamper = new TamperingStore(ArchiveStore);
        var service = Service(archives: tamper);

        var op = await service.RequestArchiveAsync(job.JobId, ArchiveKind.Purge, job.Name, Ctx(Owner));
        Assert.True(await service.RunOperationAsync(op.OperationId, "test", Runner));

        var failed = (await service.GetOperationAsync(op.OperationId))!;
        Assert.Equal(ArchiveOperationStatus.Failed, failed.Status);
        Assert.Contains("verification", failed.Error);
        Assert.Equal(before.Hash, (await FingerprintAsync(job.JobId)).Hash);               // every row is still there
        Assert.True(File.Exists(Path.Combine(_drawings, "final", job.StorageKey.Replace('/', Path.DirectorySeparatorChar))));
        var back = await JobAsync(job.JobId);
        Assert.Equal(JobState.Closed, back.State);                                          // the job is usable again
        var record = (await service.ListArchivesAsync(job.JobId)).Single();
        Assert.Equal(ArchiveStatus.Failed, record.Status);
        Assert.False(await ArchiveStore.ExistsAsync(record.StorageKey!, default));          // the unproven zip was discarded

        // And a later, clean attempt works.
        await ArchiveAsync(job, ArchiveKind.ZipOnly);
        Assert.Equal(JobState.Closed, (await JobAsync(job.JobId)).State);
    }

    [Fact]
    public async Task A_job_that_changed_while_it_was_being_archived_is_not_purged()
    {
        var job = await SeedClosedJobAsync();
        var before = await FingerprintAsync(job.JobId);
        var service = Service();
        var op = await service.RequestArchiveAsync(job.JobId, ArchiveKind.Purge, job.Name, Ctx(Owner));

        // Something writes a row after the zip is made but before it is checked against the database.
        var store = new HookStore(ArchiveStore, async () =>
            await db.ExecAsync("UPDATE ductwork.Entity SET Quantity = 99 WHERE JobId = @j AND DetectionId = 'd3';", ("@j", job.JobId)));
        service = Service(archives: store);
        Assert.True(await service.RunOperationAsync(op.OperationId, "test", Runner));

        Assert.Equal(ArchiveOperationStatus.Failed, (await service.GetOperationAsync(op.OperationId))!.Status);
        Assert.True(await LiveRowsAsync(job.JobId) > 0);
        Assert.NotEqual(before.Hash, (await FingerprintAsync(job.JobId)).Hash);             // the change is still there, and nothing was removed
        Assert.Equal(JobState.Closed, (await JobAsync(job.JobId)).State);
    }

    [Fact]
    public async Task Purging_needs_the_job_name_typed_and_a_frozen_job_without_running_work()
    {
        var job = await SeedClosedJobAsync();
        var service = Service();

        await Assert.ThrowsAsync<JobRuleViolationException>(() => service.RequestArchiveAsync(job.JobId, ArchiveKind.Purge, "wrong", Ctx(Owner)));
        await Assert.ThrowsAsync<JobRuleViolationException>(() => service.RequestArchiveAsync(job.JobId, ArchiveKind.Purge, null, Ctx(Owner)));
        Assert.Equal(JobState.Closed, (await JobAsync(job.JobId)).State);

        await db.ExecAsync("INSERT INTO ductwork.ProcessingTask (JobId, TaskType, Status, ProducerId, ContractVersion, CreatedBy) SELECT TOP 1 @j, 'detect', 'Queued', ProducerId, '0.1.0', 'tests' FROM ductwork.Producer;", ("@j", job.JobId));
        var busy = await Assert.ThrowsAsync<JobRuleViolationException>(() => service.RequestArchiveAsync(job.JobId, ArchiveKind.Purge, job.Name.ToUpperInvariant(), Ctx(Owner)));
        Assert.Contains("still running", busy.Message);
    }

    [Fact]
    public async Task A_job_that_is_not_complete_or_closed_cannot_be_archived()
    {
        var jobs = new JobRepository(db.ConnectionString);
        var draft = await jobs.CreateAsync(new NewJob { Name = "Draft " + Guid.NewGuid().ToString("N")[..6] }, Ctx(Owner));

        var refused = await Assert.ThrowsAsync<JobRuleViolationException>(() => Service().RequestArchiveAsync(draft.JobId, ArchiveKind.ZipOnly, null, Ctx(Owner)));

        Assert.Contains("Complete or Closed", refused.Message);
    }

    // ---------------------------------------------------------------- who may

    [Fact]
    public async Task Only_an_owner_or_a_job_manager_may_archive_or_restore_and_a_manager_needs_no_membership()
    {
        var job = await SeedClosedJobAsync();
        var service = Service();

        foreach (var who in new[] { Editor, Viewer, Stranger })
            await Assert.ThrowsAsync<JobAccessDeniedException>(() => service.RequestArchiveAsync(job.JobId, ArchiveKind.ZipOnly, null, Ctx(who)));

        var op = await service.RequestArchiveAsync(job.JobId, ArchiveKind.Purge, job.Name, Ctx(Manager));      // not a member, but a Job Manager
        Assert.True(await service.RunOperationAsync(op.OperationId, "test", Runner));
        foreach (var who in new[] { Editor, Viewer, Stranger })
            await Assert.ThrowsAsync<JobAccessDeniedException>(() => service.RequestRestoreAsync(job.JobId, Ctx(who)));
        var restore = await service.RequestRestoreAsync(job.JobId, Ctx(Manager));
        Assert.True(await service.RunOperationAsync(restore.OperationId, "test", Runner));
        Assert.Equal(JobState.Closed, (await JobAsync(job.JobId)).State);
    }

    [Fact]
    public async Task While_archiving_or_archived_a_job_cannot_be_edited_or_reopened()
    {
        var job = await SeedClosedJobAsync();
        var service = Service();
        var op = await service.RequestArchiveAsync(job.JobId, ArchiveKind.Purge, job.Name, Ctx(Owner));

        var reopen = await Assert.ThrowsAsync<JobRuleViolationException>(() => new JobRepository(db.ConnectionString).ReopenAsync(job.JobId, "again", Ctx(Owner)));
        Assert.Contains("Archiving", reopen.Message);
        await Assert.ThrowsAsync<JobRuleViolationException>(() => service.RequestArchiveAsync(job.JobId, ArchiveKind.ZipOnly, null, Ctx(Owner)));

        Assert.True(await service.RunOperationAsync(op.OperationId, "test", Runner));
        var archived = await Assert.ThrowsAsync<JobRuleViolationException>(() => new JobRepository(db.ConnectionString).ReopenAsync(job.JobId, "again", Ctx(Owner)));
        Assert.Contains("restored", archived.Message);
    }

    [Fact]
    public async Task Reopening_marks_the_old_archive_out_of_date_but_keeps_it()
    {
        var job = await SeedClosedJobAsync();
        var service = Service();
        await ArchiveAsync(job, ArchiveKind.ZipOnly, service);

        await new JobRepository(db.ConnectionString).ReopenAsync(job.JobId, "Won it back.", Ctx(Editor));

        var record = (await service.ListArchivesAsync(job.JobId)).Single();
        Assert.Equal(ArchiveStatus.Superseded, record.Status);
        Assert.NotNull(record.SupersededUtc);
        Assert.True(await ArchiveStore.ExistsAsync(record.StorageKey!, default));           // never deleted
    }

    // ---------------------------------------------------------------- restore failures

    [Fact]
    public async Task A_missing_zip_stops_the_restore_with_a_plain_message_and_leaves_the_job_archived()
    {
        var job = await SeedClosedJobAsync();
        var service = Service();
        await ArchiveAsync(job, ArchiveKind.Purge, service);
        var record = (await service.ListArchivesAsync(job.JobId)).Single();
        var original = Path.Combine(_archives, record.StorageKey!.Replace('/', Path.DirectorySeparatorChar));
        var copy = await File.ReadAllBytesAsync(original);
        File.Delete(original);

        var restore = await service.RequestRestoreAsync(job.JobId, Ctx(Owner));
        Assert.True(await service.RunOperationAsync(restore.OperationId, "test", Runner));

        var op = (await service.GetOperationAsync(restore.OperationId))!;
        Assert.Equal((ArchiveOperationStatus.Failed, "Archive file not found. Contact your administrator."), (op.Status, op.Error));
        Assert.Equal(JobState.Archived, (await JobAsync(job.JobId)).State);
        Assert.Equal(0, await LiveRowsAsync(job.JobId));                                    // no partial restore
        var problem = (await service.ListArchivesAsync(job.JobId)).Single();
        Assert.Contains(record.StorageKey!, problem.LastProblem);                           // the administrator sees where it was and what it hashed to
        Assert.Contains(record.Sha256!, problem.LastProblem);
        Assert.Contains("restore.failed", (await new JobRepository(db.ConnectionString).ListAuditAsync(job.JobId, new AuditFilter { Category = "archive" })).Select(e => e.Action));

        // Put it back (an administrator relinks it) and the restore goes through.
        var moved = "moved/" + record.FileName;
        Directory.CreateDirectory(Path.Combine(_archives, "moved"));
        await File.WriteAllBytesAsync(Path.Combine(_archives, moved.Replace('/', Path.DirectorySeparatorChar)), copy);
        var relinked = await service.RelinkArchiveAsync(record.ArchiveId, moved, Ctx("admin@example.com", admin: true));
        Assert.Null(relinked.LastProblem);
        Assert.Equal(2, relinked.Locations.Count);
        var retry = await service.RequestRestoreAsync(job.JobId, Ctx(Owner));
        Assert.True(await service.RunOperationAsync(retry.OperationId, "test", Runner));
        Assert.Equal(JobState.Closed, (await JobAsync(job.JobId)).State);
    }

    [Fact]
    public async Task A_restore_refuses_when_the_job_already_has_live_rows_and_changes_nothing()
    {
        var job = await SeedClosedJobAsync();
        var service = Service();
        await ArchiveAsync(job, ArchiveKind.Purge, service);
        var restore = await service.RequestRestoreAsync(job.JobId, Ctx(Owner));
        // Something put a row back by hand while the restore was queued.
        await db.ExecAsync("INSERT INTO ductwork.DrawingSetRevision (JobId, RevisionNo, CreatedBy) VALUES (@j, 1, 'tests');", ("@j", job.JobId));

        Assert.True(await service.RunOperationAsync(restore.OperationId, "test", Runner));

        var op = (await service.GetOperationAsync(restore.OperationId))!;
        Assert.Equal(ArchiveOperationStatus.Failed, op.Status);
        Assert.Contains("already has", op.Error);
        Assert.Equal(JobState.Archived, (await JobAsync(job.JobId)).State);
        Assert.Equal(1, await LiveRowsAsync(job.JobId));
    }

    // ---------------------------------------------------------------- crash and resume

    [Fact]
    public async Task A_purge_stopped_part_way_resumes_where_it_was_and_the_result_is_still_exactly_restorable()
    {
        var job = await SeedClosedJobAsync();
        var before = await FingerprintAsync(job.JobId);
        var op = await Service().RequestArchiveAsync(job.JobId, ArchiveKind.Purge, job.Name, Ctx(Owner));

        // The service is stopped after the second batch of deletes (a deploy, a restart).
        using var cts = new CancellationTokenSource();
        var stopping = Service(new ArchiveSettings { PurgeBatchRows = 3, AfterPurgeBatch = n => { if (n == 2) cts.Cancel(); } });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping.RunOperationAsync(op.OperationId, "first-runner", Runner, cts.Token));

        var partway = await LiveRowsAsync(job.JobId);
        Assert.InRange(partway, 1, (int)(before.Counts.Values.Sum() - 1));                 // some gone, some left
        Assert.Equal(JobState.Archiving, (await JobAsync(job.JobId)).State);
        Assert.Equal(ArchiveOperationStatus.Queued, (await Service().GetOperationAsync(op.OperationId))!.Status);
        var record = (await Service().ListArchivesAsync(job.JobId)).Single();
        Assert.Equal(ArchiveStatus.PurgePending, record.Status);                           // the zip was proven before the first delete

        // Another run finishes it without writing a second zip.
        Assert.True(await Service().RunOperationAsync(op.OperationId, "second-runner", Runner));
        Assert.Equal(0, await LiveRowsAsync(job.JobId));
        Assert.Equal(JobState.Archived, (await JobAsync(job.JobId)).State);
        Assert.Single(await Service().ListArchivesAsync(job.JobId));

        var restore = await Service().RequestRestoreAsync(job.JobId, Ctx(Owner));
        Assert.True(await Service().RunOperationAsync(restore.OperationId, "test", Runner));
        Assert.Equal(before.Hash, (await FingerprintAsync(job.JobId)).Hash);
    }

    [Fact]
    public async Task An_operation_whose_runner_died_is_picked_up_once_its_lease_runs_out()
    {
        var job = await SeedClosedJobAsync();
        var service = Service();
        var op = await service.RequestArchiveAsync(job.JobId, ArchiveKind.ZipOnly, null, Ctx(Owner));
        await db.ExecAsync("UPDATE ductwork.ArchiveOperation SET Status = 'Running', Attempt = 1, LeaseOwner = 'dead-runner', LeaseExpiresUtc = DATEADD(MINUTE, 5, SYSUTCDATETIME()) WHERE OperationId = @o;", ("@o", op.OperationId));

        Assert.False(await service.RunOperationAsync(op.OperationId, "live-runner", Runner));   // still leased: hands off

        await db.ExecAsync("UPDATE ductwork.ArchiveOperation SET LeaseExpiresUtc = DATEADD(MINUTE, -1, SYSUTCDATETIME()) WHERE OperationId = @o;", ("@o", op.OperationId));
        Assert.True(await service.RunOperationAsync(op.OperationId, "live-runner", Runner));

        var done = (await service.GetOperationAsync(op.OperationId))!;
        Assert.Equal((ArchiveOperationStatus.Succeeded, 2), (done.Status, done.Attempt));
        Assert.Equal(JobState.Closed, (await JobAsync(job.JobId)).State);
    }

    [Fact]
    public async Task An_operation_that_keeps_dying_is_given_up_on_and_the_job_is_released()
    {
        var job = await SeedClosedJobAsync();
        var service = Service();
        var op = await service.RequestArchiveAsync(job.JobId, ArchiveKind.ZipOnly, null, Ctx(Owner));
        await db.ExecAsync("UPDATE ductwork.ArchiveOperation SET Status = 'Running', Attempt = MaxAttempts, LeaseOwner = 'dead-runner', LeaseExpiresUtc = DATEADD(MINUTE, -1, SYSUTCDATETIME()) WHERE OperationId = @o;", ("@o", op.OperationId));

        await service.RunNextAsync("live-runner", Runner);                                  // the stuck one is not run again; it is failed

        Assert.Equal(ArchiveOperationStatus.Failed, (await service.GetOperationAsync(op.OperationId))!.Status);
        Assert.Equal(JobState.Closed, (await JobAsync(job.JobId)).State);                   // not stuck Archiving forever
    }

    [Fact]
    public async Task The_item_history_can_only_be_deleted_by_a_verified_purge_of_that_job()
    {
        var job = await SeedClosedJobAsync();
        var other = await SeedClosedJobAsync();

        // Outside a purge: refused, as before.
        var plain = await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync("DELETE FROM ductwork.EntityRevision WHERE JobId = @j;", ("@j", job.JobId)));
        Assert.Equal(51013, plain.Number);

        // Naming the job is not enough: it must be Archiving with a verified, PurgePending archive.
        var named = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var c = await Open(default);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "EXEC sp_set_session_context @key = N'ductwork.purge_job_id', @value = @j; DELETE FROM ductwork.EntityRevision WHERE JobId = @j;";
            cmd.Parameters.AddWithValue("@j", job.JobId);
            await cmd.ExecuteNonQueryAsync();
        });
        Assert.Equal(51013, named.Number);

        // And a purge of one job cannot reach another job's history.
        var service = Service();
        await service.RequestArchiveAsync(other.JobId, ArchiveKind.Purge, other.Name, Ctx(Owner));
        await db.ExecAsync("UPDATE ductwork.JobArchive SET Status = 'PurgePending', StorageKey = 'k/x.zip', SizeBytes = 1, Sha256 = CONVERT(BINARY(32), 0), DataHash = CONVERT(BINARY(32), 0) WHERE JobId = @j;", ("@j", other.JobId));
        var cross = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var c = await Open(default);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "EXEC sp_set_session_context @key = N'ductwork.purge_job_id', @value = @o; DELETE FROM ductwork.EntityRevision WHERE JobId = @j;";
            cmd.Parameters.AddWithValue("@o", other.JobId);
            cmd.Parameters.AddWithValue("@j", job.JobId);
            await cmd.ExecuteNonQueryAsync();
        });
        Assert.Equal(51013, cross.Number);
        Assert.Equal(7, await db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.EntityRevision WHERE JobId = @j;", ("@j", job.JobId)));

        // Leave nothing queued behind for other tests to pick up: the second job's operation was only needed to set the scene.
        await db.ExecAsync("UPDATE ductwork.ArchiveOperation SET Status = 'Failed', Error = 'test scaffolding', FinishedUtc = SYSUTCDATETIME() WHERE JobId = @j AND Status = 'Queued';", ("@j", other.JobId));
    }

    [Fact]
    public async Task Checking_an_archive_that_has_gone_records_the_problem_for_the_administrator()
    {
        var job = await SeedClosedJobAsync();
        var service = Service();
        await ArchiveAsync(job, ArchiveKind.ZipOnly, service);
        var record = (await service.ListArchivesAsync(job.JobId)).Single();
        File.Delete(Path.Combine(_archives, record.StorageKey!.Replace('/', Path.DirectorySeparatorChar)));

        var result = await service.CheckArchiveAsync(record.ArchiveId, Ctx("admin@example.com", admin: true));

        Assert.False(result.Ok);
        Assert.Contains("not found", (await service.GetArchiveAsync(record.ArchiveId))!.LastProblem);
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => service.CheckArchiveAsync(record.ArchiveId, Ctx(Stranger)));
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => service.RelinkArchiveAsync(record.ArchiveId, "x/y.zip", Ctx(Owner)));
    }

    [Fact]
    public async Task A_zip_changed_on_disk_after_it_was_recorded_fails_its_check_and_cannot_be_restored_from()
    {
        var job = await SeedClosedJobAsync();
        var service = Service();
        await ArchiveAsync(job, ArchiveKind.Purge, service);
        var record = (await service.ListArchivesAsync(job.JobId)).Single();
        var path = Path.Combine(_archives, record.StorageKey!.Replace('/', Path.DirectorySeparatorChar));
        await using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            file.Position = file.Length / 3;
            var b = file.ReadByte();
            file.Position = file.Length / 3;
            file.WriteByte((byte)(b ^ 0x5A));
        }

        var check = await service.CheckArchiveAsync(record.ArchiveId, Ctx("admin@example.com", admin: true));
        Assert.False(check.Ok);
        Assert.Contains("hash", check.Problems[0]);

        var restore = await service.RequestRestoreAsync(job.JobId, Ctx(Owner));
        Assert.True(await service.RunOperationAsync(restore.OperationId, "test", Runner));
        Assert.Equal(ArchiveOperationStatus.Failed, (await service.GetOperationAsync(restore.OperationId))!.Status);
        Assert.Equal(JobState.Archived, (await JobAsync(job.JobId)).State);
        Assert.Equal(0, await LiveRowsAsync(job.JobId));
    }

    // ---------------------------------------------------------------- test doubles

    /// <summary>Writes the zip, then flips a byte in it, as if the disk or the copy had damaged it.</summary>
    private sealed class TamperingStore(IArchiveStore inner) : IArchiveStore
    {
        public async Task WriteAsync(string key, Func<Stream, Task> write, CancellationToken ct)
        {
            await inner.WriteAsync(key, write, ct);
            await using var stream = await inner.OpenReadAsync(key, ct);
            var path = ((FileStream)stream!).Name;
            await stream.DisposeAsync();
            await using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
            file.Position = file.Length / 2;
            var b = file.ReadByte();
            file.Position = file.Length / 2;
            file.WriteByte((byte)(b ^ 0xFF));
        }

        public Task<Stream?> OpenReadAsync(string key, CancellationToken ct) => inner.OpenReadAsync(key, ct);
        public Task<bool> ExistsAsync(string key, CancellationToken ct) => inner.ExistsAsync(key, ct);
        public Task DeleteAsync(string key, CancellationToken ct) => inner.DeleteAsync(key, ct);
    }

    private sealed class HookStore(IArchiveStore inner, Func<Task> afterWrite) : IArchiveStore
    {
        public async Task WriteAsync(string key, Func<Stream, Task> write, CancellationToken ct)
        {
            await inner.WriteAsync(key, write, ct);
            await afterWrite();
        }

        public Task<Stream?> OpenReadAsync(string key, CancellationToken ct) => inner.OpenReadAsync(key, ct);
        public Task<bool> ExistsAsync(string key, CancellationToken ct) => inner.ExistsAsync(key, ct);
        public Task DeleteAsync(string key, CancellationToken ct) => inner.DeleteAsync(key, ct);
    }
}
