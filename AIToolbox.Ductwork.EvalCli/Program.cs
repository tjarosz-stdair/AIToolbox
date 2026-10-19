using System.Text.Json;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Evaluation;
using AIToolbox.Ductwork.Core.Llm;
using AIToolbox.Ductwork.Core.Rules;

// Scores detector output against gold labels and prints a Markdown report.
//
//   evalcli score --gold gold.json --pred a.json [--pred b.json ...] [--pred-dir dir]
//                 [--mode count|box] [--rules rules.json] [--title "..."] [--note "..."]
//                 [--md report.md] [--json report.json]
//
//   Database commands (connection from --connection or DUCTWORK_SQL):
//   evalcli migrate --yes
//   evalcli gold-import --name N --role Development|Holdout|Regression --file gold.json [--notes T]
//   evalcli record --gold N --task-key K --family F --pred manifest.json [--pred-dir dir] [--mode count|box] [--notes T]
//   evalcli runs
//
// Detector output must be a TaskResultManifest. It is validated with the same schema check and the
// same semantic rules the web app applies on ingestion, so a detector that would be refused there is
// refused here too.

return args.Length > 0 && AIToolbox.Ductwork.EvalCli.DbCommands.Names.Contains(args[0])
    ? await AIToolbox.Ductwork.EvalCli.DbCommands.RunAsync(args)
    : Run(args);

static int Run(string[] args)
{
    if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
    {
        Console.WriteLine("usage: evalcli score --gold gold.json --pred manifest.json [--pred ...] [--pred-dir dir] [--mode count|box] [--rules rules.json] [--title T] [--note N] [--md out.md] [--json out.json]");
        return args.Length == 0 ? 1 : 0;
    }

    if (args[0] != "score")
    {
        Console.Error.WriteLine($"Unknown command '{args[0]}'.");
        return 1;
    }

    var options = ParseOptions(args.Skip(1).ToArray());
    if (options is null) return 1;

    var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    IReadOnlyList<GoldItem> gold;
    try
    {
        gold = JsonSerializer.Deserialize<List<GoldItem>>(File.ReadAllText(options.Gold!), web) ?? [];
    }
    catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Could not read the gold file: {ex.Message}");
        return 1;
    }

    var detections = new List<DetectionProposal>();
    var validator = StructuredOutput.ValidatorFor(typeof(TaskResultManifest));
    var failed = false;
    foreach (var path in options.Predictions)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"{path}: could not be read: {ex.Message}");
            failed = true;
            continue;
        }

        var parsed = StructuredOutput.Parse<TaskResultManifest>(text, validator);
        if (!parsed.IsValid)
        {
            Console.Error.WriteLine($"{path}: not a valid manifest:");
            foreach (var e in parsed.Errors.Take(10)) Console.Error.WriteLine("  " + e);
            failed = true;
            continue;
        }

        var semantic = ManifestRules.Validate(parsed.Value!);
        if (semantic.Count > 0)
        {
            Console.Error.WriteLine($"{path}: refused by the manifest rules:");
            foreach (var e in semantic.Take(10)) Console.Error.WriteLine("  " + e);
            failed = true;
            continue;
        }

        detections.AddRange(parsed.Value!.Detections);
    }

    if (failed) return 2;

    var applications = new List<RuleApplication>();
    if (options.Rules is not null)
    {
        List<InterpretationRule> rules;
        try
        {
            rules = JsonSerializer.Deserialize<List<InterpretationRule>>(File.ReadAllText(options.Rules), web) ?? [];
            InterpretationRuleEngine.Validate(rules);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"Could not use the rules file: {ex.Message}");
            return 1;
        }

        var applied = InterpretationRuleEngine.Apply(detections, rules);
        detections = applied.Detections.ToList();
        applications.AddRange(applied.Applications);
    }

    var outcome = EvalHarness.Evaluate(gold, detections, options.Mode);

    var note = options.Note;
    if (applications.Count > 0)
    {
        var summary = string.Join(", ", applications.GroupBy(a => a.RuleId).Select(g => $"{g.Key} x{g.Count()}"));
        note = (note is null ? "" : note + " ") + $"Interpretation rules applied: {summary}.";
    }

    var markdown = ReportFormatter.Markdown(options.Title, outcome.Score, outcome.Calibration, note: note);
    Console.WriteLine(markdown);

    if (options.MarkdownOut is not null) File.WriteAllText(options.MarkdownOut, markdown);
    if (options.JsonOut is not null)
    {
        var report = new
        {
            options.Title,
            Mode = options.Mode.ToString(),
            outcome.Score.Overall,
            outcome.Score.ByBuildingType,
            outcome.Score.ByType,
            outcome.Score.ByKey,
            Calibration = outcome.Calibration.Bins,
            outcome.Calibration.ExpectedCalibrationError,
            RuleApplications = applications
        };
        File.WriteAllText(options.JsonOut, JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    return 0;
}

static Options? ParseOptions(string[] args)
{
    var o = new Options();
    for (var i = 0; i < args.Length; i++)
    {
        string Next()
        {
            if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} needs a value.");
            return args[++i];
        }

        try
        {
            switch (args[i])
            {
                case "--gold": o.Gold = Next(); break;
                case "--pred": o.Predictions.Add(Next()); break;
                case "--pred-dir": o.Predictions.AddRange(Directory.GetFiles(Next(), "*.json").OrderBy(p => p, StringComparer.Ordinal)); break;
                case "--mode": o.Mode = Enum.Parse<MatchMode>(Next(), ignoreCase: true); break;
                case "--rules": o.Rules = Next(); break;
                case "--title": o.Title = Next(); break;
                case "--note": o.Note = Next(); break;
                case "--md": o.MarkdownOut = Next(); break;
                case "--json": o.JsonOut = Next(); break;
                default: throw new ArgumentException($"Unknown option {args[i]}.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException)
        {
            Console.Error.WriteLine(ex.Message);
            return null;
        }
    }

    if (o.Gold is null || o.Predictions.Count == 0)
    {
        Console.Error.WriteLine("--gold and at least one --pred (or --pred-dir) are required.");
        return null;
    }

    return o;
}

internal sealed class Options
{
    public string? Gold { get; set; }
    public List<string> Predictions { get; } = [];
    public MatchMode Mode { get; set; } = MatchMode.Count;
    public string? Rules { get; set; }
    public string Title { get; set; } = "Evaluation";
    public string? Note { get; set; }
    public string? MarkdownOut { get; set; }
    public string? JsonOut { get; set; }
}
