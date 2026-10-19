using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Drawings;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Archive;

public sealed record ManifestFile(string Path, string Sha256, long Bytes, long? Rows);

public sealed record ManifestSource(long SourceFileId, Guid PublicId, string Entry, string Sha256, long Bytes, string OriginalName, string StorageKey);

/// <summary>The index of a zip: what is in it and what each part must hash to. Read with nothing but JSON tooling.</summary>
public sealed record ArchiveManifest
{
    public int FormatVersion { get; init; }
    public string ContractVersion { get; init; } = "";
    public DateTime CreatedUtc { get; init; }
    public string CreatedBy { get; init; } = "";
    public long JobId { get; init; }
    public Guid JobPublicId { get; init; }
    public string JobName { get; init; } = "";
    public int CompletionVersion { get; init; }
    public string StateAtArchive { get; init; } = "";
    public List<ManifestFile> Files { get; init; } = [];
    public Dictionary<string, long> Tables { get; init; } = [];
    public List<ManifestSource> Sources { get; init; } = [];
    public string DataHash { get; init; } = "";
    public long? AuditHeadEventId { get; init; }
    public string? AuditHeadHash { get; init; }
}

internal static class ArchiveJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

/// <summary>
/// Writes a job as a self-describing zip: manifest.json, data/*.jsonl (the heavy rows, enough to rebuild the job), reference/*.jsonl,
/// provenance/*.jsonl (the shared records the job points at, copied in), summary.json, tags.json, and sources/ with every source
/// file. Everything is canonical (ordered by key), so the same job always produces the same data.
/// </summary>
internal sealed class ArchiveWriter(Func<CancellationToken, Task<SqlConnection>> open, IArtifactStore sources)
{
    public async Task<ArchiveManifest> WriteAsync(long jobId, string createdBy, Stream output, CancellationToken ct)
    {
        await using var connection = await open(ct);
        // Repeatable read holds the job's rows still while they are read. The job is frozen (Archiving) so nothing is waiting on them.
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);

        var job = await ReadJobHeaderAsync(connection, tx, jobId, ct);
        var files = new List<ManifestFile>();
        var counts = new Dictionary<string, long>();
        var sourceList = new List<ManifestSource>();
        string dataHash;

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var table in ArchiveSchema.AllTables)
            {
                var isDetail = ArchiveSchema.Detail.Contains(table);
                var (rows, sha, bytes) = await WriteTableAsync(zip, ArchiveSchema.EntryPath(table), connection, tx, table, jobId,
                    isDetail ? hasher : null, ct);
                files.Add(new ManifestFile(ArchiveSchema.EntryPath(table), sha, bytes, rows));
                counts[table.Name] = rows;
            }

            dataHash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();

            files.Add(await WriteJsonAsync(zip, "tags.json", await ReadTagsAsync(connection, tx, jobId, ct), ct));
            files.Add(await WriteJsonAsync(zip, "summary.json", new { job.Name, job.CompletionVersion, job.StateAtArchive, counts = counts.Where(c => ArchiveSchema.Detail.Any(d => d.Name == c.Key)).ToDictionary(c => c.Key, c => c.Value), job.SummaryJson }, ct));

            // Source files: every one, always, because a purged job whose source was later lost could not be rebuilt without them.
            foreach (var file in await ReadSourceFilesAsync(connection, tx, jobId, ct))
            {
                var entry = $"sources/{file.PublicId:D}.bin";
                var (sha, bytes) = await CopySourceAsync(zip, entry, file, ct);
                files.Add(new ManifestFile(entry, sha, bytes, null));
                sourceList.Add(new ManifestSource(file.SourceFileId, file.PublicId, entry, sha, bytes, file.OriginalName, file.StorageKey));
            }

            var (headId, headHash) = await ReadAuditHeadAsync(connection, tx, jobId, ct);
            var manifest = new ArchiveManifest
            {
                FormatVersion = ArchiveSchema.FormatVersion, ContractVersion = ContractInfo.Version, CreatedUtc = DateTime.UtcNow, CreatedBy = createdBy,
                JobId = jobId, JobPublicId = job.PublicId, JobName = job.Name, CompletionVersion = job.CompletionVersion, StateAtArchive = job.StateAtArchive,
                Files = files, Tables = counts, Sources = sourceList, DataHash = dataHash, AuditHeadEventId = headId, AuditHeadHash = headHash
            };
            await WriteJsonAsync(zip, "manifest.json", manifest, ct);
            await tx.CommitAsync(ct);
            return manifest;
        }
    }

    /// <summary>A hash over every detail row of a job, in canonical form. Equal before and after an archive and restore, and equal to the one in a zip when the job has not changed since.</summary>
    public static async Task<(string Hash, Dictionary<string, long> Counts)> FingerprintAsync(SqlConnection connection, SqlTransaction? tx, long jobId, CancellationToken ct)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var counts = new Dictionary<string, long>();
        foreach (var table in ArchiveSchema.Detail)
        {
            await using var sink = new HashingSink(hasher);
            counts[table.Name] = await StreamRowsAsync(connection, tx, table, jobId, sink, ct);
        }

        return (Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant(), counts);
    }

    // ------------------------------------------------------------------ pieces

    private static async Task<(long Rows, string Sha, long Bytes)> WriteTableAsync(ZipArchive zip, string path, SqlConnection c, SqlTransaction tx,
        TableSpec table, long jobId, IncrementalHash? dataHash, CancellationToken ct)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var counting = new HashingSink(null, stream, dataHash);
        var rows = await StreamRowsAsync(c, tx, table, jobId, counting, ct);
        await counting.FlushAsync(ct);
        return (rows, counting.Hex(), counting.Length);
    }

    private static async Task<long> StreamRowsAsync(SqlConnection c, SqlTransaction? tx, TableSpec table, long jobId, Stream sink, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = table.Select;
        command.Parameters.AddWithValue("@j", jobId);
        command.CommandTimeout = 600;
        await using var reader = await command.ExecuteReaderAsync(ct);

        long rows = 0;
        var buffer = new MemoryStream();
        while (await reader.ReadAsync(ct))
        {
            buffer.SetLength(0);
            await using (var writer = new Utf8JsonWriter(buffer)) RowJson.WriteRow(writer, reader);
            buffer.WriteByte((byte)'\n');
            buffer.Position = 0;
            await buffer.CopyToAsync(sink, ct);
            rows++;
        }

        return rows;
    }

    private static async Task<ManifestFile> WriteJsonAsync(ZipArchive zip, string path, object value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, ArchiveJson.Options);
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        await using (var stream = entry.Open()) await stream.WriteAsync(bytes, ct);
        return new ManifestFile(path, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length, null);
    }

    private async Task<(string Sha, long Bytes)> CopySourceAsync(ZipArchive zip, string entryPath, SourceRow file, CancellationToken ct)
    {
        await using var from = await sources.OpenReadAsync(file.StorageKey, ct)
            ?? throw new InvalidOperationException($"The source file “{file.OriginalName}” is not in storage, so this job cannot be archived safely. Restore it or contact your administrator.");

        var entry = zip.CreateEntry(entryPath, CompressionLevel.NoCompression);
        await using var to = entry.Open();
        await using var sink = new HashingSink(null, to, null);
        await from.CopyToAsync(sink, ct);
        await sink.FlushAsync(ct);

        if (!string.Equals(sink.Hex(), Convert.ToHexString(file.Sha256).ToLowerInvariant(), StringComparison.Ordinal) || sink.Length != file.Bytes)
            throw new InvalidOperationException($"The stored copy of “{file.OriginalName}” does not match the hash recorded when it was uploaded, so this job cannot be archived safely.");
        return (sink.Hex(), sink.Length);
    }

    private sealed record JobHeader(Guid PublicId, string Name, int CompletionVersion, string StateAtArchive, string? SummaryJson);

    private static async Task<JobHeader> ReadJobHeaderAsync(SqlConnection c, SqlTransaction tx, long jobId, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT PublicId, Name, CompletionVersion, ISNULL(StateBeforeArchive, State), SummaryJson FROM ductwork.Job WHERE JobId = @j;";
        command.Parameters.AddWithValue("@j", jobId);
        await using var r = await command.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new JobHeader(r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4));
    }

    private static async Task<List<string>> ReadTagsAsync(SqlConnection c, SqlTransaction tx, long jobId, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT t.Name FROM ductwork.JobTag jt JOIN ductwork.Tag t ON t.TagId = jt.TagId WHERE jt.JobId = @j ORDER BY t.NameNormalized;";
        command.Parameters.AddWithValue("@j", jobId);
        var tags = new List<string>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) tags.Add(r.GetString(0));
        return tags;
    }

    private sealed record SourceRow(long SourceFileId, Guid PublicId, string OriginalName, byte[] Sha256, long Bytes, string StorageKey);

    private static async Task<List<SourceRow>> ReadSourceFilesAsync(SqlConnection c, SqlTransaction tx, long jobId, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT SourceFileId, PublicId, OriginalName, Sha256, Bytes, StorageKey FROM ductwork.SourceFile WHERE JobId = @j AND StorageKey IS NOT NULL ORDER BY SourceFileId;";
        command.Parameters.AddWithValue("@j", jobId);
        var list = new List<SourceRow>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new SourceRow(r.GetInt64(0), r.GetGuid(1), r.GetString(2), (byte[])r.GetValue(3), r.GetInt64(4), r.GetString(5)));
        return list;
    }

    private static async Task<(long? EventId, string? Hash)> ReadAuditHeadAsync(SqlConnection c, SqlTransaction tx, long jobId, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT TOP 1 EventId, Hash FROM ductwork.JobAuditEvent WHERE JobId = @j ORDER BY EventId DESC;";
        command.Parameters.AddWithValue("@j", jobId);
        await using var r = await command.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetInt64(0), r.GetString(1)) : (null, null);
    }
}

/// <summary>
/// A write-only stream that hashes and counts what passes through, optionally forwarding it and feeding a second running hash.
/// Used both to build an archive entry and to fingerprint rows without storing them.
/// </summary>
internal sealed class HashingSink : Stream
{
    private readonly IncrementalHash _own = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly IncrementalHash? _shared;
    private readonly Stream? _forward;
    private string? _hex;

    public HashingSink(IncrementalHash? shared, Stream? forward = null, IncrementalHash? alsoFeed = null)
    {
        // For a fingerprint the shared hash is the only consumer; for an entry the own hash is, and alsoFeed continues the data hash.
        _shared = shared ?? alsoFeed;
        _forward = forward;
        UseOwn = shared is null;
    }

    private bool UseOwn { get; }
    private long _length;

    public override long Length => _length;

    public string Hex() => _hex ??= Convert.ToHexString(_own.GetHashAndReset()).ToLowerInvariant();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (UseOwn) _own.AppendData(buffer);
        _shared?.AppendData(buffer);
        _forward?.Write(buffer);
        _length += buffer.Length;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (UseOwn) _own.AppendData(buffer.Span);
        _shared?.AppendData(buffer.Span);
        if (_forward is not null) await _forward.WriteAsync(buffer, cancellationToken);
        _length += buffer.Length;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => _forward?.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _forward?.FlushAsync(cancellationToken) ?? Task.CompletedTask;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Position { get => _length; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _own.Dispose();
        base.Dispose(disposing);
    }
}
