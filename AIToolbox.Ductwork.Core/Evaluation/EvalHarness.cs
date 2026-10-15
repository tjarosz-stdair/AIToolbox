using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Data;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Evaluation;

public enum GoldRole { Development, Holdout, Regression }

/// <summary>Anything that turns a gold set's inputs into detections: the wrapped engine, the residential counter, an LLM task.</summary>
public interface IDetector
{
    ProducerRef Producer { get; }
    DetectorFamily Family { get; }
    string TaskKey { get; }
    Task<IReadOnlyList<DetectionProposal>> DetectAsync(CancellationToken cancellationToken);
}

public sealed record EvalOutcome(ScoreReport Score, CalibrationReport Calibration);

/// <summary>
/// Runs a detector against gold labels and records the result (plan sections 4.5 and 6.13). Scoring
/// and calibration are pure and live in <see cref="Evaluate"/>; <see cref="RunAndSaveAsync"/> adds the
/// persistence, including the producer row, the per-type and per-building-type results, and the
/// calibration bins that review-level cutoffs are later derived from.
/// </summary>
public static class EvalHarness
{
    public static EvalOutcome Evaluate(
        IReadOnlyList<GoldItem> gold,
        IReadOnlyList<DetectionProposal> predicted,
        MatchMode mode,
        double iouThreshold = Scoring.DefaultIouThreshold)
    {
        var score = Scoring.Score(gold, predicted, mode, iouThreshold);
        return new EvalOutcome(score, new CalibrationReport(score.Scored));
    }

    public static async Task<int> CreateGoldSetAsync(
        SqlConnection connection, string name, GoldRole role, IReadOnlyList<GoldItem> items, CancellationToken cancellationToken = default)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var create = connection.CreateCommand();
            create.Transaction = transaction;
            create.CommandText = "INSERT INTO ductwork.GoldSet (Name, Role) VALUES (@n, @r); SELECT CAST(SCOPE_IDENTITY() AS INT);";
            create.Parameters.AddWithValue("@n", name);
            create.Parameters.AddWithValue("@r", role.ToString());
            var id = (int)(await create.ExecuteScalarAsync(cancellationToken))!;

            foreach (var item in items)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO ductwork.GoldLabel (GoldSetId, BuildingType, Type, Category, Size, Quantity, BoxX0, BoxY0, BoxX1, BoxY1)
                    VALUES (@g, @bt, @t, @c, @s, @q, @x0, @y0, @x1, @y1);
                    """;
                insert.Parameters.AddWithValue("@g", id);
                insert.Parameters.AddWithValue("@bt", (object?)item.BuildingType ?? DBNull.Value);
                insert.Parameters.AddWithValue("@t", item.Type);
                insert.Parameters.AddWithValue("@c", (object?)item.Category ?? DBNull.Value);
                insert.Parameters.AddWithValue("@s", (object?)item.Size ?? DBNull.Value);
                insert.Parameters.AddWithValue("@q", item.Quantity);
                insert.Parameters.AddWithValue("@x0", (object?)item.Box?.X0 ?? DBNull.Value);
                insert.Parameters.AddWithValue("@y0", (object?)item.Box?.Y0 ?? DBNull.Value);
                insert.Parameters.AddWithValue("@x1", (object?)item.Box?.X1 ?? DBNull.Value);
                insert.Parameters.AddWithValue("@y1", (object?)item.Box?.Y1 ?? DBNull.Value);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return id;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public static async Task<IReadOnlyList<GoldItem>> LoadGoldAsync(
        SqlConnection connection, int goldSetId, CancellationToken cancellationToken = default)
    {
        var items = new List<GoldItem>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT BuildingType, Type, Category, Size, Quantity, BoxX0, BoxY0, BoxX1, BoxY1 FROM ductwork.GoldLabel WHERE GoldSetId = @g ORDER BY GoldLabelId;";
        command.Parameters.AddWithValue("@g", goldSetId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new GoldItem
            {
                BuildingType = reader.IsDBNull(0) ? null : reader.GetString(0),
                Type = reader.GetString(1),
                Category = reader.IsDBNull(2) ? null : reader.GetString(2),
                Size = reader.IsDBNull(3) ? null : reader.GetString(3),
                Quantity = reader.GetDouble(4),
                Box = reader.IsDBNull(5) ? null : new BoundingBox(reader.GetDouble(5), reader.GetDouble(6), reader.GetDouble(7), reader.GetDouble(8))
            });
        }

        return items;
    }

    /// <summary>
    /// Runs the detector, scores it against the stored gold set and records the run. A Holdout gold
    /// set is spent by this call and refused afterwards by the database, so call it only at a gate.
    /// </summary>
    public static async Task<(int EvalRunId, EvalOutcome Outcome)> RunAndSaveAsync(
        SqlConnection connection,
        int goldSetId,
        IDetector detector,
        MatchMode mode,
        CancellationToken cancellationToken = default)
    {
        await EnsureHoldoutAvailableAsync(connection, goldSetId, cancellationToken);
        var gold = await LoadGoldAsync(connection, goldSetId, cancellationToken);
        var predicted = await detector.DetectAsync(cancellationToken);
        var outcome = Evaluate(gold, predicted, mode);

        var producerId = await ProducerStore.EnsureProducerAsync(connection, detector.Producer, cancellationToken);

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            int runId;
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                // No OUTPUT clause: SQL Server refuses it on a table with an enabled trigger (the holdout guard).
                insert.CommandText =
                    """
                    INSERT INTO ductwork.EvalRun (GoldSetId, ProducerId, TaskKey, Family, MatchMode, ContractVersion, FinishedUtc)
                    VALUES (@g, @p, @t, @f, @m, @v, SYSUTCDATETIME());
                    SELECT CAST(SCOPE_IDENTITY() AS INT);
                    """;
                insert.Parameters.AddWithValue("@g", goldSetId);
                insert.Parameters.AddWithValue("@p", producerId);
                insert.Parameters.AddWithValue("@t", detector.TaskKey);
                insert.Parameters.AddWithValue("@f", detector.Family.ToString());
                insert.Parameters.AddWithValue("@m", mode.ToString());
                insert.Parameters.AddWithValue("@v", ContractInfo.Version);
                runId = (int)(await insert.ExecuteScalarAsync(cancellationToken))!;
            }

            foreach (var row in outcome.Score.ByType.Concat(outcome.Score.ByBuildingType).Prepend(outcome.Score.Overall))
            {
                await using var insertResult = connection.CreateCommand();
                insertResult.Transaction = transaction;
                insertResult.CommandText =
                    """
                    INSERT INTO ductwork.EvalResult (EvalRunId, BuildingType, Type, TruePositives, FalsePositives, FalseNegatives)
                    VALUES (@r, @bt, @t, @tp, @fp, @fn);
                    """;
                insertResult.Parameters.AddWithValue("@r", runId);
                insertResult.Parameters.AddWithValue("@bt", (object?)row.BuildingType ?? DBNull.Value);
                insertResult.Parameters.AddWithValue("@t", (object?)row.Type ?? DBNull.Value);
                insertResult.Parameters.AddWithValue("@tp", row.TruePositives);
                insertResult.Parameters.AddWithValue("@fp", row.FalsePositives);
                insertResult.Parameters.AddWithValue("@fn", row.FalseNegatives);
                await insertResult.ExecuteNonQueryAsync(cancellationToken);
            }

            await SaveCalibrationAsync(connection, transaction, runId, detector.Family, outcome.Calibration, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (runId, outcome);
        }
        catch
        {
            // The holdout trigger rolls the transaction back itself, so a second rollback can throw.
            try { await transaction.RollbackAsync(CancellationToken.None); } catch (InvalidOperationException) { }
            throw;
        }
    }

    /// <summary>Fails fast, before any expensive detection, if the holdout was already spent. The database trigger remains the real guard.</summary>
    private static async Task EnsureHoldoutAvailableAsync(SqlConnection connection, int goldSetId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Role, SpentUtc FROM ductwork.GoldSet WHERE GoldSetId = @g;";
        command.Parameters.AddWithValue("@g", goldSetId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException($"Gold set {goldSetId} does not exist.");
        if (reader.GetString(0) == "Holdout" && !reader.IsDBNull(1))
            throw new InvalidOperationException("The holdout gold set has already been spent. Create a new holdout set.");
    }

    private static async Task SaveCalibrationAsync(
        SqlConnection connection, SqlTransaction transaction, int evalRunId, DetectorFamily family,
        CalibrationReport calibration, CancellationToken cancellationToken)
    {
        int calibrationRunId;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO ductwork.CalibrationRun (EvalRunId, Family) VALUES (@r, @f); SELECT CAST(SCOPE_IDENTITY() AS INT);";
            insert.Parameters.AddWithValue("@r", evalRunId);
            insert.Parameters.AddWithValue("@f", family.ToString());
            calibrationRunId = (int)(await insert.ExecuteScalarAsync(cancellationToken))!;
        }

        foreach (var bin in calibration.Bins)
        {
            await using var insertBin = connection.CreateCommand();
            insertBin.Transaction = transaction;
            insertBin.CommandText =
                "INSERT INTO ductwork.CalibrationBin (CalibrationRunId, BinIndex, LowerBound, UpperBound, Predicted, Correct) VALUES (@c, @i, @l, @u, @p, @k);";
            insertBin.Parameters.AddWithValue("@c", calibrationRunId);
            insertBin.Parameters.AddWithValue("@i", bin.Index);
            insertBin.Parameters.AddWithValue("@l", bin.Lower);
            insertBin.Parameters.AddWithValue("@u", bin.Upper);
            insertBin.Parameters.AddWithValue("@p", bin.Predicted);
            insertBin.Parameters.AddWithValue("@k", bin.Correct);
            await insertBin.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
