using System.Text.Json;
using AIToolbox.Ductwork.Core.Jobs;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Counting;

public enum BuildingKind { UnitType, Building, CommonArea, Other }

public enum MultiplierSource { Manual, ArchitecturalList, Suggested }

public sealed record BuildingTypeRecord
{
    public long BuildingTypeId { get; init; }
    public Guid PublicId { get; init; }
    public long JobId { get; init; }
    public required string Name { get; init; }
    public BuildingKind Kind { get; init; }
    /// <summary>The number of identical buildings or units. Null means not known yet; zero is a known count.</summary>
    public int? Multiplier { get; init; }
    public MultiplierSource? MultiplierSource { get; init; }
    public string? MultiplierEvidence { get; init; }
    public bool Confirmed { get; init; }
    public string? ConfirmedBy { get; init; }
    public DateTime? ConfirmedUtc { get; init; }
    public string? Notes { get; init; }
    public DateTime CreatedUtc { get; init; }
    public required string CreatedBy { get; init; }
}

public sealed record NewBuildingType
{
    public required string Name { get; init; }
    public BuildingKind Kind { get; init; } = BuildingKind.Other;
    public string? Notes { get; init; }
}

/// <summary>
/// Building types and their multipliers (plan sections 7 and 12). A multiplier is the number of identical
/// buildings or units a count stands for, and it multiplies every count on the sheet, so it is decided
/// by a person: a detector or the engine may only <i>suggest</i> one, and a suggestion is never
/// treated as confirmed. A job with counted items cannot be completed while any of its multipliers is
/// unconfirmed.
/// </summary>
public sealed class BuildingTypeRepository
{
    private readonly Func<CancellationToken, Task<SqlConnection>> _open;

    public BuildingTypeRepository(Func<CancellationToken, Task<SqlConnection>> openConnection) => _open = openConnection;

    public BuildingTypeRepository(string connectionString)
        : this(async ct =>
        {
            var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);
            return connection;
        })
    {
    }

    // ------------------------------------------------------------------ reads

    public async Task<BuildingTypeRecord?> GetAsync(long buildingTypeId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return await ReadAsync(connection, null, buildingTypeId, forUpdate: false, cancellationToken);
    }

    public async Task<IReadOnlyList<BuildingTypeRecord>> ListByJobAsync(long jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return await ListAsync(connection, null, jobId, cancellationToken);
    }

    // ------------------------------------------------------------------ setup (a person, holding the edit lock)

    public Task<BuildingTypeRecord> CreateAsync(long jobId, NewBuildingType type, EditToken editToken, AuditContext context,
        CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context, cancellationToken, async t =>
        {
            t.RequireEditable();
            var (name, normalized) = Normalize(type.Name);
            if (await FindByNameAsync(t.Connection, t.Transaction, jobId, normalized, t.Token) is not null)
                throw new JobRuleViolationException($"This job already has a building type called \"{name}\".");

            var created = await InsertAsync(t, name, normalized, type.Kind, type.Notes, JobRepository.Actor(context));
            await t.AuditAtCurrentVersionAsync(new AuditEntry("setup", "building-type.created", $"Added the building type \"{name}\".", "BuildingType",
                created.PublicId.ToString("D"), null, Describe(created)));
            return created;
        }, editToken, requires: JobCapability.Edit);

    public Task<BuildingTypeRecord> UpdateAsync(long buildingTypeId, string? name, BuildingKind? kind, string? notes, EditToken editToken,
        AuditContext context, CancellationToken cancellationToken = default) =>
        InTypeAsync(buildingTypeId, context, cancellationToken, editToken, JobCapability.Edit, async (t, current) =>
        {
            t.RequireEditable();
            var sets = new List<string>();
            var args = new List<(string, object?)> { ("@id", buildingTypeId) };
            var after = new Dictionary<string, object?>();
            var before = new Dictionary<string, object?>();

            if (name is not null)
            {
                var (display, normalized) = Normalize(name);
                if (normalized != Normalize(current.Name).Normalized)
                {
                    if (await FindByNameAsync(t.Connection, t.Transaction, current.JobId, normalized, t.Token) is not null)
                        throw new JobRuleViolationException($"This job already has a building type called \"{display}\".");
                    sets.Add("Name = @name, NameNormalized = @norm");
                    args.Add(("@name", display));
                    args.Add(("@norm", normalized));
                    before["name"] = current.Name;
                    after["name"] = display;
                }
            }

            if (kind is { } k && k != current.Kind)
            {
                sets.Add("Kind = @kind");
                args.Add(("@kind", k.ToString()));
                before["kind"] = current.Kind.ToString();
                after["kind"] = k.ToString();
            }

            if (notes is not null && !string.Equals(notes.Trim(), current.Notes ?? "", StringComparison.Ordinal))
            {
                sets.Add("Notes = @notes");
                args.Add(("@notes", string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()));
                before["notes"] = current.Notes;
                after["notes"] = notes.Trim();
            }

            if (sets.Count == 0) return current;

            await JobRepository.ExecAsync(t.Connection, t.Transaction, $"UPDATE ductwork.BuildingType SET {string.Join(", ", sets)} WHERE BuildingTypeId = @id;", t.Token, args.ToArray());
            await t.AuditAtCurrentVersionAsync(new AuditEntry("setup", "building-type.edited", $"Edited the building type \"{current.Name}\".", "BuildingType",
                current.PublicId.ToString("D"), JobRepository.Json(before), JobRepository.Json(after)));
            return (await ReadAsync(t.Connection, t.Transaction, buildingTypeId, forUpdate: false, t.Token))!;
        });

    /// <summary>
    /// A person states how many identical buildings or units there are. Entering it is confirming it. The
    /// source says whether it is the user's own count or read from the architectural building list (which
    /// needs its evidence, for example the sheet and row).
    /// </summary>
    public Task<BuildingTypeRecord> SetMultiplierAsync(long buildingTypeId, int multiplier, MultiplierSource source, string? evidence,
        EditToken editToken, AuditContext context, CancellationToken cancellationToken = default) =>
        InTypeAsync(buildingTypeId, context, cancellationToken, editToken, JobCapability.Edit, async (t, current) =>
        {
            if (context.ActorType != ActorType.User)
                throw new JobRuleViolationException("Only a person can confirm a multiplier. A detector may suggest one.");
            if (source == MultiplierSource.Suggested)
                throw new JobRuleViolationException("A confirmed multiplier is either the user's own count or from the architectural list, not a suggestion.");
            if (multiplier < 0)
                throw new JobRuleViolationException("A multiplier cannot be negative. Use zero for a type that has no buildings.");
            if (source == MultiplierSource.ArchitecturalList && string.IsNullOrWhiteSpace(evidence))
                throw new JobRuleViolationException("A multiplier taken from the architectural list needs its evidence, for example the sheet and the row.");
            t.RequireEditable();

            var trimmedEvidence = string.IsNullOrWhiteSpace(evidence) ? null : evidence.Trim();
            if (current is { Confirmed: true } && current.Multiplier == multiplier && current.MultiplierSource == source && current.MultiplierEvidence == trimmedEvidence)
                return current;

            await JobRepository.ExecAsync(t.Connection, t.Transaction,
                """
                UPDATE ductwork.BuildingType
                SET Multiplier = @m, MultiplierSource = @s, MultiplierEvidence = @e,
                    Confirmed = 1, ConfirmedBy = @by, ConfirmedUtc = SYSUTCDATETIME()
                WHERE BuildingTypeId = @id;
                """, t.Token, ("@m", multiplier), ("@s", source.ToString()), ("@e", trimmedEvidence), ("@by", context.ActorEmail!), ("@id", buildingTypeId));
            await t.AuditAtCurrentVersionAsync(new AuditEntry("setup", "building-type.multiplier-confirmed",
                $"Set the multiplier for \"{current.Name}\" to {multiplier} ({source}).", "BuildingType", current.PublicId.ToString("D"),
                DescribeMultiplier(current), JobRepository.Json(new Dictionary<string, object?>
                    { ["multiplier"] = multiplier, ["source"] = source.ToString(), ["evidence"] = trimmedEvidence, ["confirmed"] = true })));
            return (await ReadAsync(t.Connection, t.Transaction, buildingTypeId, forUpdate: false, t.Token))!;
        });

    /// <summary>Forgets the multiplier, so the type is unknown again and counts that depend on it show as to be determined.</summary>
    public Task<BuildingTypeRecord> ClearMultiplierAsync(long buildingTypeId, EditToken editToken, AuditContext context,
        CancellationToken cancellationToken = default) =>
        InTypeAsync(buildingTypeId, context, cancellationToken, editToken, JobCapability.Edit, async (t, current) =>
        {
            t.RequireEditable();
            if (current.Multiplier is null) return current;

            await JobRepository.ExecAsync(t.Connection, t.Transaction,
                "UPDATE ductwork.BuildingType SET Multiplier = NULL, MultiplierSource = NULL, MultiplierEvidence = NULL, Confirmed = 0, ConfirmedBy = NULL, ConfirmedUtc = NULL WHERE BuildingTypeId = @id;",
                t.Token, ("@id", buildingTypeId));
            await t.AuditAtCurrentVersionAsync(new AuditEntry("setup", "building-type.multiplier-cleared", $"Cleared the multiplier for \"{current.Name}\".",
                "BuildingType", current.PublicId.ToString("D"), DescribeMultiplier(current), null));
            return (await ReadAsync(t.Connection, t.Transaction, buildingTypeId, forUpdate: false, t.Token))!;
        });

    public Task<bool> DeleteAsync(long buildingTypeId, EditToken editToken, AuditContext context, CancellationToken cancellationToken = default) =>
        InTypeAsync(buildingTypeId, context, cancellationToken, editToken, JobCapability.Edit, async (t, current) =>
        {
            t.RequireEditable();
            await using var count = t.Connection.CreateCommand();
            count.Transaction = t.Transaction;
            count.CommandText = "SELECT COUNT(*) FROM ductwork.Entity WHERE BuildingTypeId = @id;";
            count.Parameters.AddWithValue("@id", buildingTypeId);
            var items = (int)(await count.ExecuteScalarAsync(t.Token))!;
            if (items > 0)
                throw new JobRuleViolationException($"\"{current.Name}\" has {items} counted item(s). Move or remove them before deleting the building type.");

            await JobRepository.ExecAsync(t.Connection, t.Transaction, "DELETE FROM ductwork.BuildingType WHERE BuildingTypeId = @id;", t.Token, ("@id", buildingTypeId));
            await t.AuditAtCurrentVersionAsync(new AuditEntry("setup", "building-type.deleted", $"Deleted the building type \"{current.Name}\".", "BuildingType",
                current.PublicId.ToString("D"), Describe(current), null));
            return true;
        });

    // ------------------------------------------------------------------ suggestions (a detector, a worker, the system)

    /// <summary>
    /// A detector or the engine proposes a multiplier, with its evidence. It is recorded as a suggestion and
    /// is never confirmed by this call. A confirmed value is never overwritten by a suggestion: that returns
    /// false and changes nothing. A person confirms by calling <see cref="SetMultiplierAsync"/>.
    /// </summary>
    public Task<bool> SuggestMultiplierAsync(long buildingTypeId, int multiplier, string evidence, AuditContext context,
        CancellationToken cancellationToken = default) =>
        InTypeAsync(buildingTypeId, context, cancellationToken, null, null, async (t, current) =>
        {
            if (context.ActorType == ActorType.User)
                throw new JobRuleViolationException("A person sets a multiplier outright. Suggestions come from detectors and the system.");
            if (multiplier < 0) throw new JobRuleViolationException("A suggested multiplier cannot be negative.");
            if (string.IsNullOrWhiteSpace(evidence)) throw new JobRuleViolationException("A suggested multiplier needs its evidence.");
            t.RequireEditable();

            if (current.Confirmed) return false;
            if (current.Multiplier == multiplier && current.MultiplierSource == MultiplierSource.Suggested) return false;

            await JobRepository.ExecAsync(t.Connection, t.Transaction,
                "UPDATE ductwork.BuildingType SET Multiplier = @m, MultiplierSource = 'Suggested', MultiplierEvidence = @e, Confirmed = 0 WHERE BuildingTypeId = @id;",
                t.Token, ("@m", multiplier), ("@e", evidence.Trim()), ("@id", buildingTypeId));
            await t.AuditAtCurrentVersionAsync(new AuditEntry("setup", "building-type.multiplier-suggested",
                $"Suggested a multiplier of {multiplier} for \"{current.Name}\" (not confirmed).", "BuildingType", current.PublicId.ToString("D"),
                DescribeMultiplier(current), JobRepository.Json(new Dictionary<string, object?>
                    { ["multiplier"] = multiplier, ["source"] = "Suggested", ["evidence"] = evidence.Trim(), ["confirmed"] = false })));
            return true;
        });

    // ------------------------------------------------------------------ internals shared with result ingestion

    /// <summary>The name as shown (trimmed, single spaces) and as compared (lower-case).</summary>
    public static (string Display, string Normalized) Normalize(string name)
    {
        var display = string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (display.Length == 0) throw new JobRuleViolationException("A building type needs a name.");
        if (display.Length > 100) throw new JobRuleViolationException("A building type name can be at most 100 characters.");
        return (display, display.ToLowerInvariant());
    }

    internal static async Task<BuildingTypeRecord> InsertAsync(JobTxn t, string display, string normalized, BuildingKind kind, string? notes, string createdBy)
    {
        await using var insert = t.Connection.CreateCommand();
        insert.Transaction = t.Transaction;
        insert.CommandText =
            """
            INSERT INTO ductwork.BuildingType (JobId, Name, NameNormalized, Kind, Notes, CreatedBy)
            VALUES (@j, @n, @norm, @k, @notes, @by);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);
            """;
        insert.Parameters.AddWithValue("@j", t.Job.JobId);
        insert.Parameters.AddWithValue("@n", display);
        insert.Parameters.AddWithValue("@norm", normalized);
        insert.Parameters.AddWithValue("@k", kind.ToString());
        insert.Parameters.AddWithValue("@notes", string.IsNullOrWhiteSpace(notes) ? DBNull.Value : notes.Trim());
        insert.Parameters.AddWithValue("@by", createdBy);
        var id = (long)(await insert.ExecuteScalarAsync(t.Token))!;
        return (await ReadAsync(t.Connection, t.Transaction, id, forUpdate: false, t.Token))!;
    }

    internal static async Task<BuildingTypeRecord?> FindByNameAsync(SqlConnection c, SqlTransaction? tx, long jobId, string normalized, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT {Columns} FROM ductwork.BuildingType WHERE JobId = @j AND NameNormalized = @n;";
        command.Parameters.AddWithValue("@j", jobId);
        command.Parameters.AddWithValue("@n", normalized);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    internal static async Task<IReadOnlyList<BuildingTypeRecord>> ListAsync(SqlConnection c, SqlTransaction? tx, long jobId, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT {Columns} FROM ductwork.BuildingType WHERE JobId = @j ORDER BY NameNormalized;";
        command.Parameters.AddWithValue("@j", jobId);
        var list = new List<BuildingTypeRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(Map(reader));
        return list;
    }

    private async Task<T> InTypeAsync<T>(long buildingTypeId, AuditContext context, CancellationToken cancellationToken, EditToken? editToken,
        JobCapability? requires, Func<JobTxn, BuildingTypeRecord, Task<T>> body)
    {
        var existing = await GetAsync(buildingTypeId, cancellationToken) ?? throw new KeyNotFoundException($"Building type {buildingTypeId} does not exist.");
        return await JobTxn.RunAsync(_open, existing.JobId, context, cancellationToken, async t =>
        {
            var current = await ReadAsync(t.Connection, t.Transaction, buildingTypeId, forUpdate: true, t.Token)
                ?? throw new KeyNotFoundException($"Building type {buildingTypeId} no longer exists.");
            return await body(t, current);
        }, editToken, requires: requires);
    }

    private static string Describe(BuildingTypeRecord r) => JobRepository.Json(new Dictionary<string, object?>
        { ["name"] = r.Name, ["kind"] = r.Kind.ToString(), ["multiplier"] = r.Multiplier, ["source"] = r.MultiplierSource?.ToString(), ["confirmed"] = r.Confirmed });

    private static string DescribeMultiplier(BuildingTypeRecord r) => JobRepository.Json(new Dictionary<string, object?>
        { ["multiplier"] = r.Multiplier, ["source"] = r.MultiplierSource?.ToString(), ["evidence"] = r.MultiplierEvidence, ["confirmed"] = r.Confirmed });

    private const string Columns =
        "BuildingTypeId, PublicId, JobId, Name, Kind, Multiplier, MultiplierSource, MultiplierEvidence, Confirmed, ConfirmedBy, ConfirmedUtc, Notes, CreatedUtc, CreatedBy";

    internal static async Task<BuildingTypeRecord?> ReadAsync(SqlConnection c, SqlTransaction? tx, long id, bool forUpdate, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT {Columns} FROM ductwork.BuildingType {(forUpdate ? "WITH (UPDLOCK, ROWLOCK)" : "")} WHERE BuildingTypeId = @id;";
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    private static BuildingTypeRecord Map(SqlDataReader r) => new()
    {
        BuildingTypeId = r.GetInt64(0),
        PublicId = r.GetGuid(1),
        JobId = r.GetInt64(2),
        Name = r.GetString(3),
        Kind = Enum.Parse<BuildingKind>(r.GetString(4)),
        Multiplier = r.IsDBNull(5) ? null : r.GetInt32(5),
        MultiplierSource = r.IsDBNull(6) ? null : Enum.Parse<MultiplierSource>(r.GetString(6)),
        MultiplierEvidence = r.IsDBNull(7) ? null : r.GetString(7),
        Confirmed = r.GetBoolean(8),
        ConfirmedBy = r.IsDBNull(9) ? null : r.GetString(9),
        ConfirmedUtc = r.IsDBNull(10) ? null : DateTime.SpecifyKind(r.GetDateTime(10), DateTimeKind.Utc),
        Notes = r.IsDBNull(11) ? null : r.GetString(11),
        CreatedUtc = DateTime.SpecifyKind(r.GetDateTime(12), DateTimeKind.Utc),
        CreatedBy = r.GetString(13)
    };
}
