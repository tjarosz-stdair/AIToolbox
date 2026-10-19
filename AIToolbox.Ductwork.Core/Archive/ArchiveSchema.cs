using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Archive;

/// <summary>A column that must point at a row that exists, and where that row is looked up.</summary>
internal sealed record TableRef(string Column, string ParentTable);

/// <summary>
/// One table in an archive. <see cref="Where"/> picks the job's rows (parameter @j). <see cref="Key"/> is the primary key, in
/// the order rows are written, which makes the export canonical: the same data always produces the same bytes.
/// </summary>
internal sealed record TableSpec(string Name, string Key, string Where, string OrderBy, IReadOnlyList<TableRef> Refs, string? SelfRef = null)
{
    public string Qualified => $"ductwork.{Name}";
    public string Select => $"SELECT * FROM {Qualified} WHERE {Where} ORDER BY {OrderBy}";
}

/// <summary>
/// What an archive holds (format version 1). The format is versioned apart from the database schema and changes only additively:
/// a later version may add tables and columns, and every reader keeps reading this one.
///
/// <b>Detail</b> tables are the job's heavy rows. They are written, purged and restored. <b>Reference</b> tables stay in SQL when a
/// job is purged; they are written so the zip describes the whole job on its own, and used to check that detail rows point at
/// something real. Restore never inserts them.
/// </summary>
internal static class ArchiveSchema
{
    public const int FormatVersion = 1;

    private const string ByJob = "JobId = @j";

    private static TableSpec D(string name, string key, IReadOnlyList<TableRef>? refs = null, string? self = null) =>
        new(name, key, ByJob, key, refs ?? [], self);

    /// <summary>Restore order: a table comes after every table it points at. Purge deletes in the reverse order.</summary>
    public static readonly IReadOnlyList<TableSpec> Detail =
    [
        D("DrawingSetRevision", "RevisionId", [new("SupersedesRevisionId", "DrawingSetRevision")], self: "SupersedesRevisionId"),
        D("SourceFile", "SourceFileId", [new("RevisionId", "DrawingSetRevision"), new("DuplicateOfSourceFileId", "SourceFile")], self: "DuplicateOfSourceFileId"),
        D("UploadSession", "UploadSessionId", [new("SourceFileId", "SourceFile")]),
        D("Sheet", "SheetId", [new("RevisionId", "DrawingSetRevision"), new("SourceFileId", "SourceFile"), new("DuplicateOfSheetId", "Sheet"), new("ProcessingTaskId", "ProcessingTask")], self: "DuplicateOfSheetId"),
        D("SheetView", "SheetViewId", [new("SheetId", "Sheet"), new("BuildingTypeId", "BuildingType")]),
        D("SheetRegion", "SheetRegionId", [new("SheetId", "Sheet")]),
        D("Entity", "EntityId", [new("BuildingTypeId", "BuildingType"), new("ProducerId", "Producer"), new("ProcessingTaskId", "ProcessingTask"), new("SheetViewId", "SheetView"), new("ResolvedByRuleId", "InterpretationRule")]),
        D("Finding", "FindingId", [new("ProcessingTaskId", "ProcessingTask"), new("ProducerId", "Producer"), new("EntityId", "Entity"), new("ResolvedRuleId", "InterpretationRule")]),
        D("EntityRevision", "EntityRevisionId", [new("EntityId", "Entity")]),
        D("RuleApplication", "RuleApplicationId", [new("RuleId", "InterpretationRule"), new("EntityId", "Entity")])
    ];

    /// <summary>Written for completeness and for checking references; stays in SQL after a purge.</summary>
    public static readonly IReadOnlyList<TableSpec> Reference =
    [
        new("Job", "JobId", ByJob, "JobId", []),
        new("JobMember", "UserEmail", ByJob, "UserEmail", []),
        new("JobWorkflowStep", "Step", ByJob, "Step", []),
        new("JobTag", "TagId", ByJob, "TagId", []),
        new("BuildingType", "BuildingTypeId", ByJob, "BuildingTypeId", []),
        new("ProcessingTask", "ProcessingTaskId", ByJob, "ProcessingTaskId", []),
        // The job's own rules, the rules it wrote, and any other rule that changed one of its items.
        new("InterpretationRule", "RuleId",
            "JobId = @j OR OriginJobId = @j OR RuleId IN (SELECT ResolvedByRuleId FROM ductwork.Entity WHERE JobId = @j AND ResolvedByRuleId IS NOT NULL) " +
            "OR RuleId IN (SELECT RuleId FROM ductwork.RuleApplication WHERE JobId = @j) OR RuleId IN (SELECT ResolvedRuleId FROM ductwork.Finding WHERE JobId = @j AND ResolvedRuleId IS NOT NULL)",
            "RuleId", []),
        new("VisionApproval", "VisionApprovalId", ByJob, "VisionApprovalId", []),
        new("VisionTransmission", "VisionTransmissionId", ByJob, "VisionTransmissionId", []),
        new("ActivitySession", "SessionId", ByJob, "SessionId", []),
        new("JobAuditEvent", "EventId", ByJob, "EventId", [])
    ];

    /// <summary>Rows of the shared, immutable records the job points at, copied in so the zip stands on its own.</summary>
    public static readonly IReadOnlyList<TableSpec> Provenance =
    [
        new("Producer", "ProducerId",
            "ProducerId IN (SELECT ProducerId FROM ductwork.Entity WHERE JobId = @j) OR ProducerId IN (SELECT ProducerId FROM ductwork.Finding WHERE JobId = @j) " +
            "OR ProducerId IN (SELECT ProducerId FROM ductwork.ProcessingTask WHERE JobId = @j)", "ProducerId", []),
        new("ProviderSnapshot", "ProviderSnapshotId",
            "ProviderSnapshotId IN (SELECT p.ProviderSnapshotId FROM ductwork.Producer p WHERE p.ProviderSnapshotId IS NOT NULL AND (p.ProducerId IN (SELECT ProducerId FROM ductwork.Entity WHERE JobId = @j) " +
            "OR p.ProducerId IN (SELECT ProducerId FROM ductwork.Finding WHERE JobId = @j) OR p.ProducerId IN (SELECT ProducerId FROM ductwork.ProcessingTask WHERE JobId = @j))) " +
            "OR ProviderSnapshotId IN (SELECT ProviderSnapshotId FROM ductwork.VisionApproval WHERE JobId = @j)", "ProviderSnapshotId", [])
    ];

    public static IEnumerable<TableSpec> AllTables => Detail.Concat(Reference).Concat(Provenance);

    public static TableSpec Find(string name) => AllTables.First(t => t.Name == name);

    public static string KeyOf(string table) => Find(table).Key;

    public static string EntryPath(TableSpec table) =>
        Detail.Contains(table) ? $"data/{table.Name}.jsonl" : Reference.Contains(table) ? $"reference/{table.Name}.jsonl" : $"provenance/{table.Name}.jsonl";
}

/// <summary>Turns database rows into canonical JSON lines and back, driven by the live table's own column types.</summary>
internal static class RowJson
{
    public static void WriteRow(Utf8JsonWriter writer, SqlDataReader reader)
    {
        writer.WriteStartObject();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            writer.WritePropertyName(reader.GetName(i));
            if (reader.IsDBNull(i)) { writer.WriteNullValue(); continue; }

            switch (reader.GetValue(i))
            {
                case bool b: writer.WriteBooleanValue(b); break;
                case byte n: writer.WriteNumberValue(n); break;
                case short n: writer.WriteNumberValue(n); break;
                case int n: writer.WriteNumberValue(n); break;
                case long n: writer.WriteNumberValue(n); break;
                case double d: writer.WriteNumberValue(d); break;
                case float f: writer.WriteNumberValue(f); break;
                case decimal m: writer.WriteStringValue(m.ToString(CultureInfo.InvariantCulture)); break;
                case DateTime t: writer.WriteStringValue(t.ToString("O", CultureInfo.InvariantCulture)); break;
                case DateTimeOffset o: writer.WriteStringValue(o.ToString("O", CultureInfo.InvariantCulture)); break;
                case Guid g: writer.WriteStringValue(g.ToString("D")); break;
                case byte[] bytes: writer.WriteStringValue(Convert.ToBase64String(bytes)); break;
                case string s: writer.WriteStringValue(s); break;
                default: writer.WriteStringValue(Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)); break;
            }
        }

        writer.WriteEndObject();
    }

    /// <summary>Reads a value back as the type its column has. Anything that does not fit is an error, never a guess.</summary>
    public static object? ReadValue(JsonElement value, Type target)
    {
        if (value.ValueKind == JsonValueKind.Null) return DBNull.Value;
        target = Nullable.GetUnderlyingType(target) ?? target;

        if (target == typeof(long)) return value.GetInt64();
        if (target == typeof(int)) return value.GetInt32();
        if (target == typeof(short)) return value.GetInt16();
        if (target == typeof(byte)) return value.GetByte();
        if (target == typeof(bool)) return value.GetBoolean();
        if (target == typeof(double)) return value.GetDouble();
        if (target == typeof(float)) return value.GetSingle();
        if (target == typeof(decimal)) return decimal.Parse(value.GetString()!, CultureInfo.InvariantCulture);
        if (target == typeof(DateTime)) return DateTime.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (target == typeof(Guid)) return Guid.Parse(value.GetString()!);
        if (target == typeof(byte[])) return Convert.FromBase64String(value.GetString()!);
        if (target == typeof(string)) return value.GetString();
        throw new InvalidOperationException($"The archive has a value for a column of type {target.Name}, which it does not know how to read.");
    }

    /// <summary>The key of a row as text, for checking that references resolve.</summary>
    public static string KeyText(JsonElement row, string keyColumn) =>
        row.TryGetProperty(keyColumn, out var v) ? v.ValueKind == JsonValueKind.Null ? "" : v.ToString() : "";
}
