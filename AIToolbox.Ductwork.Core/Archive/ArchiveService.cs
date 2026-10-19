using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AIToolbox.Ductwork.Core.Drawings;
using AIToolbox.Ductwork.Core.Jobs;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Archive;

/// <summary>
/// Archive, purge and restore (plan section 8.3). A request freezes the job (Archiving or Restoring) and queues an operation; a
/// runner does the work in steps that are recorded as they finish, so a crash resumes where it stopped. Nothing is removed unless
/// the zip has been read back and has been proven to hold exactly what is in SQL; a purge that is interrupted is finished, never
/// abandoned half-way, and the verified zip means a half-purged job is still restorable.
///
/// The work runs on the server, not on a worker: it needs the database and the stores, which a worker is deliberately never given.
/// </summary>
public sealed class ArchiveService
{
    private static readonly string[] OperationColumns =
        ["OperationId", "PublicId", "JobId", "ArchiveId", "Kind", "Status", "Step", "Message", "ProgressDone", "ProgressTotal", "Attempt", "MaxAttempts", "RequestedBy", "RequestedUtc", "StartedUtc", "FinishedUtc", "Error"];

    private readonly Func<CancellationToken, Task<SqlConnection>> _open;
    private readonly IArtifactStore _drawings;
    private readonly IArchiveStore _archives;
    private readonly ArchiveSettings _settings;
    private readonly ICompletionProbe _probe;

    public ArchiveService(Func<CancellationToken, Task<SqlConnection>> open, IArtifactStore drawings, IArchiveStore archives, ArchiveSettings? settings = null, ICompletionProbe? probe = null)
    {
        _open = open;
        _drawings = drawings;
        _archives = archives;
        _settings = settings ?? new ArchiveSettings();
        _probe = probe ?? new Counting.JobCompletionProbe();
    }

    // ------------------------------------------------------------------ reads

    public async Task<ArchiveRecord?> GetArchiveAsync(long archiveId, CancellationToken ct = default)
    {
        await using var c = await _open(ct);
        return (await ReadArchivesAsync(c, null, "WHERE ArchiveId = @a", [("@a", archiveId)], ct)).FirstOrDefault();
    }

    /// <summary>A job's archives, newest first.</summary>
    public async Task<IReadOnlyList<ArchiveRecord>> ListArchivesAsync(long jobId, CancellationToken ct = default)
    {
        await using var c = await _open(ct);
        return await ReadArchivesAsync(c, null, "WHERE JobId = @j", [("@j", jobId)], ct);
    }

    /// <summary>Every archive on the instance with its job's name, for the administrator's Archives page.</summary>
    public async Task<IReadOnlyList<(ArchiveRecord Archive, string JobName, Guid JobPublicId)>> ListAllArchivesAsync(int limit = 500, CancellationToken ct = default)
    {
        await using var c = await _open(ct);
        var archives = await ReadArchivesAsync(c, null, "", [], ct, limit);
        var names = new Dictionary<long, (string, Guid)>();
        await using var command = c.CreateCommand();
        command.CommandText = "SELECT JobId, Name, PublicId FROM ductwork.Job WHERE JobId IN (SELECT DISTINCT JobId FROM ductwork.JobArchive);";
        await using (var r = await command.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) names[r.GetInt64(0)] = (r.GetString(1), r.GetGuid(2));
        return archives.Select(a => (a, names[a.JobId].Item1, names[a.JobId].Item2)).ToList();
    }

    public async Task<ArchiveOperationRecord?> GetOperationAsync(long operationId, CancellationToken ct = default)
    {
        await using var c = await _open(ct);
        return (await ReadOperationsAsync(c, null, "WHERE OperationId = @o", [("@o", operationId)], ct)).FirstOrDefault();
    }

    /// <summary>The job's most recent operations, newest first. The first is the one in progress, if any.</summary>
    public async Task<IReadOnlyList<ArchiveOperationRecord>> ListOperationsAsync(long jobId, int limit = 10, CancellationToken ct = default)
    {
        await using var c = await _open(ct);
        return await ReadOperationsAsync(c, null, "WHERE JobId = @j", [("@j", jobId)], ct, limit);
    }

    /// <summary>The most recent operations across all jobs, newest first, for the administrator's and the Job Manager's overview.</summary>
    public async Task<IReadOnlyList<ArchiveOperationRecord>> ListRecentOperationsAsync(int limit = 500, CancellationToken ct = default)
    {
        await using var c = await _open(ct);
        return await ReadOperationsAsync(c, null, "", [], ct, limit);
    }

    // ------------------------------------------------------------------ requests

    /// <summary>Freezes a Complete or Closed job and queues making its archive. A purge also removes the job's detail rows once the zip is proven, so it needs the job's name typed to confirm.</summary>
    public Task<ArchiveOperationRecord> RequestArchiveAsync(long jobId, ArchiveKind kind, string? confirmName, AuditContext context, CancellationToken ct = default) =>
        JobTxn.RunAsync(_open, jobId, context, ct, async t =>
        {
            await t.RequireAsync(JobCapability.Archive);
            var state = t.Job.State;
            if (state is not (JobState.Complete or JobState.Closed))
                throw new JobRuleViolationException(state switch
                {
                    JobState.Archived => "This job is already archived. Restore it first to archive it again.",
                    JobState.Archiving or JobState.Restoring => $"This job is {state} and is being held still.",
                    _ => "Only a Complete or Closed job can be archived. Complete or close it first."
                });

            if (kind == ArchiveKind.Purge && !string.Equals((confirmName ?? "").Trim(), t.Job.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new JobRuleViolationException("To remove the job's detail from the database, type the job's name to confirm.");

            var (active, _) = await _probe.GetAsync(t.Connection, t.Transaction, jobId, t.Token);
            if (active > 0) throw new JobRuleViolationException($"{active} processing task(s) are still running. Wait for them or cancel them first.");

            if (kind == ArchiveKind.ZipOnly && await ScalarAsync<int>(t, "SELECT COUNT(*) FROM ductwork.JobArchive WHERE JobId = @j AND CompletionVersion = @v AND Status IN ('Ready', 'PurgePending', 'Purged');", ("@v", t.Job.CompletionVersion)) > 0)
                throw new JobRuleViolationException("This version of the job already has an archive. Reopen the job to change it, or archive and purge it to free the space.");

            await JobRepository.ExecAsync(t.Connection, t.Transaction, "DELETE FROM ductwork.JobEditLock WHERE JobId = @j;", t.Token, ("@j", jobId));

            var archiveId = await ScalarAsync<long>(t,
                """
                INSERT INTO ductwork.JobArchive (JobId, CompletionVersion, Kind, StateAtArchive, FormatVersion, ContractVersion, CreatedBy)
                OUTPUT inserted.ArchiveId
                VALUES (@j, @v, @k, @s, @f, @c, @by);
                """, ("@v", t.Job.CompletionVersion), ("@k", kind.ToString()), ("@s", state.ToString()), ("@f", ArchiveSchema.FormatVersion),
                ("@c", Contracts.ContractInfo.Version), ("@by", JobRepository.Actor(t.Context)));

            var operationId = await InsertOperationAsync(t, archiveId, kind == ArchiveKind.Purge ? ArchiveOperationKind.ArchivePurge : ArchiveOperationKind.ArchiveZip);

            var version = await t.BumpAsync("State = 'Archiving', StateBeforeArchive = @s", ("@s", state.ToString()));
            await t.AuditAsync(version, new AuditEntry("archive", "archive.requested",
                kind == ArchiveKind.Purge ? "Asked for the job to be archived and its detail removed from the database." : "Asked for the job to be archived (a copy; the data stays in the database).",
                "Archive", archiveId.ToString(), JobRepository.Json(new() { ["state"] = state.ToString() }), JobRepository.Json(new() { ["state"] = "Archiving", ["kind"] = kind.ToString() })));
            return (await ReadOperationsAsync(t.Connection, t.Transaction, "WHERE OperationId = @o", [("@o", operationId)], t.Token)).Single();
        }, refuseIfLockedByAnother: true, requires: JobCapability.Archive);

    /// <summary>Freezes an Archived job and queues putting its detail back from the zip. Refused if the job has live detail rows.</summary>
    public Task<ArchiveOperationRecord> RequestRestoreAsync(long jobId, AuditContext context, CancellationToken ct = default) =>
        JobTxn.RunAsync(_open, jobId, context, ct, async t =>
        {
            await t.RequireAsync(JobCapability.Restore);
            if (t.Job.State != JobState.Archived)
                throw new JobRuleViolationException(t.Job.State is JobState.Complete or JobState.Closed
                    ? "This job's data is still in the database, so there is nothing to restore."
                    : $"Only an archived job can be restored, not one that is {t.Job.State}.");

            var archiveId = await ScalarAsync<long?>(t, "SELECT TOP 1 ArchiveId FROM ductwork.JobArchive WHERE JobId = @j AND Status = 'Purged' ORDER BY ArchiveId DESC;")
                ?? throw new JobRuleViolationException("This job has no archive to restore from. Contact your administrator.");

            var operationId = await InsertOperationAsync(t, archiveId, ArchiveOperationKind.Restore);
            var version = await t.BumpAsync("State = 'Restoring'");
            await t.AuditAsync(version, new AuditEntry("archive", "restore.requested", "Asked for the job to be restored from its archive.", "Archive", archiveId.ToString(),
                JobRepository.Json(new() { ["state"] = "Archived" }), JobRepository.Json(new() { ["state"] = "Restoring" })));
            return (await ReadOperationsAsync(t.Connection, t.Transaction, "WHERE OperationId = @o", [("@o", operationId)], t.Token)).Single();
        }, requires: JobCapability.Restore);

    /// <summary>Puts a purge that gave up back in the queue. The zip is already proven, so this only finishes removing the detail.</summary>
    public async Task<ArchiveOperationRecord> RetryOperationAsync(long operationId, AuditContext context, CancellationToken ct = default)
    {
        var op = await GetOperationAsync(operationId, ct) ?? throw new KeyNotFoundException("That operation does not exist.");
        return await JobTxn.RunAsync(_open, op.JobId, context, ct, async t =>
        {
            if (!context.IsAdmin) await t.RequireAsync(JobCapability.Archive);
            var current = (await ReadOperationsAsync(t.Connection, t.Transaction, "WHERE OperationId = @o", [("@o", operationId)], t.Token)).Single();
            if (current.Status != ArchiveOperationStatus.Failed || current.Kind != ArchiveOperationKind.ArchivePurge || t.Job.State != JobState.Archiving)
                throw new JobRuleViolationException("Only a purge that stopped part-way can be retried.");

            await JobRepository.ExecAsync(t.Connection, t.Transaction,
                "UPDATE ductwork.ArchiveOperation SET Status = 'Queued', Attempt = 0, Error = NULL, FinishedUtc = NULL, Message = 'Retrying.' WHERE OperationId = @o;", t.Token, ("@o", operationId));
            await t.AuditAtCurrentVersionAsync(new AuditEntry("archive", "archive.retry", "Retried finishing the purge.", "Archive", current.ArchiveId.ToString()));
            return (await ReadOperationsAsync(t.Connection, t.Transaction, "WHERE OperationId = @o", [("@o", operationId)], t.Token)).Single();
        });
    }

    // ------------------------------------------------------------------ administration

    /// <summary>Reads the zip back from where it is and checks it. A zip that cannot be found or does not check out is recorded as a problem for the administrator to see.</summary>
    public async Task<ArchiveVerification> CheckArchiveAsync(long archiveId, AuditContext context, CancellationToken ct = default)
    {
        var archive = await GetArchiveAsync(archiveId, ct) ?? throw new KeyNotFoundException("That archive does not exist.");
        await RequireArchiveRightAsync(archive.JobId, context, JobCapability.Archive, ct);
        if (archive.StorageKey is null) throw new JobRuleViolationException("That archive was never completed, so there is nothing to check.");

        ArchiveVerification result;
        try { (_, result) = await ArchiveReader.InspectAsync(_archives, archive.StorageKey, archive.Sha256 is null ? null : Convert.FromHexString(archive.Sha256), archive.SizeBytes, ct); }
        catch (ArchiveMissingException) { result = ArchiveVerification.Failed("Archive file not found."); }

        await RecordProblemAsync(archive, result.Ok ? null : string.Join(" ", result.Problems.Take(3)), result.Ok ? "archive.checked" : "archive.check-failed",
            result.Ok ? "Checked the archive: it is intact." : "Checked the archive: " + string.Join(" ", result.Problems.Take(3)), context, ct);
        return result;
    }

    /// <summary>Points an archive at a zip that was moved outside the app, after proving the file there is the same one.</summary>
    public async Task<ArchiveRecord> RelinkArchiveAsync(long archiveId, string newStorageKey, AuditContext context, CancellationToken ct = default)
    {
        if (!context.IsAdmin) throw new JobAccessDeniedException("Only an administrator can relink an archive.");
        var archive = await GetArchiveAsync(archiveId, ct) ?? throw new KeyNotFoundException("That archive does not exist.");
        if (archive.StorageKey is null || archive.Sha256 is null) throw new JobRuleViolationException("That archive was never completed, so it cannot be relinked.");
        if (!FileSystemArchiveStore.IsValidKey(newStorageKey)) throw new ArgumentException("That is not a valid archive location.");

        var (_, result) = await ArchiveReader.InspectAsync(_archives, newStorageKey, Convert.FromHexString(archive.Sha256), archive.SizeBytes, ct);
        if (!result.Ok) throw new ArchiveCorruptException(result.Problems);

        return await JobTxn.RunAsync(_open, archive.JobId, context, ct, async t =>
        {
            var history = archive.Locations.Append(new ArchiveLocation(newStorageKey, DateTime.UtcNow, JobRepository.Actor(context), $"Relinked from {archive.StorageKey}")).ToList();
            await JobRepository.ExecAsync(t.Connection, t.Transaction,
                "UPDATE ductwork.JobArchive SET StorageKey = @k, FileName = @n, LocationHistoryJson = @h, LastProblem = NULL, LastProblemUtc = NULL WHERE ArchiveId = @a;", t.Token,
                ("@k", newStorageKey), ("@n", newStorageKey[(newStorageKey.LastIndexOf('/') + 1)..]), ("@h", JsonSerializer.Serialize(history, ArchiveJson.Options)), ("@a", archiveId));
            await t.AuditAtCurrentVersionAsync(new AuditEntry("archive", "archive.relinked", "Pointed the archive at a new location after checking it is the same file.", "Archive", archiveId.ToString(),
                JobRepository.Json(new() { ["storageKey"] = archive.StorageKey }), JobRepository.Json(new() { ["storageKey"] = newStorageKey })));
            return (await ReadArchivesAsync(t.Connection, t.Transaction, "WHERE ArchiveId = @a", [("@a", archiveId)], t.Token)).Single();
        });
    }

    // ------------------------------------------------------------------ running

    /// <summary>Claims the next queued (or abandoned) operation and runs it. Returns false when there was nothing to do.</summary>
    public async Task<bool> RunNextAsync(string owner, AuditContext context, CancellationToken ct = default)
    {
        await FailExhaustedAsync(context, ct);
        var id = await ClaimAsync(owner, null, ct);
        if (id is null) return false;
        await RunClaimedAsync(id.Value, owner, context, ct);
        return true;
    }

    /// <summary>Runs one specific operation, if it is waiting. Used by tests and by an administrator's "run now".</summary>
    public async Task<bool> RunOperationAsync(long operationId, string owner, AuditContext context, CancellationToken ct = default)
    {
        var id = await ClaimAsync(owner, operationId, ct);
        if (id is null) return false;
        await RunClaimedAsync(id.Value, owner, context, ct);
        return true;
    }

    private async Task RunClaimedAsync(long operationId, string owner, AuditContext context, CancellationToken ct)
    {
        var op = (await GetOperationAsync(operationId, ct))!;
        using var lease = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renewal = RenewLeaseAsync(operationId, owner, lease.Token);
        try
        {
            switch (op.Kind)
            {
                case ArchiveOperationKind.Restore: await RunRestoreAsync(op, owner, context, ct); break;
                default: await RunArchiveAsync(op, owner, context, ct); break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await ReleaseAsync(operationId, owner, "Stopped; it will resume.", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await HandleFailureAsync(op, owner, ex, context);
        }
        finally
        {
            await lease.CancelAsync();
            try { await renewal; } catch (Exception) { /* a failed renewal is only a reason for the lease to lapse; it must never mask the operation's own result */ }
        }
    }

    // ---- archive: write, verify, then (for a purge) remove

    private async Task RunArchiveAsync(ArchiveOperationRecord op, string owner, AuditContext context, CancellationToken ct)
    {
        var archive = (await GetArchiveAsync(op.ArchiveId, ct))!;

        if (archive.Status == ArchiveStatus.Writing)
        {
            await StepAsync(op, owner, "Write", "Writing the archive.", ct);
            archive = await WriteArchiveAsync(archive, op, context, ct);
        }

        if (archive.Status == ArchiveStatus.Writing || (archive.VerifiedUtc is null && archive.Status != ArchiveStatus.PurgePending))
        {
            await StepAsync(op, owner, "Verify", "Checking the archive against the database.", ct);
            archive = await VerifyArchiveAsync(archive, op, ct);
        }

        if (archive.Status == ArchiveStatus.Ready && archive.Kind == ArchiveKind.ZipOnly)
        {
            await FinishZipOnlyAsync(op, archive, owner, ct);
            return;
        }

        if (archive.Status == ArchiveStatus.PurgePending)
        {
            await StepAsync(op, owner, "Purge", "Removing the job's detail from the database.", ct);
            await PurgeAsync(archive, op, owner, ct);
            await FinishPurgeAsync(op, archive, owner, ct);
        }
    }

    private async Task<ArchiveRecord> WriteArchiveAsync(ArchiveRecord archive, ArchiveOperationRecord op, AuditContext context, CancellationToken ct)
    {
        var key = $"jobs/{archive.JobId}/ductwork-{archive.JobId}-v{archive.CompletionVersion}-{DateTime.UtcNow:yyyyMMdd}-{archive.PublicId.ToString("N")[..8]}.zip";
        await _archives.DeleteAsync(key, ct);                         // a crashed earlier attempt at this same archive left a partial that was never relied on

        ArchiveManifest manifest = null!;
        await _archives.WriteAsync(key, async stream =>
        {
            manifest = await new ArchiveWriter(_open, _drawings).WriteAsync(archive.JobId, op.RequestedBy, stream, ct);
        }, ct);

        string sha;
        long size;
        await using (var back = await _archives.OpenReadAsync(key, ct) ?? throw new ArchiveMissingException(key))
        {
            using var hash = SHA256.Create();
            sha = Convert.ToHexString(await hash.ComputeHashAsync(back, ct));
            size = back.Length;
        }

        var history = new List<ArchiveLocation> { new(key, DateTime.UtcNow, op.RequestedBy, "Written") };
        await using var c = await _open(ct);
        await JobRepository.ExecAsync(c, null,
            """
            UPDATE ductwork.JobArchive
            SET StorageKey = @k, FileName = @n, SizeBytes = @sz, Sha256 = @sha, DataHash = @dh, TableCountsJson = @tc, SourceFileCount = @sc, SourceBytes = @sb,
                AuditHeadEventId = @he, AuditHeadHash = @hh, LocationHistoryJson = @lh
            WHERE ArchiveId = @a;
            """, ct,
            ("@k", key), ("@n", key[(key.LastIndexOf('/') + 1)..]), ("@sz", size), ("@sha", Convert.FromHexString(sha)), ("@dh", Convert.FromHexString(manifest.DataHash)),
            ("@tc", JsonSerializer.Serialize(manifest.Tables)), ("@sc", manifest.Sources.Count), ("@sb", manifest.Sources.Sum(s => s.Bytes)),
            ("@he", manifest.AuditHeadEventId), ("@hh", manifest.AuditHeadHash), ("@lh", JsonSerializer.Serialize(history, ArchiveJson.Options)), ("@a", archive.ArchiveId));
        return (await GetArchiveAsync(archive.ArchiveId, ct))!;
    }

    private async Task<ArchiveRecord> VerifyArchiveAsync(ArchiveRecord archive, ArchiveOperationRecord op, CancellationToken ct)
    {
        // Gate 2 and 4: read the zip back from where it now is, check every hash, and dry-run restoring it.
        var (manifest, result) = await ArchiveReader.InspectAsync(_archives, archive.StorageKey!, Convert.FromHexString(archive.Sha256!), archive.SizeBytes, ct);
        if (!result.Ok || manifest is null) throw new ArchiveCorruptException(result.Problems);

        // Gate 3: what is in SQL right now, with the job frozen, is exactly what the zip holds.
        await using (var c = await _open(ct))
        {
            var (liveHash, liveCounts) = await ArchiveWriter.FingerprintAsync(c, null, archive.JobId, ct);
            var problems = new List<string>();
            foreach (var table in ArchiveSchema.Detail)
                if (!manifest.Tables.TryGetValue(table.Name, out var inZip) || inZip != liveCounts[table.Name])
                    problems.Add($"{table.Name}: the archive has {(manifest.Tables.TryGetValue(table.Name, out var z) ? z : 0):N0} rows and the database has {liveCounts[table.Name]:N0}.");
            if (problems.Count == 0 && !string.Equals(liveHash, manifest.DataHash, StringComparison.OrdinalIgnoreCase))
                problems.Add("The rows in the database are not the rows that were archived (the job changed while it was being archived).");
            if (problems.Count > 0) throw new ArchiveCorruptException(problems);
        }

        var next = archive.Kind == ArchiveKind.Purge ? "PurgePending" : "Ready";
        await using var c2 = await _open(ct);
        await JobRepository.ExecAsync(c2, null, $"UPDATE ductwork.JobArchive SET Status = '{next}', VerifiedUtc = SYSUTCDATETIME() WHERE ArchiveId = @a;", ct, ("@a", archive.ArchiveId));
        return (await GetArchiveAsync(archive.ArchiveId, ct))!;
    }

    private Task FinishZipOnlyAsync(ArchiveOperationRecord op, ArchiveRecord archive, string owner, CancellationToken ct) =>
        JobTxn.RunAsync(_open, op.JobId, AuditContext.ForSystem("archive runner"), ct, async t =>
        {
            if (t.Job.State == JobState.Archiving)
            {
                var version = await t.BumpAsync("State = @s, StateBeforeArchive = NULL", ("@s", archive.StateAtArchive));
                await t.AuditAsync(version, new AuditEntry("archive", "archive.completed", $"The job was archived (a verified copy of {archive.SizeBytes:N0} bytes; the data stays in the database).",
                    "Archive", archive.ArchiveId.ToString(), JobRepository.Json(new() { ["state"] = "Archiving" }), JobRepository.Json(new() { ["state"] = archive.StateAtArchive })));
            }

            await SucceedAsync(t, op.OperationId, owner, "Archived.");
            return 0;
        });

    private async Task PurgeAsync(ArchiveRecord archive, ArchiveOperationRecord op, string owner, CancellationToken ct)
    {
        // Source files go first, while the rows that name them still exist; the zip holds every one, verified.
        await using (var c = await _open(ct))
        {
            var keys = new List<string>();
            await using (var command = c.CreateCommand())
            {
                command.CommandText = "SELECT StorageKey FROM ductwork.SourceFile WHERE JobId = @j AND StorageKey IS NOT NULL;";
                command.Parameters.AddWithValue("@j", archive.JobId);
                await using var r = await command.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) keys.Add(r.GetString(0));
            }

            foreach (var key in keys) await _drawings.DeleteAsync(key, ct);

            // An upload nobody finished leaves staging bytes behind; its session row is about to go, so nothing would clean them later.
            var staging = new List<string>();
            await using (var command = c.CreateCommand())
            {
                command.CommandText = "SELECT StagingKey FROM ductwork.UploadSession WHERE JobId = @j;";
                command.Parameters.AddWithValue("@j", archive.JobId);
                await using var r = await command.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) staging.Add(r.GetString(0));
            }

            foreach (var key in staging) await _drawings.DeleteStagingAsync(key, ct);
        }

        long total = archive.TableCounts.Where(t => ArchiveSchema.Detail.Any(d => d.Name == t.Key)).Sum(t => t.Value);
        long done = 0;
        var batches = 0;
        await using var connection = await _open(ct);
        try
        {
            await JobRepository.ExecAsync(connection, null, "EXEC sp_set_session_context @key = N'ductwork.purge_job_id', @value = @j;", ct, ("@j", archive.JobId));

            foreach (var table in ArchiveSchema.Detail.Reverse())
            {
                // A row that points at another row of its own table would block deleting in batches, so those links go first.
                if (table.SelfRef is not null)
                    await JobRepository.ExecAsync(connection, null, $"UPDATE {table.Qualified} SET {table.SelfRef} = NULL WHERE JobId = @j AND {table.SelfRef} IS NOT NULL;", ct, ("@j", archive.JobId));

                int removed;
                do
                {
                    removed = await JobRepository.ExecAsync(connection, null, $"DELETE TOP ({_settings.PurgeBatchRows}) FROM {table.Qualified} WHERE JobId = @j;", ct, ("@j", archive.JobId));
                    done += removed;
                    if (removed > 0)
                    {
                        await ProgressAsync(op.OperationId, owner, $"Removing {table.Name}.", done, total, ct);
                        _settings.AfterPurgeBatch?.Invoke(++batches);
                    }
                } while (removed > 0);
            }
        }
        finally
        {
            try { await JobRepository.ExecAsync(connection, null, "EXEC sp_set_session_context @key = N'ductwork.purge_job_id', @value = NULL;", CancellationToken.None); }
            catch (SqlException) { /* the connection is closing; a pooled connection is reset on reuse */ }
        }
    }

    private Task FinishPurgeAsync(ArchiveOperationRecord op, ArchiveRecord archive, string owner, CancellationToken ct) =>
        JobTxn.RunAsync(_open, op.JobId, AuditContext.ForSystem("archive runner"), ct, async t =>
        {
            if (t.Job.State == JobState.Archiving)
            {
                var detail = archive.TableCounts.Where(c => ArchiveSchema.Detail.Any(d => d.Name == c.Key)).ToDictionary(c => c.Key, c => c.Value);
                var summary = JsonSerializer.Serialize(new { purgedUtc = DateTime.UtcNow, archiveId = archive.ArchiveId, rows = detail });
                await JobRepository.ExecAsync(t.Connection, t.Transaction, "UPDATE ductwork.JobArchive SET Status = 'Purged', PurgedUtc = SYSUTCDATETIME() WHERE ArchiveId = @a;", t.Token, ("@a", archive.ArchiveId));
                var version = await t.BumpAsync("State = 'Archived', SummaryJson = @s", ("@s", summary));
                await t.AuditAsync(version, new AuditEntry("archive", "archive.purged",
                    $"The job was archived and its detail removed from the database ({detail.Values.Sum():N0} rows). The archive of {archive.SizeBytes:N0} bytes is now the only full copy.",
                    "Archive", archive.ArchiveId.ToString(), JobRepository.Json(new() { ["state"] = "Archiving" }), JobRepository.Json(new() { ["state"] = "Archived", ["archiveId"] = archive.ArchiveId })));
            }

            await SucceedAsync(t, op.OperationId, owner, "Archived and purged.");
            return 0;
        });

    // ---- restore

    private async Task RunRestoreAsync(ArchiveOperationRecord op, string owner, AuditContext context, CancellationToken ct)
    {
        var archive = (await GetArchiveAsync(op.ArchiveId, ct))!;
        await StepAsync(op, owner, "Locate", "Finding and checking the archive.", ct);

        if (archive.StorageKey is null || archive.Sha256 is null) throw new ArchiveMissingException(archive.StorageKey ?? "");
        var (manifest, result) = await ArchiveReader.InspectAsync(_archives, archive.StorageKey, Convert.FromHexString(archive.Sha256), archive.SizeBytes, ct);
        if (!result.Ok || manifest is null) throw new ArchiveCorruptException(result.Problems);

        await StepAsync(op, owner, "Import", "Putting the job back.", ct);
        long total = manifest.Tables.Where(t => ArchiveSchema.Detail.Any(d => d.Name == t.Key)).Sum(t => t.Value);

        await JobTxn.RunAsync(_open, op.JobId, AuditContext.ForSystem("archive runner"), ct, async t =>
        {
            if (t.Job.State != JobState.Restoring)
            {
                await SucceedAsync(t, op.OperationId, owner, "Restored.");        // a crash after the commit: it is already done
                return 0;
            }

            await using var stream = await _archives.OpenReadAsync(archive.StorageKey, ct) ?? throw new ArchiveMissingException(archive.StorageKey);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

            var files = await ArchiveImporter.RestoreSourcesAsync(manifest, zip, _drawings, archive.PublicId, ct);
            try
            {
                var inserted = await ArchiveImporter.ImportAsync(t.Connection, t.Transaction, manifest, zip, op.JobId, done => ProgressAsync(op.OperationId, owner, "Putting the job back.", done, total, ct), ct);

                // The restored rows must be exactly the archived rows, or the whole restore is undone.
                var (hash, _) = await ArchiveWriter.FingerprintAsync(t.Connection, t.Transaction, op.JobId, ct);
                if (!string.Equals(hash, manifest.DataHash, StringComparison.OrdinalIgnoreCase))
                    throw new ArchiveCorruptException(["The restored rows do not match the archive, so nothing was restored."]);

                await JobRepository.ExecAsync(t.Connection, t.Transaction, "UPDATE ductwork.JobArchive SET Status = 'Ready', RestoredUtc = SYSUTCDATETIME(), LastProblem = NULL, LastProblemUtc = NULL WHERE ArchiveId = @a;", t.Token, ("@a", archive.ArchiveId));
                var version = await t.BumpAsync("State = @s, StateBeforeArchive = NULL", ("@s", archive.StateAtArchive));
                await t.AuditAsync(version, new AuditEntry("archive", "archive.restored", $"The job was restored from its archive ({inserted.Values.Sum():N0} rows and {manifest.Sources.Count} source file(s)).",
                    "Archive", archive.ArchiveId.ToString(), JobRepository.Json(new() { ["state"] = "Restoring" }), JobRepository.Json(new() { ["state"] = archive.StateAtArchive })));
                await SucceedAsync(t, op.OperationId, owner, "Restored.");
            }
            catch
            {
                foreach (var key in files) await _drawings.DeleteAsync(key, CancellationToken.None);
                throw;
            }

            return 0;
        });
    }

    // ---- failure

    private static bool IsPermanent(Exception ex) => ex is ArchiveMissingException or ArchiveCorruptException or JobRuleViolationException or ArgumentException;

    private async Task HandleFailureAsync(ArchiveOperationRecord op, string owner, Exception ex, AuditContext context)
    {
        var fresh = await GetOperationAsync(op.OperationId) ?? op;
        var message = ex is ArchiveMissingException ? ex.Message : ex.Message.Length > 1500 ? ex.Message[..1500] : ex.Message;
        if (!IsPermanent(ex) && fresh.Attempt < fresh.MaxAttempts)
        {
            await ReleaseAsync(op.OperationId, owner, "Will try again: " + message, CancellationToken.None);
            return;
        }

        await FailOperationAsync(fresh, message, ex as ArchiveMissingException);
    }

    /// <summary>Gives up on an operation and puts the job in the safest state: back as it was if nothing had been removed, Archiving (for a retry) if a purge had begun, Archived after a failed restore.</summary>
    private async Task FailOperationAsync(ArchiveOperationRecord op, string message, ArchiveMissingException? missing = null)
    {
        var archive = (await GetArchiveAsync(op.ArchiveId))!;
        await JobTxn.RunAsync(_open, op.JobId, AuditContext.ForSystem("archive runner"), CancellationToken.None, async t =>
        {
            await JobRepository.ExecAsync(t.Connection, t.Transaction,
                "UPDATE ductwork.ArchiveOperation SET Status = 'Failed', Error = @e, Message = NULL, LeaseOwner = NULL, LeaseExpiresUtc = NULL, FinishedUtc = SYSUTCDATETIME() WHERE OperationId = @o;", t.Token,
                ("@e", message), ("@o", op.OperationId));

            if (op.Kind == ArchiveOperationKind.Restore)
            {
                var problem = missing is null ? message : $"{message} Last known location: {missing.StorageKey}. Recorded hash: {archive.Sha256}.";
                await JobRepository.ExecAsync(t.Connection, t.Transaction, "UPDATE ductwork.JobArchive SET LastProblem = @p, LastProblemUtc = SYSUTCDATETIME() WHERE ArchiveId = @a;", t.Token, ("@p", Cut(problem, 1000)), ("@a", archive.ArchiveId));
                if (t.Job.State == JobState.Restoring)
                {
                    var version = await t.BumpAsync("State = 'Archived'");
                    await t.AuditAsync(version, new AuditEntry("archive", "restore.failed", "Restoring the job failed and nothing was changed: " + message, "Archive", archive.ArchiveId.ToString(),
                        JobRepository.Json(new() { ["state"] = "Restoring" }), JobRepository.Json(new() { ["state"] = "Archived" })));
                }
            }
            else if (archive.Status == ArchiveStatus.PurgePending)
            {
                await t.AuditAtCurrentVersionAsync(new AuditEntry("archive", "archive.purge-failed",
                    "Removing the job's detail stopped part-way: " + message + " The archive is verified, so the job can still be restored. An administrator can retry.", "Archive", archive.ArchiveId.ToString()));
            }
            else
            {
                // Nothing was removed, so the job simply goes back to what it was and the unproven zip is discarded.
                // A zip that was already proven stays relied on even if a later step failed; only an unproven one is written off.
                var verified = archive.Status == ArchiveStatus.Ready;
                await JobRepository.ExecAsync(t.Connection, t.Transaction,
                    $"UPDATE ductwork.JobArchive SET {(verified ? "" : "Status = 'Failed', ")}LastProblem = @p, LastProblemUtc = SYSUTCDATETIME() WHERE ArchiveId = @a;", t.Token, ("@p", Cut(message, 1000)), ("@a", archive.ArchiveId));
                if (t.Job.State == JobState.Archiving)
                {
                    var version = await t.BumpAsync("State = @s, StateBeforeArchive = NULL", ("@s", archive.StateAtArchive));
                    await t.AuditAsync(version, new AuditEntry("archive", "archive.failed", "Archiving the job failed and nothing was changed: " + message, "Archive", archive.ArchiveId.ToString(),
                        JobRepository.Json(new() { ["state"] = "Archiving" }), JobRepository.Json(new() { ["state"] = archive.StateAtArchive })));
                }
            }

            return 0;
        });

        if (op.Kind != ArchiveOperationKind.Restore && archive.Status == ArchiveStatus.Writing && archive.StorageKey is not null)
            try { await _archives.DeleteAsync(archive.StorageKey, CancellationToken.None); } catch (IOException) { }
    }

    private async Task FailExhaustedAsync(AuditContext context, CancellationToken ct)
    {
        List<long> ids = [];
        await using (var c = await _open(ct))
        {
            await using var command = c.CreateCommand();
            command.CommandText = "SELECT OperationId FROM ductwork.ArchiveOperation WHERE Status = 'Running' AND LeaseExpiresUtc < SYSUTCDATETIME() AND Attempt >= MaxAttempts;";
            await using var r = await command.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ids.Add(r.GetInt64(0));
        }

        foreach (var id in ids) await FailOperationAsync((await GetOperationAsync(id, ct))!, "The operation stopped repeatedly and was given up on.");
    }

    // ---- operation bookkeeping

    private async Task<long?> ClaimAsync(string owner, long? only, CancellationToken ct)
    {
        await using var c = await _open(ct);
        await using var command = c.CreateCommand();
        command.CommandText =
            $"""
            ;WITH next AS (
                SELECT TOP (1) * FROM ductwork.ArchiveOperation WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE (Status = 'Queued' OR (Status = 'Running' AND LeaseExpiresUtc < SYSUTCDATETIME())) AND Attempt < MaxAttempts
                  {(only is null ? "" : "AND OperationId = @only")}
                ORDER BY OperationId)
            UPDATE next SET Status = 'Running', Attempt = Attempt + 1, LeaseOwner = @o, LeaseExpiresUtc = DATEADD(SECOND, @lease, SYSUTCDATETIME()), StartedUtc = ISNULL(StartedUtc, SYSUTCDATETIME())
            OUTPUT inserted.OperationId;
            """;
        command.Parameters.AddWithValue("@o", owner);
        command.Parameters.AddWithValue("@lease", (int)_settings.LeaseDuration.TotalSeconds);
        if (only is not null) command.Parameters.AddWithValue("@only", only.Value);
        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : (long)result;
    }

    private async Task RenewLeaseAsync(long operationId, string owner, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _settings.LeaseDuration.TotalSeconds / 3)));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await using var c = await _open(ct);
                await JobRepository.ExecAsync(c, null,
                    "UPDATE ductwork.ArchiveOperation SET LeaseExpiresUtc = DATEADD(SECOND, @lease, SYSUTCDATETIME()) WHERE OperationId = @o AND LeaseOwner = @w AND Status = 'Running';", ct,
                    ("@lease", (int)_settings.LeaseDuration.TotalSeconds), ("@o", operationId), ("@w", owner));
            }
            catch (Exception ex) when (ex is SqlException) { /* the next tick tries again; the lease only matters if this goes on for minutes */ }
        }
    }

    private async Task StepAsync(ArchiveOperationRecord op, string owner, string step, string message, CancellationToken ct)
    {
        await using var c = await _open(ct);
        await JobRepository.ExecAsync(c, null, "UPDATE ductwork.ArchiveOperation SET Step = @s, Message = @m WHERE OperationId = @o AND LeaseOwner = @w;", ct,
            ("@s", step), ("@m", message), ("@o", op.OperationId), ("@w", owner));
    }

    private async Task ProgressAsync(long operationId, string owner, string message, long done, long? total, CancellationToken ct)
    {
        await using var c = await _open(ct);
        await JobRepository.ExecAsync(c, null,
            "UPDATE ductwork.ArchiveOperation SET Message = @m, ProgressDone = @d, ProgressTotal = @t WHERE OperationId = @o AND LeaseOwner = @w;", ct,
            ("@m", message), ("@d", done), ("@t", total), ("@o", operationId), ("@w", owner));
    }

    private static Task SucceedAsync(JobTxn t, long operationId, string owner, string message) =>
        JobRepository.ExecAsync(t.Connection, t.Transaction,
            "UPDATE ductwork.ArchiveOperation SET Status = 'Succeeded', Message = @m, Error = NULL, LeaseOwner = NULL, LeaseExpiresUtc = NULL, FinishedUtc = SYSUTCDATETIME() WHERE OperationId = @o;", t.Token,
            ("@m", message), ("@o", operationId));

    private async Task ReleaseAsync(long operationId, string owner, string message, CancellationToken ct)
    {
        await using var c = await _open(ct);
        await JobRepository.ExecAsync(c, null,
            "UPDATE ductwork.ArchiveOperation SET Status = 'Queued', Message = @m, LeaseOwner = NULL, LeaseExpiresUtc = NULL WHERE OperationId = @o AND LeaseOwner = @w;", ct,
            ("@m", Cut(message, 400)), ("@o", operationId), ("@w", owner));
    }

    private static async Task<long> InsertOperationAsync(JobTxn t, long archiveId, ArchiveOperationKind kind)
    {
        try
        {
            return await ScalarAsync<long>(t,
                "INSERT INTO ductwork.ArchiveOperation (JobId, ArchiveId, Kind, RequestedBy) OUTPUT inserted.OperationId VALUES (@j, @a, @k, @by);",
                ("@a", archiveId), ("@k", kind.ToString()), ("@by", JobRepository.Actor(t.Context)));
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            throw new JobRuleViolationException("Something is already being done to this job's archive. Wait for it to finish.");
        }
    }

    private async Task RecordProblemAsync(ArchiveRecord archive, string? problem, string action, string summary, AuditContext context, CancellationToken ct) =>
        await JobTxn.RunAsync(_open, archive.JobId, context, ct, async t =>
        {
            await JobRepository.ExecAsync(t.Connection, t.Transaction, "UPDATE ductwork.JobArchive SET LastProblem = @p, LastProblemUtc = CASE WHEN @p IS NULL THEN NULL ELSE SYSUTCDATETIME() END WHERE ArchiveId = @a;", t.Token,
                ("@p", problem is null ? null : Cut(problem, 1000)), ("@a", archive.ArchiveId));
            await t.AuditAtCurrentVersionAsync(new AuditEntry("archive", action, summary, "Archive", archive.ArchiveId.ToString()));
            return 0;
        });

    private async Task RequireArchiveRightAsync(long jobId, AuditContext context, JobCapability capability, CancellationToken ct)
    {
        context.EnsureValid();
        if (context.IsAdmin || context.ActorType != ActorType.User) return;
        await using var c = await _open(ct);
        var (role, manager) = await JobAccess.LoadAsync(c, null, jobId, context.ActorEmail!, ct);
        if (!JobPermissions.Can(role, manager, capability)) throw new JobAccessDeniedException($"You do not have permission to do that on this job ({capability}).");
    }

    // ---- row mapping

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];

    private static async Task<T> ScalarAsync<T>(JobTxn t, string sql, params (string Name, object? Value)[] args)
    {
        await using var command = t.Connection.CreateCommand();
        command.Transaction = t.Transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("@j", t.Job.JobId);
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = await command.ExecuteScalarAsync(t.Token);
        return result is null or DBNull ? default! : (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    private static async Task<IReadOnlyList<ArchiveRecord>> ReadArchivesAsync(SqlConnection c, SqlTransaction? tx, string where, (string Name, object Value)[] args, CancellationToken ct, int? limit = null)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            $"""
            SELECT {(limit is null ? "" : $"TOP ({limit})")} ArchiveId, PublicId, JobId, CompletionVersion, Kind, Status, StateAtArchive, FormatVersion, ContractVersion, StorageKey, FileName, SizeBytes, Sha256, DataHash,
                   TableCountsJson, SourceFileCount, SourceBytes, AuditHeadEventId, AuditHeadHash, LocationHistoryJson, LastProblem, LastProblemUtc, CreatedUtc, CreatedBy, VerifiedUtc, PurgedUtc, RestoredUtc, SupersededUtc
            FROM ductwork.JobArchive {where} ORDER BY ArchiveId DESC;
            """;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value);

        var list = new List<ArchiveRecord>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            string? Str(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            DateTime? Time(int i) => r.IsDBNull(i) ? null : r.GetDateTime(i);
            list.Add(new ArchiveRecord
            {
                ArchiveId = r.GetInt64(0), PublicId = r.GetGuid(1), JobId = r.GetInt64(2), CompletionVersion = r.GetInt32(3),
                Kind = Enum.Parse<ArchiveKind>(r.GetString(4)), Status = Enum.Parse<ArchiveStatus>(r.GetString(5)), StateAtArchive = r.GetString(6),
                FormatVersion = r.GetInt32(7), ContractVersion = r.GetString(8), StorageKey = Str(9), FileName = Str(10),
                SizeBytes = r.IsDBNull(11) ? null : r.GetInt64(11), Sha256 = r.IsDBNull(12) ? null : Convert.ToHexString((byte[])r.GetValue(12)).ToLowerInvariant(),
                DataHash = r.IsDBNull(13) ? null : Convert.ToHexString((byte[])r.GetValue(13)).ToLowerInvariant(),
                TableCounts = r.IsDBNull(14) ? new Dictionary<string, long>() : JsonSerializer.Deserialize<Dictionary<string, long>>(r.GetString(14))!,
                SourceFileCount = r.IsDBNull(15) ? null : r.GetInt32(15), SourceBytes = r.IsDBNull(16) ? null : r.GetInt64(16),
                AuditHeadEventId = r.IsDBNull(17) ? null : r.GetInt64(17), AuditHeadHash = Str(18),
                Locations = r.IsDBNull(19) ? [] : JsonSerializer.Deserialize<List<ArchiveLocation>>(r.GetString(19), ArchiveJson.Options)!,
                LastProblem = Str(20), LastProblemUtc = Time(21), CreatedUtc = r.GetDateTime(22), CreatedBy = r.GetString(23),
                VerifiedUtc = Time(24), PurgedUtc = Time(25), RestoredUtc = Time(26), SupersededUtc = Time(27)
            });
        }

        return list;
    }

    private static async Task<IReadOnlyList<ArchiveOperationRecord>> ReadOperationsAsync(SqlConnection c, SqlTransaction? tx, string where, (string Name, object Value)[] args, CancellationToken ct, int? limit = null)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT {(limit is null ? "" : $"TOP ({limit})")} {string.Join(", ", OperationColumns)} FROM ductwork.ArchiveOperation {where} ORDER BY OperationId DESC;";
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value);

        var list = new List<ArchiveOperationRecord>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            string? Str(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            DateTime? Time(int i) => r.IsDBNull(i) ? null : r.GetDateTime(i);
            list.Add(new ArchiveOperationRecord
            {
                OperationId = r.GetInt64(0), PublicId = r.GetGuid(1), JobId = r.GetInt64(2), ArchiveId = r.GetInt64(3), Kind = Enum.Parse<ArchiveOperationKind>(r.GetString(4)),
                Status = Enum.Parse<ArchiveOperationStatus>(r.GetString(5)), Step = r.GetString(6), Message = Str(7), ProgressDone = r.GetInt64(8),
                ProgressTotal = r.IsDBNull(9) ? null : r.GetInt64(9), Attempt = r.GetInt32(10), MaxAttempts = r.GetInt32(11), RequestedBy = r.GetString(12),
                RequestedUtc = r.GetDateTime(13), StartedUtc = Time(14), FinishedUtc = Time(15), Error = Str(16)
            });
        }

        return list;
    }
}
