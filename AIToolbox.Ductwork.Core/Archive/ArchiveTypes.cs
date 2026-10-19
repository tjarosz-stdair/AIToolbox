namespace AIToolbox.Ductwork.Core.Archive;

public enum ArchiveKind { ZipOnly, Purge }

/// <summary>Writing: being made. Ready: a verified zip, data still in SQL. PurgePending: verified, detail rows being removed. Purged: the zip is the only full copy.</summary>
public enum ArchiveStatus { Writing, Ready, PurgePending, Purged, Failed, Superseded }

public enum ArchiveOperationKind { ArchiveZip, ArchivePurge, Restore }

public enum ArchiveOperationStatus { Queued, Running, Succeeded, Failed }

public sealed record ArchiveRecord
{
    public long ArchiveId { get; init; }
    public Guid PublicId { get; init; }
    public long JobId { get; init; }
    public int CompletionVersion { get; init; }
    public ArchiveKind Kind { get; init; }
    public ArchiveStatus Status { get; init; }
    public string StateAtArchive { get; init; } = "";
    public int FormatVersion { get; init; }
    public string ContractVersion { get; init; } = "";
    public string? StorageKey { get; init; }
    public string? FileName { get; init; }
    public long? SizeBytes { get; init; }
    public string? Sha256 { get; init; }
    public string? DataHash { get; init; }
    public IReadOnlyDictionary<string, long> TableCounts { get; init; } = new Dictionary<string, long>();
    public int? SourceFileCount { get; init; }
    public long? SourceBytes { get; init; }
    public long? AuditHeadEventId { get; init; }
    public string? AuditHeadHash { get; init; }
    public IReadOnlyList<ArchiveLocation> Locations { get; init; } = [];
    public string? LastProblem { get; init; }
    public DateTime? LastProblemUtc { get; init; }
    public DateTime CreatedUtc { get; init; }
    public string CreatedBy { get; init; } = "";
    public DateTime? VerifiedUtc { get; init; }
    public DateTime? PurgedUtc { get; init; }
    public DateTime? RestoredUtc { get; init; }
    public DateTime? SupersededUtc { get; init; }
}

/// <summary>One place a zip has been, so a move or a relink is never silent.</summary>
public sealed record ArchiveLocation(string StorageKey, DateTime SinceUtc, string By, string Note);

public sealed record ArchiveOperationRecord
{
    public long OperationId { get; init; }
    public Guid PublicId { get; init; }
    public long JobId { get; init; }
    public long ArchiveId { get; init; }
    public ArchiveOperationKind Kind { get; init; }
    public ArchiveOperationStatus Status { get; init; }
    public string Step { get; init; } = "";
    public string? Message { get; init; }
    public long ProgressDone { get; init; }
    public long? ProgressTotal { get; init; }
    public int Attempt { get; init; }
    public int MaxAttempts { get; init; }
    public string RequestedBy { get; init; } = "";
    public DateTime RequestedUtc { get; init; }
    public DateTime? StartedUtc { get; init; }
    public DateTime? FinishedUtc { get; init; }
    public string? Error { get; init; }
}

/// <summary>What the verifier found. A zip is relied on (and anything is purged) only when <see cref="Ok"/>.</summary>
public sealed record ArchiveVerification(bool Ok, IReadOnlyList<string> Problems, string? Sha256, IReadOnlyDictionary<string, long> Counts)
{
    public static ArchiveVerification Failed(params string[] problems) => new(false, problems, null, new Dictionary<string, long>());
}

public sealed class ArchiveSettings
{
    /// <summary>Rows removed per statement during a purge, so a job with many thousands of rows never holds one huge transaction.</summary>
    public int PurgeBatchRows { get; init; } = 2000;

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Called after each batch of a purge with the number of batches so far. A seam for tests to interrupt a purge part-way.</summary>
    public Action<int>? AfterPurgeBatch { get; init; }
}

/// <summary>The zip is not where its record says it is.</summary>
public sealed class ArchiveMissingException(string key) : Exception("Archive file not found. Contact your administrator.")
{
    public string StorageKey { get; } = key;
}

/// <summary>The zip is there but is not what was written: a hash, a count or a reference does not hold.</summary>
public sealed class ArchiveCorruptException(IReadOnlyList<string> problems) : Exception("The archive failed verification: " + string.Join(" ", problems.Take(3)))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>A hash over every heavy row of a job in canonical form: equal before and after an archive and restore, which is how that round trip is proven.</summary>
public static class ArchiveFingerprint
{
    public static async Task<(string Hash, Dictionary<string, long> Counts)> OfAsync(Microsoft.Data.SqlClient.SqlConnection connection, long jobId, CancellationToken cancellationToken = default) =>
        await ArchiveWriter.FingerprintAsync(connection, null, jobId, cancellationToken);
}
