using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Data;
using AIToolbox.Ductwork.Core.Evaluation;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.EvalCli;

/// <summary>
/// The commands that touch a database: apply the schema, store a gold set, record an evaluation run, and show what has been measured.
/// They are the step between "a detector exists" and "an administrator can switch it on". The connection string comes from
/// <c>--connection</c> or the <c>DUCTWORK_SQL</c> environment variable and is never printed.
/// </summary>
internal static class DbCommands
{
    public static readonly string[] Names = ["migrate", "gold-import", "record", "runs"];

    public static async Task<int> RunAsync(string[] args)
    {
        var o = Parse(args);
        if (o is null) return 1;

        var connectionString = o.Connection ?? Environment.GetEnvironmentVariable("DUCTWORK_SQL");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("Give the database with --connection or the DUCTWORK_SQL environment variable.");
            return 1;
        }

        try
        {
            switch (args[0])
            {
                case "migrate": return await MigrateAsync(connectionString, o);
                case "gold-import": return await GoldImportAsync(connectionString, o);
                case "record": return await RecordAsync(connectionString, o);
                default: return await RunsAsync(connectionString);
            }
        }
        catch (EvalRecordingException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (SqlException ex)
        {
            Console.Error.WriteLine($"The database refused that: {ex.Message}");
            return 3;
        }
    }

    private static async Task<int> MigrateAsync(string connectionString, DbOptions o)
    {
        if (!o.Yes)
        {
            Console.Error.WriteLine("This changes the database it is pointed at (it creates or alters the ductwork schema). Say so with --yes.");
            return 1;
        }

        var result = await new MigrationRunner().MigrateAsync(connectionString);
        Console.WriteLine($"Applied: {(result.Applied.Count == 0 ? "nothing new" : string.Join(", ", result.Applied))}. Already applied: {result.AlreadyApplied.Count}.");
        return 0;
    }

    private static async Task<int> GoldImportAsync(string connectionString, DbOptions o)
    {
        if (o.Name is null || o.File is null || o.Role is null)
        {
            Console.Error.WriteLine("gold-import needs --name, --role (Development, Holdout or Regression) and --file.");
            return 1;
        }

        var items = EvalRecorder.ReadGold(await File.ReadAllTextAsync(o.File));
        await using var connection = await OpenAsync(connectionString);
        var id = await EvalRecorder.ImportGoldAsync(connection, o.Name, o.Role.Value, items, o.Notes);
        Console.WriteLine($"Stored gold set “{o.Name}” (id {id}, {o.Role}) with {items.Count} item(s).");
        return 0;
    }

    private static async Task<int> RecordAsync(string connectionString, DbOptions o)
    {
        if (o.Name is null || o.TaskKey is null || o.Predictions.Count == 0)
        {
            Console.Error.WriteLine("record needs --gold NAME, --task-key KEY and at least one --pred (or --pred-dir).");
            return 1;
        }

        var files = o.Predictions.Select(p => (p, File.ReadAllText(p))).ToList();
        var (manifests, producer) = EvalRecorder.ReadManifests(files);
        await using var connection = await OpenAsync(connectionString);
        var goldId = await EvalRecorder.FindGoldAsync(connection, o.Name) ?? throw new EvalRecordingException($"There is no gold set named “{o.Name}”. Import it first with gold-import.");
        var run = await EvalRecorder.RecordAsync(connection, goldId, manifests, o.TaskKey, o.Family, o.Mode, o.Notes);

        Console.WriteLine($"Recorded evaluation run {run.EvalRunId} of {producer.AlgorithmName ?? producer.Kind.ToString()} {producer.AlgorithmVersion} on “{o.Name}” ({run.Predictions} prediction(s)).");
        Console.WriteLine($"  precision {Pct(run.Overall.Precision)}, recall {Pct(run.Overall.Recall)}  (true {run.Overall.TruePositives:0.##}, false {run.Overall.FalsePositives:0.##}, missed {run.Overall.FalseNegatives:0.##})");
        Console.WriteLine($"  An administrator can now enable a task type for this producer, citing run {run.EvalRunId}.");
        return 0;
    }

    private static async Task<int> RunsAsync(string connectionString)
    {
        await using var connection = await OpenAsync(connectionString);
        Console.WriteLine("Gold sets:");
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT g.GoldSetId, g.Name, g.Role, g.SpentUtc, (SELECT COUNT(*) FROM ductwork.GoldLabel l WHERE l.GoldSetId = g.GoldSetId), g.Notes FROM ductwork.GoldSet g ORDER BY g.GoldSetId;";
            await using var r = await command.ExecuteReaderAsync();
            var any = false;
            while (await r.ReadAsync())
            {
                any = true;
                Console.WriteLine($"  {r.GetInt32(0),3}  {r.GetString(1)}  [{r.GetString(2)}{(r.IsDBNull(3) ? "" : ", spent")}]  {r.GetInt32(4)} item(s){(r.IsDBNull(5) ? "" : "  — " + r.GetString(5))}");
            }

            if (!any) Console.WriteLine("  none");
        }

        Console.WriteLine("Evaluation runs:");
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT r.EvalRunId, r.TaskKey, g.Name, p.AlgorithmName, p.AlgorithmVersion, p.Kind, r.StartedUtc,
                       (SELECT TruePositives FROM ductwork.EvalResult x WHERE x.EvalRunId = r.EvalRunId AND x.BuildingType IS NULL AND x.Type IS NULL),
                       (SELECT PrecisionRatio FROM ductwork.EvalResult x WHERE x.EvalRunId = r.EvalRunId AND x.BuildingType IS NULL AND x.Type IS NULL),
                       (SELECT RecallRatio FROM ductwork.EvalResult x WHERE x.EvalRunId = r.EvalRunId AND x.BuildingType IS NULL AND x.Type IS NULL),
                       (SELECT COUNT(*) FROM ductwork.TaskBinding b WHERE b.EvalRunId = r.EvalRunId AND b.RetiredUtc IS NULL)
                FROM ductwork.EvalRun r JOIN ductwork.GoldSet g ON g.GoldSetId = r.GoldSetId JOIN ductwork.Producer p ON p.ProducerId = r.ProducerId
                ORDER BY r.EvalRunId;
                """;
            await using var r = await command.ExecuteReaderAsync();
            var any = false;
            while (await r.ReadAsync())
            {
                any = true;
                var who = r.IsDBNull(3) ? r.GetString(5) : $"{r.GetString(3)} {r.GetString(4)}";
                Console.WriteLine($"  {r.GetInt32(0),3}  {r.GetString(1)}  {who}  on “{r.GetString(2)}”  precision {Pct(r.IsDBNull(8) ? null : r.GetDouble(8))}, recall {Pct(r.IsDBNull(9) ? null : r.GetDouble(9))}  {(r.GetInt32(10) > 0 ? "(enabled)" : "")}");
            }

            if (!any) Console.WriteLine("  none");
        }

        return 0;
    }

    private static async Task<SqlConnection> OpenAsync(string connectionString)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static string Pct(double? value) => value is null ? "n/a" : $"{value * 100:0.0}%";

    private static DbOptions? Parse(string[] args)
    {
        var o = new DbOptions();
        for (var i = 1; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            try
            {
                switch (args[i])
                {
                    case "--connection": o.Connection = Next(); break;
                    case "--yes": o.Yes = true; break;
                    case "--name": case "--gold": o.Name = Next(); break;
                    case "--role": o.Role = Enum.Parse<GoldRole>(Next(), ignoreCase: true); break;
                    case "--file": o.File = Next(); break;
                    case "--notes": o.Notes = Next(); break;
                    case "--pred": o.Predictions.Add(Next()); break;
                    case "--pred-dir": o.Predictions.AddRange(Directory.GetFiles(Next(), "*.json").OrderBy(p => p, StringComparer.Ordinal)); break;
                    case "--task-key": o.TaskKey = Next(); break;
                    case "--family": o.Family = Enum.Parse<DetectorFamily>(Next(), ignoreCase: true); break;
                    case "--mode": o.Mode = Enum.Parse<MatchMode>(Next(), ignoreCase: true); break;
                    default: throw new ArgumentException($"Unknown option {args[i]}.");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException)
            {
                Console.Error.WriteLine(ex.Message);
                return null;
            }
        }

        return o;
    }

    private sealed class DbOptions
    {
        public string? Connection { get; set; }
        public bool Yes { get; set; }
        public string? Name { get; set; }
        public GoldRole? Role { get; set; }
        public string? File { get; set; }
        public string? Notes { get; set; }
        public List<string> Predictions { get; } = [];
        public string? TaskKey { get; set; }
        public DetectorFamily Family { get; set; } = DetectorFamily.LabelFlagCount;
        public MatchMode Mode { get; set; } = MatchMode.Count;
    }
}
