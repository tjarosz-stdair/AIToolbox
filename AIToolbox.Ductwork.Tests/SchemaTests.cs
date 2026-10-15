using AIToolbox.Ductwork.Core.Data;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Tests;

public class SchemaTests(SqlFixture db) : IClassFixture<SqlFixture>
{
    private const string Hash = "0123456789012345678901234567890123456789012345678901234567890123";

    // ---- Producer CHECK constraints

    private async Task<int> InsertProducer(string key, string kind, int? directive = null, int? provider = null,
        string? algName = null, string? algVersion = null, string? paramsHash = null, string? user = null) =>
        await db.ExecAsync(
            """
            INSERT INTO ductwork.Producer (ProducerKey, Kind, DirectiveSnapshotId, ProviderSnapshotId, AlgorithmName, AlgorithmVersion, ParamsHash, UserEmail)
            VALUES (@k, @kind, @d, @p, @an, @av, @ph, @u);
            """,
            ("@k", key), ("@kind", kind), ("@d", directive), ("@p", provider),
            ("@an", algName), ("@av", algVersion), ("@ph", paramsHash), ("@u", user));

    private async Task<int> NewProviderSnapshot(string hash)
    {
        await db.ExecAsync(
            "INSERT INTO ductwork.ProviderSnapshot (ProviderType, EndpointHost, ModelName, ContentHash) VALUES (2, 'example.invalid', 'm', @h);",
            ("@h", hash));
        return await db.ScalarAsync<int>("SELECT ProviderSnapshotId FROM ductwork.ProviderSnapshot WHERE ContentHash = @h;", ("@h", hash));
    }

    [Fact]
    public async Task Valid_producers_of_each_kind_insert()
    {
        var snap = await NewProviderSnapshot(Hash.Replace('0', 'a'));
        Assert.Equal(1, await InsertProducer("t-llm", "Llm", directive: 5, provider: snap));
        Assert.Equal(1, await InsertProducer("t-alg", "Algorithm", algName: "engine", algVersion: "1.0", paramsHash: "abc"));
        Assert.Equal(1, await InsertProducer("t-man", "Manual", user: "a@example.com"));
    }

    [Fact]
    public async Task Llm_producer_without_a_snapshot_is_rejected()
    {
        var snap = await NewProviderSnapshot(Hash.Replace('0', 'b'));
        await Assert.ThrowsAsync<SqlException>(() => InsertProducer("t-llm-no-provider", "Llm", directive: 5));
        await Assert.ThrowsAsync<SqlException>(() => InsertProducer("t-llm-no-directive", "Llm", provider: snap));
        await Assert.ThrowsAsync<SqlException>(() => InsertProducer("t-llm-none", "Llm"));
    }

    [Fact]
    public async Task Algorithm_and_manual_producers_with_missing_or_blank_fields_are_rejected()
    {
        await Assert.ThrowsAsync<SqlException>(() => InsertProducer("t-alg-noversion", "Algorithm", algName: "e", paramsHash: "h"));
        await Assert.ThrowsAsync<SqlException>(() => InsertProducer("t-alg-blank", "Algorithm", algName: "  ", algVersion: "1", paramsHash: "h"));
        await Assert.ThrowsAsync<SqlException>(() => InsertProducer("t-man-blank", "Manual", user: " "));
        await Assert.ThrowsAsync<SqlException>(() => InsertProducer("t-man-none", "Manual"));
    }

    [Fact]
    public async Task Mixed_shapes_and_unknown_kinds_are_rejected()
    {
        var snap = await NewProviderSnapshot(Hash.Replace('0', 'c'));
        await Assert.ThrowsAsync<SqlException>(() => InsertProducer("t-mixed-1", "Algorithm", algName: "e", algVersion: "1", paramsHash: "h", provider: snap));
        await Assert.ThrowsAsync<SqlException>(() => InsertProducer("t-mixed-2", "Manual", user: "a@example.com", algName: "e"));
        await Assert.ThrowsAsync<SqlException>(() => InsertProducer("t-kind", "Robot", user: "a@example.com"));
    }

    [Fact]
    public async Task Producer_key_is_unique()
    {
        await InsertProducer("t-dup", "Manual", user: "dup@example.com");
        await Assert.ThrowsAsync<SqlException>(() => InsertProducer("t-dup", "Manual", user: "dup2@example.com"));
    }

    [Fact]
    public async Task Provider_snapshot_is_deduplicated_by_content_hash()
    {
        var hash = Hash.Replace('0', 'd');
        await NewProviderSnapshot(hash);
        await Assert.ThrowsAsync<SqlException>(() => NewProviderSnapshot(hash));
    }

    [Fact]
    public async Task Provider_snapshot_has_no_column_that_could_hold_a_secret()
    {
        var columns = new List<string>();
        await using var connection = await db.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID('ductwork.ProviderSnapshot');";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));

        Assert.DoesNotContain(columns, c =>
            c.Contains("key", StringComparison.OrdinalIgnoreCase) || c.Contains("secret", StringComparison.OrdinalIgnoreCase) || c.Contains("cipher", StringComparison.OrdinalIgnoreCase));
    }

    // ---- Evaluation tables and the holdout trigger

    private async Task<int> NewGoldSet(string name, string role) =>
        await db.ScalarAsync<int>("INSERT INTO ductwork.GoldSet (Name, Role) OUTPUT INSERTED.GoldSetId VALUES (@n, @r);", ("@n", name), ("@r", role));

    private async Task<long> NewProducer(string key) =>
        await db.ScalarAsync<long>(
            "INSERT INTO ductwork.Producer (ProducerKey, Kind, UserEmail) OUTPUT INSERTED.ProducerId VALUES (@k, 'Manual', 'eval@example.com');", ("@k", key));

    private async Task<int> NewEvalRun(int goldSetId, long producerId) =>
        await db.ScalarAsync<int>(
            // No OUTPUT clause: SQL Server refuses it on a table that has an enabled trigger.
            "INSERT INTO ductwork.EvalRun (GoldSetId, ProducerId, TaskKey, MatchMode, ContractVersion) VALUES (@g, @p, 'detect', 'Count', '0.1.0'); SELECT CAST(SCOPE_IDENTITY() AS INT);",
            ("@g", goldSetId), ("@p", producerId));

    [Fact]
    public async Task Development_set_can_be_evaluated_repeatedly()
    {
        var set = await NewGoldSet("dev-" + Guid.NewGuid().ToString("N"), "Development");
        var producer = await NewProducer("eval-dev-" + Guid.NewGuid().ToString("N"));

        await NewEvalRun(set, producer);
        await NewEvalRun(set, producer);

        Assert.Null(await db.ScalarAsync<DateTime?>("SELECT SpentUtc FROM ductwork.GoldSet WHERE GoldSetId = @g;", ("@g", set)));
    }

    [Fact]
    public async Task Holdout_is_spent_by_its_first_run_and_refuses_a_second()
    {
        var set = await NewGoldSet("holdout-" + Guid.NewGuid().ToString("N"), "Holdout");
        var producer = await NewProducer("eval-holdout-" + Guid.NewGuid().ToString("N"));

        var first = await NewEvalRun(set, producer);

        Assert.NotNull(await db.ScalarAsync<DateTime?>("SELECT SpentUtc FROM ductwork.GoldSet WHERE GoldSetId = @g;", ("@g", set)));
        Assert.Equal(first, await db.ScalarAsync<int>("SELECT SpentByEvalRunId FROM ductwork.GoldSet WHERE GoldSetId = @g;", ("@g", set)));

        var ex = await Assert.ThrowsAsync<SqlException>(() => NewEvalRun(set, producer));
        Assert.Contains("holdout", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.EvalRun WHERE GoldSetId = @g;", ("@g", set)));
    }

    [Fact]
    public async Task Only_a_holdout_can_be_marked_spent_and_roles_are_checked()
    {
        var dev = await NewGoldSet("dev2-" + Guid.NewGuid().ToString("N"), "Development");
        await Assert.ThrowsAsync<SqlException>(() =>
            db.ExecAsync("UPDATE ductwork.GoldSet SET SpentUtc = SYSUTCDATETIME() WHERE GoldSetId = @g;", ("@g", dev)));
        await Assert.ThrowsAsync<SqlException>(() => NewGoldSet("bad-role-" + Guid.NewGuid().ToString("N"), "Training"));
    }

    [Fact]
    public async Task Eval_result_computes_precision_and_recall_and_leaves_undefined_as_null()
    {
        var set = await NewGoldSet("res-" + Guid.NewGuid().ToString("N"), "Development");
        var run = await NewEvalRun(set, await NewProducer("eval-res-" + Guid.NewGuid().ToString("N")));

        await db.ExecAsync(
            "INSERT INTO ductwork.EvalResult (EvalRunId, Type, TruePositives, FalsePositives, FalseNegatives) VALUES (@r, 'Boot', 8, 2, 4), (@r, 'Tee', 0, 0, 0);",
            ("@r", run));

        Assert.Equal(0.8, await db.ScalarAsync<double>("SELECT PrecisionRatio FROM ductwork.EvalResult WHERE EvalRunId = @r AND Type = 'Boot';", ("@r", run)), 9);
        Assert.Equal(8.0 / 12, await db.ScalarAsync<double>("SELECT RecallRatio FROM ductwork.EvalResult WHERE EvalRunId = @r AND Type = 'Boot';", ("@r", run)), 9);
        Assert.Null(await db.ScalarAsync<double?>("SELECT PrecisionRatio FROM ductwork.EvalResult WHERE EvalRunId = @r AND Type = 'Tee';", ("@r", run)));
    }

    [Fact]
    public async Task Gold_label_box_must_be_all_or_nothing_and_quantity_non_negative()
    {
        var set = await NewGoldSet("lbl-" + Guid.NewGuid().ToString("N"), "Development");
        await db.ExecAsync("INSERT INTO ductwork.GoldLabel (GoldSetId, Type) VALUES (@g, 'Boot');", ("@g", set));
        await db.ExecAsync("INSERT INTO ductwork.GoldLabel (GoldSetId, Type, BoxX0, BoxY0, BoxX1, BoxY1) VALUES (@g, 'Boot', 0, 0, 1, 1);", ("@g", set));
        await Assert.ThrowsAsync<SqlException>(() =>
            db.ExecAsync("INSERT INTO ductwork.GoldLabel (GoldSetId, Type, BoxX0) VALUES (@g, 'Boot', 0);", ("@g", set)));
        await Assert.ThrowsAsync<SqlException>(() =>
            db.ExecAsync("INSERT INTO ductwork.GoldLabel (GoldSetId, Type, Quantity) VALUES (@g, 'Boot', -1);", ("@g", set)));
    }

    [Fact]
    public async Task Calibration_bins_reject_impossible_counts()
    {
        var set = await NewGoldSet("cal-" + Guid.NewGuid().ToString("N"), "Development");
        var run = await NewEvalRun(set, await NewProducer("eval-cal-" + Guid.NewGuid().ToString("N")));
        var cal = await db.ScalarAsync<int>(
            "INSERT INTO ductwork.CalibrationRun (EvalRunId, Family) OUTPUT INSERTED.CalibrationRunId VALUES (@r, 'LabelFlagCount');", ("@r", run));

        await db.ExecAsync("INSERT INTO ductwork.CalibrationBin VALUES (@c, 0, 0, 0.1, 10, 4.5);", ("@c", cal));
        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync("INSERT INTO ductwork.CalibrationBin VALUES (@c, 1, 0.1, 0.2, 3, 5);", ("@c", cal)));
        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync("INSERT INTO ductwork.CalibrationBin VALUES (@c, 2, 0.5, 0.2, 3, 1);", ("@c", cal)));
    }
}

public class MigrationRunnerTests
{
    private static async Task<(SqlFixture Db, MigrationRunner Runner)> FreshAsync()
    {
        var db = new SqlFixture();
        await db.InitializeAsync();
        return (db, new MigrationRunner());
    }

    [Fact]
    public async Task Second_run_applies_nothing_and_reports_everything_as_already_applied()
    {
        var (db, runner) = await FreshAsync();
        try
        {
            var again = await runner.MigrateAsync(db.ConnectionString);
            Assert.Empty(again.Applied);
            Assert.Equal(runner.Migrations.Select(m => m.Version), again.AlreadyApplied);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task An_applied_script_that_is_later_changed_is_refused()
    {
        var (db, runner) = await FreshAsync();
        try
        {
            var changed = runner.Migrations
                .Select(m => m.Version == 1 ? m with { Sql = m.Sql + "\n-- edited", Checksum = MigrationRunner.Checksum(m.Sql + "\n-- edited") } : m)
                .ToList();

            await Assert.ThrowsAsync<MigrationTamperedException>(() => new MigrationRunner(changed).MigrateAsync(db.ConnectionString));
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Checksum_ignores_line_ending_differences()
    {
        Assert.Equal(MigrationRunner.Checksum("a\r\nb\r\n"), MigrationRunner.Checksum("a\nb\n"));
        Assert.NotEqual(MigrationRunner.Checksum("a\nb\n"), MigrationRunner.Checksum("a\nc\n"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_failing_script_rolls_back_completely_and_records_nothing()
    {
        var (db, runner) = await FreshAsync();
        try
        {
            var bad = new Migration(99, "bad",
                "CREATE TABLE ductwork.ShouldNotSurvive (Id INT);\nGO\nSELECT 1/0;\nGO\n", MigrationRunner.Checksum("bad"));
            var withBad = new MigrationRunner(runner.Migrations.Append(bad).ToList());

            await Assert.ThrowsAsync<SqlException>(() => withBad.MigrateAsync(db.ConnectionString));

            Assert.Null(await db.ScalarAsync<int?>("SELECT OBJECT_ID('ductwork.ShouldNotSurvive');"));
            Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.SchemaVersion WHERE Version = 99;"));
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Concurrent_runners_on_an_empty_database_apply_each_script_exactly_once()
    {
        var db = new SqlFixture();
        // Create the empty database without running the migrations.
        await using (var master = new SqlConnection(@"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true;"))
        {
            await master.OpenAsync();
            await using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{db.DatabaseName}];";
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var runner = new MigrationRunner();
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => runner.MigrateAsync(db.ConnectionString)));

            Assert.Equal(runner.Migrations.Count, results.Sum(r => r.Applied.Count));
            Assert.Equal(runner.Migrations.Count, await db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.SchemaVersion;"));
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public void Batches_split_on_go_lines_only()
    {
        var batches = MigrationRunner.SplitBatches("SELECT 1;\nGO\nSELECT 'GO ahead';\r\n  go  \nSELECT 3;");
        Assert.Equal(["SELECT 1;", "SELECT 'GO ahead';", "SELECT 3;"], batches);
    }

    [Fact]
    public void Embedded_migrations_are_numbered_contiguously_from_one()
    {
        var versions = new MigrationRunner().Migrations.Select(m => m.Version).ToList();
        Assert.Equal(Enumerable.Range(1, versions.Count), versions);
    }
}
