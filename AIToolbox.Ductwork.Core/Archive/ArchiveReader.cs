using System.Data;
using System.Data.Common;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AIToolbox.Ductwork.Core.Drawings;
using AIToolbox.Ductwork.Core.Jobs;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Archive;

/// <summary>
/// Reads a zip back and decides whether it can be relied on: the file hashes to what was recorded, every part hashes to what the
/// manifest says, row counts agree, and every row in the heavy tables points at something that exists. Nothing is trusted because
/// it was written a moment ago: the bytes are read back from where they now are.
/// </summary>
internal static class ArchiveReader
{
    public static async Task<(ArchiveManifest? Manifest, ArchiveVerification Result)> InspectAsync(
        IArchiveStore store, string storageKey, byte[]? expectedSha, long? expectedSize, CancellationToken ct)
    {
        var problems = new List<string>();

        string fileSha;
        long fileSize;
        await using (var raw = await store.OpenReadAsync(storageKey, ct) ?? throw new ArchiveMissingException(storageKey))
        {
            using var hash = SHA256.Create();
            fileSha = Convert.ToHexString(await hash.ComputeHashAsync(raw, ct)).ToLowerInvariant();
            fileSize = raw.Length;
        }

        if (expectedSha is not null && !string.Equals(fileSha, Convert.ToHexString(expectedSha), StringComparison.OrdinalIgnoreCase))
            problems.Add("The file does not hash to the value recorded when it was made.");
        if (expectedSize is not null && fileSize != expectedSize) problems.Add($"The file is {fileSize:N0} bytes, not the {expectedSize:N0} recorded.");
        if (problems.Count > 0) return (null, new ArchiveVerification(false, problems, fileSha, new Dictionary<string, long>()));

        await using var stream = await store.OpenReadAsync(storageKey, ct) ?? throw new ArchiveMissingException(storageKey);
        ZipArchive zip;
        try { zip = new ZipArchive(stream, ZipArchiveMode.Read); }
        catch (InvalidDataException) { return (null, ArchiveVerification.Failed("The file is not a readable zip.")); }

        using (zip)
        {
            var manifestEntry = zip.GetEntry("manifest.json");
            if (manifestEntry is null) return (null, ArchiveVerification.Failed("The archive has no manifest."));

            ArchiveManifest? manifest;
            await using (var m = manifestEntry.Open()) manifest = await JsonSerializer.DeserializeAsync<ArchiveManifest>(m, ArchiveJson.Options, ct);
            if (manifest is null) return (null, ArchiveVerification.Failed("The manifest could not be read."));
            if (manifest.FormatVersion < 1 || manifest.FormatVersion > ArchiveSchema.FormatVersion)
                return (null, ArchiveVerification.Failed($"The archive is format version {manifest.FormatVersion}; this server reads up to {ArchiveSchema.FormatVersion}."));

            var keys = new Dictionary<string, HashSet<string>>();
            var refs = new List<(string Table, string Column, string Parent, string Value)>();
            var counts = new Dictionary<string, long>();

            var listed = manifest.Files.ToDictionary(f => f.Path);
            foreach (var entry in zip.Entries.Where(e => e.FullName != "manifest.json"))
                if (!listed.ContainsKey(entry.FullName)) problems.Add($"“{entry.FullName}” is in the zip but not in the manifest.");

            foreach (var file in manifest.Files)
            {
                var entry = zip.GetEntry(file.Path);
                if (entry is null) { problems.Add($"“{file.Path}” is listed in the manifest but missing from the zip."); continue; }

                var spec = ArchiveSchema.AllTables.FirstOrDefault(t => ArchiveSchema.EntryPath(t) == file.Path);
                try
                {
                await using var part = entry.Open();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long bytes = 0;
                long rows = 0;

                if (spec is null)
                {
                    var buffer = new byte[81920];
                    int n;
                    while ((n = await part.ReadAsync(buffer, ct)) > 0) { hash.AppendData(buffer, 0, n); bytes += n; }
                }
                else
                {
                    var set = keys[spec.Name] = [];
                    var line = new MemoryStream();
                    var buffer = new byte[81920];
                    int n;
                    while ((n = await part.ReadAsync(buffer, ct)) > 0)
                    {
                        hash.AppendData(buffer, 0, n);
                        bytes += n;
                        for (var i = 0; i < n; i++)
                        {
                            if (buffer[i] != (byte)'\n') { line.WriteByte(buffer[i]); continue; }
                            rows += ReadRow(spec, line, set, refs, problems);
                            line.SetLength(0);
                        }
                    }

                    if (line.Length > 0) rows += ReadRow(spec, line, set, refs, problems);
                    counts[spec.Name] = rows;
                    if (file.Rows is { } expected && expected != rows) problems.Add($"{spec.Name} has {rows:N0} rows; the manifest says {expected:N0}.");
                }

                var sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (!string.Equals(sha, file.Sha256, StringComparison.OrdinalIgnoreCase)) problems.Add($"“{file.Path}” does not hash to the value in the manifest.");
                if (bytes != file.Bytes) problems.Add($"“{file.Path}” is {bytes:N0} bytes; the manifest says {file.Bytes:N0}.");
                }
                catch (InvalidDataException)
                {
                    problems.Add($"“{file.Path}” is damaged and cannot be read.");
                }
            }

            foreach (var source in manifest.Sources)
                if (!listed.ContainsKey(source.Entry)) problems.Add($"The source file “{source.OriginalName}” is not in the archive.");

            foreach (var (table, column, parent, value) in refs)
                if (!keys.TryGetValue(parent, out var set) || !set.Contains(value))
                    problems.Add($"{table}.{column} points at {parent} {value}, which is not in the archive.");

            return (manifest, new ArchiveVerification(problems.Count == 0, problems.Take(25).ToList(), fileSha, counts));
        }
    }

    private static int ReadRow(TableSpec spec, MemoryStream line, HashSet<string> keys, List<(string, string, string, string)> refs, List<string> problems)
    {
        try
        {
            using var doc = JsonDocument.Parse(line.GetBuffer().AsMemory(0, (int)line.Length));
            var row = doc.RootElement;
            if (row.ValueKind != JsonValueKind.Object) { problems.Add($"{spec.Name} has a line that is not an object."); return 1; }

            var key = RowJson.KeyText(row, spec.Key);
            if (key.Length == 0) problems.Add($"A {spec.Name} row has no {spec.Key}.");
            else if (!keys.Add(key)) problems.Add($"{spec.Name} {key} appears twice.");

            foreach (var r in spec.Refs)
                if (row.TryGetProperty(r.Column, out var v) && v.ValueKind != JsonValueKind.Null) refs.Add((spec.Name, r.Column, r.ParentTable, v.ToString()));
        }
        catch (JsonException) { problems.Add($"{spec.Name} has a line that is not valid JSON."); }

        return 1;
    }
}

/// <summary>
/// Puts a job's detail rows back. Every id is kept as it was: rules that stayed in SQL point at sheets and findings by id, so the
/// restored rows must be the same rows. Runs inside the caller's transaction, so a failure leaves nothing behind but files, which
/// the caller removes.
/// </summary>
internal static class ArchiveImporter
{
    private const int Chunk = 5000;

    public static async Task<Dictionary<string, long>> ImportAsync(SqlConnection c, SqlTransaction tx, ArchiveManifest manifest, ZipArchive zip, long jobId,
        Func<long, Task>? progress, CancellationToken ct)
    {
        // Restore only into a job that has none of these rows: a half-merged job would be worse than a refusal.
        foreach (var table in ArchiveSchema.Detail)
        {
            var live = await CountAsync(c, tx, table, jobId, ct);
            if (live > 0) throw new JobRuleViolationException($"This job already has {live:N0} {table.Name} row(s), so it cannot be restored over them.");
        }

        await RequireSharedRecordsAsync(c, tx, zip, ct);

        var inserted = new Dictionary<string, long>();
        long done = 0;
        foreach (var table in ArchiveSchema.Detail)
        {
            var entry = zip.GetEntry(ArchiveSchema.EntryPath(table)) ?? throw new ArchiveCorruptException([$"{table.Name} is missing from the archive."]);
            inserted[table.Name] = await InsertAsync(c, tx, table, entry, ct, async n => { done += n; if (progress is not null) await progress(done); });
        }

        return inserted;
    }

    private static async Task RequireSharedRecordsAsync(SqlConnection c, SqlTransaction tx, ZipArchive zip, CancellationToken ct)
    {
        var entry = zip.GetEntry("provenance/Producer.jsonl");
        if (entry is null) return;

        var wanted = new List<long>();
        await using (var s = entry.Open())
        using (var reader = new StreamReader(s))
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (line.Length == 0) continue;
                using var doc = JsonDocument.Parse(line);
                wanted.Add(doc.RootElement.GetProperty("ProducerId").GetInt64());
            }
        }

        foreach (var id in wanted)
        {
            await using var command = c.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "SELECT COUNT(*) FROM ductwork.Producer WHERE ProducerId = @p;";
            command.Parameters.AddWithValue("@p", id);
            if ((int)(await command.ExecuteScalarAsync(ct))! == 0)
                throw new JobRuleViolationException($"A shared record this job depends on (producer {id}) no longer exists here, so it cannot be restored safely. The archive keeps a copy of it.");
        }
    }

    private static async Task<int> CountAsync(SqlConnection c, SqlTransaction tx, TableSpec table, long jobId, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT COUNT(*) FROM {table.Qualified} WHERE {table.Where};";
        command.Parameters.AddWithValue("@j", jobId);
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task<long> InsertAsync(SqlConnection c, SqlTransaction tx, TableSpec table, ZipArchiveEntry entry, CancellationToken ct, Func<long, Task> advanced)
    {
        // The live table says what each column is; the file says what each value is. Columns the table no longer has are ignored,
        // and columns the file does not have take their default, which is what lets the format and the schema each change additively.
        var shape = new DataTable();
        await using (var schema = c.CreateCommand())
        {
            schema.Transaction = tx;
            schema.CommandText = $"SELECT * FROM {table.Qualified} WHERE 1 = 0;";
            await using var r = await schema.ExecuteReaderAsync(ct);
            foreach (var column in await r.GetColumnSchemaAsync(ct))
            {
                if (column.IsReadOnly == true && column.IsIdentity != true) continue;     // computed
                shape.Columns.Add(column.ColumnName, column.DataType ?? typeof(string));
            }
        }

        using var bulk = new SqlBulkCopy(c, SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.CheckConstraints, tx) { DestinationTableName = table.Qualified, BulkCopyTimeout = 0 };
        foreach (DataColumn column in shape.Columns) bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);

        long total = 0;
        var batch = shape.Clone();
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream);

        async Task FlushAsync()
        {
            if (batch.Rows.Count == 0) return;
            await bulk.WriteToServerAsync(batch, ct);
            total += batch.Rows.Count;
            await advanced(batch.Rows.Count);
            batch.Clear();
        }

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            var row = batch.NewRow();
            foreach (DataColumn column in batch.Columns)
                row[column] = doc.RootElement.TryGetProperty(column.ColumnName, out var value) ? RowJson.ReadValue(value, column.DataType) : DBNull.Value;
            batch.Rows.Add(row);
            if (batch.Rows.Count >= Chunk) await FlushAsync();
        }

        await FlushAsync();
        return total;
    }

    /// <summary>Writes the archive's source files back to the drawing store under the keys the restored rows name, checking each against its recorded hash.</summary>
    public static async Task<List<string>> RestoreSourcesAsync(ArchiveManifest manifest, ZipArchive zip, IArtifactStore store, Guid archivePublicId, CancellationToken ct)
    {
        var written = new List<string>();
        try
        {
            foreach (var source in manifest.Sources)
            {
                var entry = zip.GetEntry(source.Entry) ?? throw new ArchiveCorruptException([$"The source file “{source.OriginalName}” is not in the archive."]);
                var staging = $"restore/{archivePublicId:D}-{source.PublicId:D}";
                await store.DeleteStagingAsync(staging, ct);
                (long Written, byte[] Sha256) result;
                await using (var part = entry.Open()) result = await store.AppendStagingAsync(staging, 0, part, long.MaxValue, ct);

                if (result.Written != source.Bytes || !string.Equals(Convert.ToHexString(result.Sha256), source.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    await store.DeleteStagingAsync(staging, ct);
                    throw new ArchiveCorruptException([$"The source file “{source.OriginalName}” does not match its recorded hash."]);
                }

                await store.DeleteAsync(source.StorageKey, ct);       // a retry after a crash may find an earlier copy
                await store.PromoteAsync(staging, source.StorageKey, ct);
                written.Add(source.StorageKey);
            }
        }
        catch
        {
            foreach (var key in written) await store.DeleteAsync(key, CancellationToken.None);
            throw;
        }

        return written;
    }
}
