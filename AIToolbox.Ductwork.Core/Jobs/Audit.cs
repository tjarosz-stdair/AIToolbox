using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Jobs;

/// <summary>
/// Who is acting and why, passed to every mutating data-layer method (plan section 10). It is built by
/// the server from the authenticated identity and never from client input, which is why the client
/// cannot choose its own actor, time or session.
/// </summary>
public sealed record AuditContext
{
    public required ActorType ActorType { get; init; }
    public string? ActorEmail { get; init; }
    public string? ActorDisplayName { get; init; }
    public string? SessionId { get; init; }
    public string? ClientIp { get; init; }
    public string? UserAgent { get; init; }
    /// <summary>Whether the identity is an AIToolbox administrator. Set from the server's user table.</summary>
    public bool IsAdmin { get; init; }
    public string? Reason { get; init; }
    /// <summary>Groups the events of one bulk operation or workflow.</summary>
    public Guid? CorrelationId { get; init; }
    public long? ProcessingTaskId { get; init; }
    public long? ProducerId { get; init; }
    public long? FencingToken { get; init; }

    public static AuditContext ForUser(string email, string? displayName = null, string? sessionId = null,
        string? clientIp = null, string? userAgent = null, bool isAdmin = false, string? reason = null, Guid? correlationId = null) => new()
    {
        ActorType = ActorType.User,
        ActorEmail = Normalize(email),
        ActorDisplayName = displayName,
        SessionId = sessionId,
        ClientIp = clientIp,
        UserAgent = userAgent,
        IsAdmin = isAdmin,
        Reason = reason,
        CorrelationId = correlationId
    };

    public static AuditContext ForSystem(string name, string? reason = null, Guid? correlationId = null) => new()
    {
        ActorType = ActorType.System,
        ActorDisplayName = name,
        Reason = reason,
        CorrelationId = correlationId
    };

    /// <summary>The same context with a reason for one action.</summary>
    public AuditContext WithReason(string? reason) => this with { Reason = reason };

    public void EnsureValid()
    {
        if (ActorType == ActorType.User && string.IsNullOrWhiteSpace(ActorEmail))
            throw new ArgumentException("A user action must name the user.");
    }

    public static string Normalize(string email) => (email ?? string.Empty).Trim().ToLowerInvariant();
}

/// <summary>One thing that happened, described by the data layer. The context supplies who and why.</summary>
public sealed record AuditEntry(
    string Category,
    string Action,
    string Summary,
    string? SubjectType = null,
    string? SubjectPublicId = null,
    string? BeforeJson = null,
    string? AfterJson = null);

public sealed record AuditEvent
{
    public long JobId { get; init; }
    public long EventId { get; init; }
    public DateTime OccurredUtc { get; init; }
    public ActorType ActorType { get; init; }
    public string? ActorEmail { get; init; }
    public string? ActorDisplayName { get; init; }
    public string? ActorSessionId { get; init; }
    public string? ClientIp { get; init; }
    public string? UserAgent { get; init; }
    public required string Category { get; init; }
    public required string Action { get; init; }
    public string? SubjectType { get; init; }
    public string? SubjectPublicId { get; init; }
    public required string Summary { get; init; }
    public string? BeforeJson { get; init; }
    public string? AfterJson { get; init; }
    public string? Reason { get; init; }
    public Guid? CorrelationId { get; init; }
    public long? ProcessingTaskId { get; init; }
    public long? ProducerId { get; init; }
    public long? RevisionId { get; init; }
    public long? FencingToken { get; init; }
    public long StateVersion { get; init; }
    public required string PrevHash { get; init; }
    public required string Hash { get; init; }
}

public sealed record ChainVerification(bool IsValid, long EventCount, long? FirstBadEventId, string? Problem, string? HeadHash);

public sealed record AuditFilter
{
    public string? Category { get; init; }
    public string? ActorEmail { get; init; }
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
    public string? Text { get; init; }
    public long? AfterEventId { get; init; }
    public int Limit { get; init; } = 500;
}

/// <summary>
/// Tamper evidence (plan section 10). Each event's hash covers the previous event's hash and the
/// event's own canonical content, so changing, removing or reordering any past event breaks every hash
/// after it, and a verify pass can name the first break.
/// </summary>
public static class AuditHasher
{
    /// <summary>The previous-hash of a job's first event.</summary>
    public static readonly string Genesis = new('0', 64);

    public static string Hash(string prevHash, AuditEvent e)
    {
        var bytes = Encoding.UTF8.GetBytes(prevHash + "\n" + Canonical(e));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>
    /// Fixed field order, every field present (null written as null), times as UTC ticks, so the same
    /// event always produces the same bytes whatever the database or the serializer defaults are.
    /// </summary>
    public static string Canonical(AuditEvent e)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("jobId", e.JobId);
            w.WriteNumber("eventId", e.EventId);
            w.WriteString("occurredUtc", e.OccurredUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            w.WriteString("actorType", e.ActorType.ToString());
            Str(w, "actorEmail", e.ActorEmail);
            Str(w, "actorDisplayName", e.ActorDisplayName);
            Str(w, "actorSessionId", e.ActorSessionId);
            Str(w, "clientIp", e.ClientIp);
            Str(w, "userAgent", e.UserAgent);
            Str(w, "category", e.Category);
            Str(w, "action", e.Action);
            Str(w, "subjectType", e.SubjectType);
            Str(w, "subjectPublicId", e.SubjectPublicId);
            Str(w, "summary", e.Summary);
            Str(w, "beforeJson", e.BeforeJson);
            Str(w, "afterJson", e.AfterJson);
            Str(w, "reason", e.Reason);
            Str(w, "correlationId", e.CorrelationId?.ToString("D"));
            Num(w, "processingTaskId", e.ProcessingTaskId);
            Num(w, "producerId", e.ProducerId);
            Num(w, "revisionId", e.RevisionId);
            Num(w, "fencingToken", e.FencingToken);
            w.WriteNumber("stateVersion", e.StateVersion);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Str(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name);
        else w.WriteString(name, value);
    }

    private static void Num(Utf8JsonWriter w, string name, long? value)
    {
        if (value is null) w.WriteNull(name);
        else w.WriteNumber(name, value.Value);
    }
}

/// <summary>
/// Appends and reads a job's audit log. <see cref="AppendAsync"/> must be called inside the same
/// transaction as the change it records, by a caller that already holds the job row's lock, so events
/// are numbered and chained in order and no change exists without its event.
/// </summary>
public static class AuditLog
{
    public static async Task<AuditEvent> AppendAsync(
        SqlConnection connection, SqlTransaction transaction, long jobId, long stateVersion,
        AuditEntry entry, AuditContext context, CancellationToken cancellationToken = default)
    {
        context.EnsureValid();
        if (string.IsNullOrWhiteSpace(entry.Summary)) throw new ArgumentException("An audit entry needs a summary.", nameof(entry));

        long lastId = 0;
        string prev = AuditHasher.Genesis;
        await using (var last = connection.CreateCommand())
        {
            last.Transaction = transaction;
            last.CommandText =
                "SELECT TOP 1 EventId, Hash FROM ductwork.JobAuditEvent WITH (UPDLOCK, HOLDLOCK) WHERE JobId = @j ORDER BY EventId DESC;";
            last.Parameters.AddWithValue("@j", jobId);
            await using var reader = await last.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                lastId = reader.GetInt64(0);
                prev = reader.GetString(1).Trim();
            }
        }

        DateTime now;
        await using (var clock = connection.CreateCommand())
        {
            clock.Transaction = transaction;
            clock.CommandText = "SELECT SYSUTCDATETIME();"; // the database clock, never the client's
            now = (DateTime)(await clock.ExecuteScalarAsync(cancellationToken))!;
        }

        var unhashed = new AuditEvent
        {
            JobId = jobId,
            EventId = lastId + 1,
            OccurredUtc = DateTime.SpecifyKind(now, DateTimeKind.Utc),
            ActorType = context.ActorType,
            ActorEmail = context.ActorEmail,
            ActorDisplayName = context.ActorDisplayName,
            ActorSessionId = context.SessionId,
            ClientIp = context.ClientIp,
            UserAgent = context.UserAgent,
            Category = entry.Category,
            Action = entry.Action,
            SubjectType = entry.SubjectType,
            SubjectPublicId = entry.SubjectPublicId,
            Summary = entry.Summary,
            BeforeJson = entry.BeforeJson,
            AfterJson = entry.AfterJson,
            Reason = context.Reason,
            CorrelationId = context.CorrelationId,
            ProcessingTaskId = context.ProcessingTaskId,
            ProducerId = context.ProducerId,
            FencingToken = context.FencingToken,
            StateVersion = stateVersion,
            PrevHash = prev,
            Hash = string.Empty
        };
        var stored = unhashed with { Hash = AuditHasher.Hash(prev, unhashed) };

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO ductwork.JobAuditEvent
                (JobId, EventId, OccurredUtc, ActorType, ActorEmail, ActorDisplayName, ActorSessionId, ClientIp, UserAgent,
                 Category, Action, SubjectType, SubjectPublicId, Summary, BeforeJson, AfterJson, Reason, CorrelationId,
                 ProcessingTaskId, ProducerId, RevisionId, FencingToken, StateVersion, PrevHash, Hash)
            VALUES
                (@JobId, @EventId, @OccurredUtc, @ActorType, @ActorEmail, @ActorDisplayName, @ActorSessionId, @ClientIp, @UserAgent,
                 @Category, @Action, @SubjectType, @SubjectPublicId, @Summary, @BeforeJson, @AfterJson, @Reason, @CorrelationId,
                 @ProcessingTaskId, @ProducerId, @RevisionId, @FencingToken, @StateVersion, @PrevHash, @Hash);
            """;
        Add(insert, "@JobId", stored.JobId);
        Add(insert, "@EventId", stored.EventId);
        insert.Parameters.Add(new SqlParameter("@OccurredUtc", System.Data.SqlDbType.DateTime2) { Scale = 7, Value = stored.OccurredUtc });
        Add(insert, "@ActorType", stored.ActorType.ToString());
        Add(insert, "@ActorEmail", stored.ActorEmail);
        Add(insert, "@ActorDisplayName", stored.ActorDisplayName);
        Add(insert, "@ActorSessionId", stored.ActorSessionId);
        Add(insert, "@ClientIp", stored.ClientIp);
        Add(insert, "@UserAgent", stored.UserAgent);
        Add(insert, "@Category", stored.Category);
        Add(insert, "@Action", stored.Action);
        Add(insert, "@SubjectType", stored.SubjectType);
        Add(insert, "@SubjectPublicId", stored.SubjectPublicId);
        Add(insert, "@Summary", stored.Summary);
        Add(insert, "@BeforeJson", stored.BeforeJson);
        Add(insert, "@AfterJson", stored.AfterJson);
        Add(insert, "@Reason", stored.Reason);
        Add(insert, "@CorrelationId", stored.CorrelationId);
        Add(insert, "@ProcessingTaskId", stored.ProcessingTaskId);
        Add(insert, "@ProducerId", stored.ProducerId);
        Add(insert, "@RevisionId", stored.RevisionId);
        Add(insert, "@FencingToken", stored.FencingToken);
        Add(insert, "@StateVersion", stored.StateVersion);
        Add(insert, "@PrevHash", stored.PrevHash);
        Add(insert, "@Hash", stored.Hash);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return stored;
    }

    public static async Task<IReadOnlyList<AuditEvent>> ListAsync(
        SqlConnection connection, long jobId, AuditFilter? filter = null, CancellationToken cancellationToken = default)
    {
        filter ??= new AuditFilter();
        var events = new List<AuditEvent>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT TOP (@limit) {Columns}
            FROM ductwork.JobAuditEvent
            WHERE JobId = @j
              AND (@category IS NULL OR Category = @category)
              AND (@actor IS NULL OR ActorEmail = @actor)
              AND (@from IS NULL OR OccurredUtc >= @from)
              AND (@to IS NULL OR OccurredUtc <= @to)
              AND (@after IS NULL OR EventId > @after)
              AND (@text IS NULL OR Summary LIKE '%' + @text + '%' OR Action LIKE '%' + @text + '%')
            ORDER BY EventId;
            """;
        command.Parameters.AddWithValue("@limit", Math.Clamp(filter.Limit, 1, 5000));
        command.Parameters.AddWithValue("@j", jobId);
        Add(command, "@category", filter.Category);
        Add(command, "@actor", filter.ActorEmail is null ? null : AuditContext.Normalize(filter.ActorEmail));
        Add(command, "@from", filter.FromUtc);
        Add(command, "@to", filter.ToUtc);
        Add(command, "@after", filter.AfterEventId);
        Add(command, "@text", string.IsNullOrWhiteSpace(filter.Text) ? null : filter.Text.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) events.Add(Read(reader));
        return events;
    }

    /// <summary>Recomputes the whole chain and names the first event that does not fit.</summary>
    public static async Task<ChainVerification> VerifyAsync(
        SqlConnection connection, long jobId, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM ductwork.JobAuditEvent WHERE JobId = @j ORDER BY EventId;";
        command.Parameters.AddWithValue("@j", jobId);

        var prev = AuditHasher.Genesis;
        long expectedId = 1, count = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var e = Read(reader);
            count++;
            if (e.EventId != expectedId)
                return new(false, count, expectedId, $"Event {expectedId} is missing: the log jumps to {e.EventId}.", prev);
            if (!string.Equals(e.PrevHash, prev, StringComparison.Ordinal))
                return new(false, count, e.EventId, $"Event {e.EventId} does not follow the event before it.", prev);
            var recomputed = AuditHasher.Hash(prev, e);
            if (!string.Equals(e.Hash, recomputed, StringComparison.Ordinal))
                return new(false, count, e.EventId, $"Event {e.EventId} was changed after it was written.", prev);
            prev = e.Hash;
            expectedId++;
        }

        return new(true, count, null, null, count == 0 ? null : prev);
    }

    private const string Columns =
        "JobId, EventId, OccurredUtc, ActorType, ActorEmail, ActorDisplayName, ActorSessionId, ClientIp, UserAgent, Category, Action, " +
        "SubjectType, SubjectPublicId, Summary, BeforeJson, AfterJson, Reason, CorrelationId, ProcessingTaskId, ProducerId, RevisionId, " +
        "FencingToken, StateVersion, PrevHash, Hash";

    private static AuditEvent Read(SqlDataReader r) => new()
    {
        JobId = r.GetInt64(0),
        EventId = r.GetInt64(1),
        OccurredUtc = DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc),
        ActorType = Enum.Parse<ActorType>(r.GetString(3)),
        ActorEmail = Str(r, 4),
        ActorDisplayName = Str(r, 5),
        ActorSessionId = Str(r, 6),
        ClientIp = Str(r, 7),
        UserAgent = Str(r, 8),
        Category = r.GetString(9),
        Action = r.GetString(10),
        SubjectType = Str(r, 11),
        SubjectPublicId = Str(r, 12),
        Summary = r.GetString(13),
        BeforeJson = Str(r, 14),
        AfterJson = Str(r, 15),
        Reason = Str(r, 16),
        CorrelationId = r.IsDBNull(17) ? null : r.GetGuid(17),
        ProcessingTaskId = Lng(r, 18),
        ProducerId = Lng(r, 19),
        RevisionId = Lng(r, 20),
        FencingToken = Lng(r, 21),
        StateVersion = r.GetInt64(22),
        PrevHash = r.GetString(23).Trim(),
        Hash = r.GetString(24).Trim()
    };

    private static string? Str(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static long? Lng(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    private static void Add(SqlCommand c, string name, object? value) => c.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
