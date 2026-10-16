using AIToolbox.Ductwork.Core.Jobs;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Drawings;

public enum UploadStatus { Open, Completed, Aborted, Expired }

public sealed record UploadSettings
{
    /// <summary>The size the browser is told to cut a file into.</summary>
    public long ChunkBytes { get; init; } = 8L * 1024 * 1024;
    public long MaxFileBytes { get; init; } = 1024L * 1024 * 1024;
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromHours(24);
    public int MaxOpenPerJob { get; init; } = 5;
}

public sealed record BeginUpload(string FileName, long TotalBytes, string? Sha256Hex);

public sealed record UploadSessionRecord
{
    public long UploadSessionId { get; init; }
    public Guid PublicId { get; init; }
    public long JobId { get; init; }
    public required string OriginalName { get; init; }
    public long TotalBytes { get; init; }
    public string? ExpectedSha256 { get; init; }
    public long ReceivedBytes { get; init; }
    public UploadStatus Status { get; init; }
    public required string StagingKey { get; init; }
    public required string StartedBy { get; init; }
    public required string StartedSessionId { get; init; }
    public DateTime StartedUtc { get; init; }
    public DateTime LastActivityUtc { get; init; }
    public DateTime ExpiresUtc { get; init; }
    public bool IsExpired { get; init; }
    public Guid? SourceFilePublicId { get; init; }
    public long ChunkBytes { get; init; }
}

public sealed record SourceFileRecord
{
    public long SourceFileId { get; init; }
    public Guid PublicId { get; init; }
    public long JobId { get; init; }
    public long RevisionId { get; init; }
    public int RevisionNo { get; init; }
    public required string OriginalName { get; init; }
    public required string Sha256 { get; init; }
    public long Bytes { get; init; }
    public required string ContentType { get; init; }
    public string? StorageKey { get; init; }
    public Guid? DuplicateOfPublicId { get; init; }
    public required string UploadedBy { get; init; }
    public DateTime UploadedUtc { get; init; }
}

/// <summary>Another request is writing to this upload at the same moment.</summary>
public sealed class UploadBusyException() : Exception("Another request is writing to this upload. Wait and ask where it is up to.");

/// <summary>
/// Resumable, hash-verified upload of drawing files (plan section 6.8). The bytes are streamed to the artifact
/// store as they arrive, never held in memory. Starting and finishing an upload are job edits (they need the
/// edit lock and are audited); the pieces in between only land in staging, so they are checked against the
/// upload's own session instead and never touch job content.
/// </summary>
public sealed class UploadService
{
    private const string Pdf = "application/pdf";
    private readonly Func<CancellationToken, Task<SqlConnection>> _open;
    private readonly IArtifactStore _store;
    private readonly UploadSettings _settings;

    public UploadService(Func<CancellationToken, Task<SqlConnection>> openConnection, IArtifactStore store, UploadSettings? settings = null)
    {
        _open = openConnection;
        _store = store;
        _settings = settings ?? new UploadSettings();
    }

    public UploadSettings Settings => _settings;

    // ------------------------------------------------------------------ start

    public Task<UploadSessionRecord> BeginAsync(long jobId, BeginUpload request, EditToken editToken, AuditContext context, CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context, cancellationToken, async t =>
        {
            t.RequireEditable();
            var name = CleanName(request.FileName);
            if (request.TotalBytes <= 0) throw new JobRuleViolationException("The file is empty.");
            if (request.TotalBytes > _settings.MaxFileBytes)
                throw new JobRuleViolationException($"The file is {request.TotalBytes / 1048576.0:N0} MB, over the {_settings.MaxFileBytes / 1048576.0:N0} MB limit.");
            var expected = ParseSha(request.Sha256Hex);

            var open = await ScalarAsync<int>(t.Connection, t.Transaction, t.Token,
                "SELECT COUNT(*) FROM ductwork.UploadSession WHERE JobId = @j AND Status = 'Open' AND ExpiresUtc > SYSUTCDATETIME();", ("@j", jobId));
            if (open >= _settings.MaxOpenPerJob)
                throw new JobRuleViolationException($"This job already has {open} uploads in progress. Let them finish or cancel one.");

            var staging = "uploads/" + Guid.NewGuid().ToString("N");
            var id = await ScalarAsync<long>(t.Connection, t.Transaction, t.Token,
                """
                INSERT INTO ductwork.UploadSession (JobId, OriginalName, TotalBytes, ExpectedSha256, StagingKey, StartedBy, StartedSessionId, ExpiresUtc)
                OUTPUT INSERTED.UploadSessionId
                VALUES (@j, @n, @t, @h, @k, @by, @s, DATEADD(SECOND, @ttl, SYSUTCDATETIME()));
                """,
                ("@j", jobId), ("@n", name), ("@t", request.TotalBytes), ("@h", (object?)expected ?? System.Data.SqlTypes.SqlBinary.Null), ("@k", staging),
                ("@by", JobRepository.Actor(context)), ("@s", context.SessionId), ("@ttl", (int)_settings.SessionLifetime.TotalSeconds));

            var session = (await ReadSessionAsync(t.Connection, t.Transaction, "WHERE u.UploadSessionId = @id", t.Token, ("@id", id))).Single();
            await t.AuditAtCurrentVersionAsync(new AuditEntry("drawings", "upload.started", $"Started uploading \"{name}\" ({request.TotalBytes:N0} bytes).",
                "UploadSession", session.PublicId.ToString("D"), null,
                JobRepository.Json(new() { ["fileName"] = name, ["bytes"] = request.TotalBytes })));
            return session;
        }, editToken, requires: JobCapability.Edit);

    // ------------------------------------------------------------------ pieces

    /// <summary>
    /// Adds the next piece. It must start exactly where the upload is up to; a retry after a dropped
    /// connection asks where that is first. An integrity hash, if given, is checked and a bad piece is discarded.
    /// </summary>
    [NoAudit("Bytes land in staging only and change no job content. Starting and completing the upload are audited.")]
    public async Task<UploadSessionRecord> AppendAsync(Guid uploadId, long offset, Stream data, string? chunkSha256Hex, AuditContext context,
        CancellationToken cancellationToken = default)
    {
        context.EnsureValid();
        var expectedChunk = ParseSha(chunkSha256Hex);
        var session = await RequireOwnSessionAsync(uploadId, context, cancellationToken, requireOpen: true);

        if (offset != session.ReceivedBytes) throw new UploadOffsetException(session.ReceivedBytes, offset);
        if (session.ReceivedBytes >= session.TotalBytes) throw new JobRuleViolationException("Every byte has already arrived. Finish the upload.");
        var limit = Math.Min(_settings.ChunkBytes, session.TotalBytes - session.ReceivedBytes);

        (long Written, byte[] Sha256) result;
        try
        {
            result = await _store.AppendStagingAsync(session.StagingKey, offset, data, limit, cancellationToken);
        }
        catch (IOException)
        {
            throw new UploadBusyException();
        }

        if (result.Written == 0) throw new ArgumentException("The piece was empty.");
        if (expectedChunk is not null && !expectedChunk.AsSpan().SequenceEqual(result.Sha256))
        {
            await _store.TruncateStagingAsync(session.StagingKey, offset, cancellationToken);
            throw new ChunkIntegrityException();
        }

        await using var connection = await _open(cancellationToken);
        var moved = await JobRepository.ExecAsync(connection, null,
            """
            UPDATE ductwork.UploadSession
            SET ReceivedBytes = @new, LastActivityUtc = SYSUTCDATETIME()
            WHERE UploadSessionId = @id AND Status = 'Open' AND ReceivedBytes = @old AND @new <= TotalBytes;
            """, cancellationToken, ("@new", offset + result.Written), ("@old", offset), ("@id", session.UploadSessionId));
        if (moved == 0)
            throw new UploadOffsetException((await RequireOwnSessionAsync(uploadId, context, cancellationToken, requireOpen: false)).ReceivedBytes, offset);

        return session with { ReceivedBytes = offset + result.Written };
    }

    // ------------------------------------------------------------------ finish

    /// <summary>
    /// Checks the whole file (every byte arrived, the hash matches what the sender gave, it really is a PDF),
    /// then stores it. A file the job already holds is recorded as a duplicate and not stored again.
    /// </summary>
    public async Task<SourceFileRecord> CompleteAsync(Guid uploadId, EditToken editToken, AuditContext context, CancellationToken cancellationToken = default)
    {
        var session = await RequireOwnSessionAsync(uploadId, context, cancellationToken, requireOpen: true);
        if (session.JobId != editToken.JobId) throw new StaleEditTokenException("That edit token is for a different job.");

        string? promoted = null;
        try
        {
            return await JobTxn.RunAsync(_open, session.JobId, context, cancellationToken, async t =>
            {
                t.RequireEditable();
                var current = (await ReadSessionAsync(t.Connection, t.Transaction, "WHERE u.UploadSessionId = @id", t.Token, ("@id", session.UploadSessionId), forUpdate: true)).Single();
                if (current.Status != UploadStatus.Open) throw new JobRuleViolationException($"This upload is {current.Status.ToString().ToLowerInvariant()}.");
                if (current.ReceivedBytes != current.TotalBytes)
                    throw new JobRuleViolationException($"Only {current.ReceivedBytes:N0} of {current.TotalBytes:N0} bytes have arrived.");

                var (length, hash, head) = await _store.InspectStagingAsync(current.StagingKey, 1024, t.Token);
                if (length != current.TotalBytes) throw new JobRuleViolationException("The stored file is not the size that was announced. Start the upload again.");
                if (current.ExpectedSha256 is { } expected && !string.Equals(expected, Convert.ToHexString(hash), StringComparison.OrdinalIgnoreCase))
                    throw new JobRuleViolationException("The file's checksum does not match the one sent. It was damaged on the way; upload it again.");
                if (!LooksLikePdf(head)) throw new JobRuleViolationException("That is not a PDF file.");

                var revision = await EnsureRevisionAsync(t, JobRepository.Actor(context));
                var sourcePublicId = Guid.NewGuid();
                var duplicateOf = await ScalarOrNullAsync<long>(t.Connection, t.Transaction, t.Token,
                    "SELECT TOP 1 SourceFileId FROM ductwork.SourceFile WHERE JobId = @j AND Sha256 = @h AND StorageKey IS NOT NULL ORDER BY SourceFileId;",
                    ("@j", t.Job.JobId), ("@h", hash));

                string? key = null;
                if (duplicateOf is null)
                {
                    key = $"jobs/{t.Job.PublicId:N}/{sourcePublicId:N}.pdf";
                    await _store.PromoteAsync(current.StagingKey, key, t.Token);
                    promoted = key;
                }
                else
                {
                    await _store.DeleteStagingAsync(current.StagingKey, t.Token);
                }

                var id = await ScalarAsync<long>(t.Connection, t.Transaction, t.Token,
                    """
                    INSERT INTO ductwork.SourceFile (PublicId, JobId, RevisionId, OriginalName, Sha256, Bytes, ContentType, StorageKey, DuplicateOfSourceFileId, UploadedBy)
                    OUTPUT INSERTED.SourceFileId
                    VALUES (@p, @j, @r, @n, @h, @b, @c, @k, @d, @by);
                    """,
                    ("@p", sourcePublicId), ("@j", t.Job.JobId), ("@r", revision.RevisionId), ("@n", current.OriginalName), ("@h", hash), ("@b", length),
                    ("@c", Pdf), ("@k", key), ("@d", duplicateOf), ("@by", JobRepository.Actor(context)));
                await JobRepository.ExecAsync(t.Connection, t.Transaction,
                    "UPDATE ductwork.UploadSession SET Status = 'Completed', SourceFileId = @f, LastActivityUtc = SYSUTCDATETIME() WHERE UploadSessionId = @id;",
                    t.Token, ("@f", id), ("@id", current.UploadSessionId));

                var file = (await ReadFileAsync(t.Connection, t.Transaction, "WHERE f.SourceFileId = @id", t.Token, ("@id", id))).Single();
                await t.AuditAtCurrentVersionAsync(new AuditEntry("drawings", duplicateOf is null ? "source-file.added" : "source-file.duplicate",
                    duplicateOf is null
                        ? $"Added the drawing file \"{file.OriginalName}\"."
                        : $"\"{file.OriginalName}\" is identical to a file already on this job and was not stored again.",
                    "SourceFile", file.PublicId.ToString("D"), null,
                    JobRepository.Json(new() { ["fileName"] = file.OriginalName, ["bytes"] = file.Bytes, ["sha256"] = file.Sha256, ["revision"] = revision.RevisionNo })));
                return file;
            }, editToken, requires: JobCapability.Edit);
        }
        catch
        {
            // The database change rolled back, so a file already moved into place would be an orphan.
            if (promoted is not null)
            {
                try { await _store.DeleteAsync(promoted, CancellationToken.None); }
                catch (IOException) { }
            }

            throw;
        }
    }

    public async Task<bool> AbortAsync(Guid uploadId, AuditContext context, CancellationToken cancellationToken = default)
    {
        var session = await GetAsync(uploadId, cancellationToken) ?? throw new KeyNotFoundException("That upload does not exist.");
        return await JobTxn.RunAsync(_open, session.JobId, context, cancellationToken, async t =>
        {
            var current = (await ReadSessionAsync(t.Connection, t.Transaction, "WHERE u.UploadSessionId = @id", t.Token, ("@id", session.UploadSessionId), forUpdate: true)).Single();
            if (current.Status != UploadStatus.Open) return false;
            if (context.ActorType == ActorType.User && current.StartedBy != context.ActorEmail)
            {
                var (role, _) = await JobAccess.LoadAsync(t.Connection, t.Transaction, t.Job.JobId, context.ActorEmail!, t.Token);
                if (role != JobRole.Owner) throw new JobAccessDeniedException("Only the person who started an upload, or a job owner, can cancel it.");
            }

            await JobRepository.ExecAsync(t.Connection, t.Transaction, "UPDATE ductwork.UploadSession SET Status = 'Aborted', LastActivityUtc = SYSUTCDATETIME() WHERE UploadSessionId = @id;",
                t.Token, ("@id", current.UploadSessionId));
            await _store.DeleteStagingAsync(current.StagingKey, t.Token);
            await t.AuditAtCurrentVersionAsync(new AuditEntry("drawings", "upload.cancelled", $"Cancelled the upload of \"{current.OriginalName}\".", "UploadSession", current.PublicId.ToString("D")));
            return true;
        }, requires: JobCapability.Edit);
    }

    /// <summary>Closes uploads nobody finished and frees their staging space. Meant for a periodic job.</summary>
    public async Task<int> ExpireStaleAsync(AuditContext context, CancellationToken cancellationToken = default)
    {
        List<UploadSessionRecord> stale;
        await using (var connection = await _open(cancellationToken))
            stale = await ReadSessionAsync(connection, null, "WHERE u.Status = 'Open' AND u.ExpiresUtc <= SYSUTCDATETIME()", cancellationToken);

        var expired = 0;
        foreach (var session in stale)
        {
            var done = await JobTxn.RunAsync(_open, session.JobId, context, cancellationToken, async t =>
            {
                var moved = await JobRepository.ExecAsync(t.Connection, t.Transaction,
                    "UPDATE ductwork.UploadSession SET Status = 'Expired' WHERE UploadSessionId = @id AND Status = 'Open' AND ExpiresUtc <= SYSUTCDATETIME();",
                    t.Token, ("@id", session.UploadSessionId));
                if (moved == 0) return false;
                await _store.DeleteStagingAsync(session.StagingKey, t.Token);
                await t.AuditAtCurrentVersionAsync(new AuditEntry("drawings", "upload.expired", $"The unfinished upload of \"{session.OriginalName}\" expired.", "UploadSession", session.PublicId.ToString("D")));
                return true;
            });
            if (done) expired++;
        }

        return expired;
    }

    // ------------------------------------------------------------------ reads

    public async Task<UploadSessionRecord?> GetAsync(Guid uploadId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return (await ReadSessionAsync(connection, null, "WHERE u.PublicId = @p", cancellationToken, ("@p", uploadId))).SingleOrDefault();
    }

    public async Task<IReadOnlyList<SourceFileRecord>> ListFilesAsync(long jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return await ReadFileAsync(connection, null, "WHERE f.JobId = @j ORDER BY f.SourceFileId", cancellationToken, ("@j", jobId));
    }

    public async Task<SourceFileRecord?> GetFileAsync(long jobId, Guid publicId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return (await ReadFileAsync(connection, null, "WHERE f.JobId = @j AND f.PublicId = @p", cancellationToken, ("@j", jobId), ("@p", publicId))).SingleOrDefault();
    }

    /// <summary>Opens a file's bytes. A duplicate reads the copy it points at.</summary>
    public async Task<Stream?> GetFileContentAsync(SourceFileRecord file, CancellationToken cancellationToken = default)
    {
        var key = file.StorageKey;
        if (key is null && file.DuplicateOfPublicId is { } original)
            key = (await GetFileAsync(file.JobId, original, cancellationToken))?.StorageKey;
        return key is null ? null : await _store.OpenReadAsync(key, cancellationToken);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<UploadSessionRecord> RequireOwnSessionAsync(Guid uploadId, AuditContext context, CancellationToken ct, bool requireOpen)
    {
        var session = await GetAsync(uploadId, ct) ?? throw new KeyNotFoundException("That upload does not exist.");
        // Someone else's upload is reported as missing, so an upload id reveals nothing.
        if (context.ActorType != ActorType.User || session.StartedSessionId != context.SessionId || session.StartedBy != context.ActorEmail)
            throw new KeyNotFoundException("That upload does not exist.");

        if (!requireOpen) return session;
        if (session.Status != UploadStatus.Open) throw new JobRuleViolationException($"This upload is {session.Status.ToString().ToLowerInvariant()}.");
        if (session.IsExpired) throw new JobRuleViolationException("This upload expired. Start it again.");

        await using var connection = await _open(ct);
        var job = await JobRepository.ReadJobAsync(connection, null, session.JobId, forUpdate: false, ct) ?? throw new JobNotFoundException(session.JobId);
        if (!JobLifecycle.IsEditable(job.State)) throw new JobRuleViolationException($"This job is {job.State} and cannot take uploads.");
        var (role, manager) = await JobAccess.LoadAsync(connection, null, session.JobId, context.ActorEmail!, ct);
        if (!JobPermissions.Can(role, manager, JobCapability.Edit)) throw new JobAccessDeniedException("You no longer have permission to edit this job.");
        return session;
    }

    private static async Task<(long RevisionId, int RevisionNo)> EnsureRevisionAsync(JobTxn t, string by)
    {
        await using (var command = t.Connection.CreateCommand())
        {
            command.Transaction = t.Transaction;
            command.CommandText = "SELECT TOP 1 RevisionId, RevisionNo FROM ductwork.DrawingSetRevision WHERE JobId = @j ORDER BY RevisionNo DESC;";
            command.Parameters.AddWithValue("@j", t.Job.JobId);
            await using var reader = await command.ExecuteReaderAsync(t.Token);
            if (await reader.ReadAsync(t.Token)) return (reader.GetInt64(0), reader.GetInt32(1));
        }

        var id = await ScalarAsync<long>(t.Connection, t.Transaction, t.Token,
            "INSERT INTO ductwork.DrawingSetRevision (JobId, RevisionNo, CreatedBy) OUTPUT INSERTED.RevisionId VALUES (@j, 1, @by);", ("@j", t.Job.JobId), ("@by", by));
        return (id, 1);
    }

    internal static string CleanName(string? fileName)
    {
        // Only the final part: a browser may send a full path. The name never decides where bytes go (the key is built from ids).
        var name = Path.GetFileName((fileName ?? "").Replace('\\', '/')).Trim();
        if (name.Length == 0) throw new JobRuleViolationException("The file needs a name.");
        if (name.Length > 260) name = name[^260..];
        if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) throw new JobRuleViolationException("Only PDF drawings can be uploaded.");
        if (name.Any(char.IsControl)) throw new JobRuleViolationException("That file name has characters that are not allowed.");
        return name;
    }

    private static byte[]? ParseSha(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var text = hex.Trim();
        if (text.Length != 64 || !text.All(Uri.IsHexDigit)) throw new ArgumentException("A checksum must be 64 hexadecimal characters (SHA-256).");
        return Convert.FromHexString(text);
    }

    /// <summary>The PDF specification allows the header anywhere in the first kilobyte.</summary>
    private static bool LooksLikePdf(byte[] head) => head.AsSpan().IndexOf("%PDF-"u8) >= 0;

    private static async Task<T> ScalarAsync<T>(SqlConnection c, SqlTransaction? tx, CancellationToken ct, string sql, params (string Name, object? Value)[] args) where T : struct =>
        (await ScalarOrNullAsync<T>(c, tx, ct, sql, args)) ?? throw new InvalidOperationException("The statement returned nothing.");

    private static async Task<T?> ScalarOrNullAsync<T>(SqlConnection c, SqlTransaction? tx, CancellationToken ct, string sql, params (string Name, object? Value)[] args) where T : struct
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in args) JobRepository.P(command, name, value);
        var result = await command.ExecuteScalarAsync(ct);
        if (result is null or DBNull) return null;
        return (T)Convert.ChangeType(result, typeof(T));
    }

    private const string SessionSelect =
        """
        SELECT u.UploadSessionId, u.PublicId, u.JobId, u.OriginalName, u.TotalBytes, u.ExpectedSha256, u.ReceivedBytes, u.Status, u.StagingKey,
               u.StartedBy, u.StartedSessionId, u.StartedUtc, u.LastActivityUtc, u.ExpiresUtc,
               CASE WHEN u.ExpiresUtc <= SYSUTCDATETIME() THEN 1 ELSE 0 END, f.PublicId
        FROM ductwork.UploadSession u{HINT}
        LEFT JOIN ductwork.SourceFile f ON f.SourceFileId = u.SourceFileId
        """;

    private async Task<List<UploadSessionRecord>> ReadSessionAsync(SqlConnection c, SqlTransaction? tx, string where, CancellationToken ct,
        (string Name, object? Value) arg = default, bool forUpdate = false)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = SessionSelect.Replace("{HINT}", forUpdate ? " WITH (UPDLOCK, ROWLOCK)" : "") + " " + where;
        if (arg.Name is not null) JobRepository.P(command, arg.Name, arg.Value);
        var list = new List<UploadSessionRecord>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new UploadSessionRecord
            {
                UploadSessionId = r.GetInt64(0), PublicId = r.GetGuid(1), JobId = r.GetInt64(2), OriginalName = r.GetString(3), TotalBytes = r.GetInt64(4),
                ExpectedSha256 = r.IsDBNull(5) ? null : Convert.ToHexString((byte[])r[5]).ToLowerInvariant(),
                ReceivedBytes = r.GetInt64(6), Status = Enum.Parse<UploadStatus>(r.GetString(7)), StagingKey = r.GetString(8), StartedBy = r.GetString(9),
                StartedSessionId = r.GetString(10), StartedUtc = r.GetDateTime(11), LastActivityUtc = r.GetDateTime(12), ExpiresUtc = r.GetDateTime(13),
                IsExpired = r.GetInt32(14) == 1, SourceFilePublicId = r.IsDBNull(15) ? null : r.GetGuid(15), ChunkBytes = _settings.ChunkBytes
            });
        return list;
    }

    private static async Task<List<SourceFileRecord>> ReadFileAsync(SqlConnection c, SqlTransaction? tx, string where, CancellationToken ct,
        params (string Name, object? Value)[] args)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            """
            SELECT f.SourceFileId, f.PublicId, f.JobId, f.RevisionId, r.RevisionNo, f.OriginalName, f.Sha256, f.Bytes, f.ContentType, f.StorageKey,
                   d.PublicId, f.UploadedBy, f.UploadedUtc
            FROM ductwork.SourceFile f
            JOIN ductwork.DrawingSetRevision r ON r.RevisionId = f.RevisionId
            LEFT JOIN ductwork.SourceFile d ON d.SourceFileId = f.DuplicateOfSourceFileId
            """ + " " + where;
        foreach (var (name, value) in args) JobRepository.P(command, name, value);
        var list = new List<SourceFileRecord>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new SourceFileRecord
            {
                SourceFileId = r.GetInt64(0), PublicId = r.GetGuid(1), JobId = r.GetInt64(2), RevisionId = r.GetInt64(3), RevisionNo = r.GetInt32(4),
                OriginalName = r.GetString(5), Sha256 = Convert.ToHexString((byte[])r[6]).ToLowerInvariant(), Bytes = r.GetInt64(7), ContentType = r.GetString(8),
                StorageKey = r.IsDBNull(9) ? null : r.GetString(9), DuplicateOfPublicId = r.IsDBNull(10) ? null : r.GetGuid(10),
                UploadedBy = r.GetString(11), UploadedUtc = r.GetDateTime(12)
            });
        return list;
    }
}
