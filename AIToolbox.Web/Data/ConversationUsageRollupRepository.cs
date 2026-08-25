using AIToolbox.Web.Models;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Web.Data;

/// <summary>
/// UPDATE-first, INSERT-on-zero-rows, with the (Date, Surface) primary key catching a genuine
/// concurrent-insert race as a duplicate-key error that gets retried as an UPDATE — the safe
/// direction for this codebase's known "IF EXISTS...ELSE INSERT" upsert race bug class.
/// </summary>
public sealed class ConversationUsageRollupRepository : IConversationUsageRollupRepository
{
    private const int UniqueConstraintViolation = 2627;
    private const int DuplicateKeyError = 2601;

    private readonly SqlConnectionFactory _connectionFactory;

    public ConversationUsageRollupRepository(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task IncrementAsync(
        DateOnly date,
        string surface,
        int conversationDelta = 0,
        int turnDelta = 0,
        int tokensInDelta = 0,
        int tokensOutDelta = 0,
        decimal estimatedCostUsdDelta = 0m,
        int degradedTierDelta = 0,
        int budgetExhaustedDelta = 0,
        int failureDelta = 0,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var updated = await TryUpdateAsync(connection, date, surface, conversationDelta, turnDelta, tokensInDelta,
            tokensOutDelta, estimatedCostUsdDelta, degradedTierDelta, budgetExhaustedDelta, failureDelta, cancellationToken);
        if (updated)
        {
            return;
        }

        try
        {
            await InsertAsync(connection, date, surface, conversationDelta, turnDelta, tokensInDelta,
                tokensOutDelta, estimatedCostUsdDelta, degradedTierDelta, budgetExhaustedDelta, failureDelta, cancellationToken);
        }
        catch (SqlException ex) when (ex.Number is UniqueConstraintViolation or DuplicateKeyError)
        {
            // Another request inserted the row between our UPDATE and INSERT attempts.
            await TryUpdateAsync(connection, date, surface, conversationDelta, turnDelta, tokensInDelta,
                tokensOutDelta, estimatedCostUsdDelta, degradedTierDelta, budgetExhaustedDelta, failureDelta, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<ConversationUsageRollup>> GetAsync(
        DateOnly fromDate,
        DateOnly toDate,
        string surface,
        CancellationToken cancellationToken = default)
    {
        var normalizedSurface = string.IsNullOrWhiteSpace(surface) ? "all" : surface.Trim();
        var surfaceClause = string.Equals(normalizedSurface, "all", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : "AND Surface = @Surface";

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT [Date], Surface, ConversationCount, TurnCount, TokensIn, TokensOut,
                   EstimatedCostUsd, DegradedTierCount, BudgetExhaustedCount, FailureCount
            FROM dbo.ConversationUsageRollup
            WHERE [Date] BETWEEN @FromDate AND @ToDate
                {surfaceClause}
            ORDER BY [Date] ASC, Surface ASC;
            """;
        command.Parameters.AddWithValue("@FromDate", fromDate);
        command.Parameters.AddWithValue("@ToDate", toDate);
        if (!string.Equals(normalizedSurface, "all", StringComparison.OrdinalIgnoreCase))
        {
            command.Parameters.AddWithValue("@Surface", normalizedSurface);
        }

        var rows = new List<ConversationUsageRollup>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(MapRollup(reader));
        }

        return rows;
    }

    private static ConversationUsageRollup MapRollup(SqlDataReader reader)
    {
        return new ConversationUsageRollup
        {
            Date = DateOnly.FromDateTime((DateTime)reader["Date"]),
            Surface = (string)reader["Surface"],
            ConversationCount = (int)reader["ConversationCount"],
            TurnCount = (int)reader["TurnCount"],
            TokensIn = (int)reader["TokensIn"],
            TokensOut = (int)reader["TokensOut"],
            EstimatedCostUsd = (decimal)reader["EstimatedCostUsd"],
            DegradedTierCount = (int)reader["DegradedTierCount"],
            BudgetExhaustedCount = (int)reader["BudgetExhaustedCount"],
            FailureCount = (int)reader["FailureCount"]
        };
    }

    private static async Task<bool> TryUpdateAsync(
        SqlConnection connection,
        DateOnly date,
        string surface,
        int conversationDelta,
        int turnDelta,
        int tokensInDelta,
        int tokensOutDelta,
        decimal estimatedCostUsdDelta,
        int degradedTierDelta,
        int budgetExhaustedDelta,
        int failureDelta,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.ConversationUsageRollup
            SET ConversationCount = ConversationCount + @ConversationDelta,
                TurnCount = TurnCount + @TurnDelta,
                TokensIn = TokensIn + @TokensInDelta,
                TokensOut = TokensOut + @TokensOutDelta,
                EstimatedCostUsd = EstimatedCostUsd + @EstimatedCostUsdDelta,
                DegradedTierCount = DegradedTierCount + @DegradedTierDelta,
                BudgetExhaustedCount = BudgetExhaustedCount + @BudgetExhaustedDelta,
                FailureCount = FailureCount + @FailureDelta
            WHERE [Date] = @Date AND Surface = @Surface;
            """;
        AddParameters(command, date, surface, conversationDelta, turnDelta, tokensInDelta, tokensOutDelta,
            estimatedCostUsdDelta, degradedTierDelta, budgetExhaustedDelta, failureDelta);
        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
        return rowsAffected > 0;
    }

    private static async Task InsertAsync(
        SqlConnection connection,
        DateOnly date,
        string surface,
        int conversationDelta,
        int turnDelta,
        int tokensInDelta,
        int tokensOutDelta,
        decimal estimatedCostUsdDelta,
        int degradedTierDelta,
        int budgetExhaustedDelta,
        int failureDelta,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.ConversationUsageRollup
                ([Date], Surface, ConversationCount, TurnCount, TokensIn, TokensOut,
                 EstimatedCostUsd, DegradedTierCount, BudgetExhaustedCount, FailureCount)
            VALUES
                (@Date, @Surface, @ConversationDelta, @TurnDelta, @TokensInDelta, @TokensOutDelta,
                 @EstimatedCostUsdDelta, @DegradedTierDelta, @BudgetExhaustedDelta, @FailureDelta);
            """;
        AddParameters(command, date, surface, conversationDelta, turnDelta, tokensInDelta, tokensOutDelta,
            estimatedCostUsdDelta, degradedTierDelta, budgetExhaustedDelta, failureDelta);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameters(
        SqlCommand command,
        DateOnly date,
        string surface,
        int conversationDelta,
        int turnDelta,
        int tokensInDelta,
        int tokensOutDelta,
        decimal estimatedCostUsdDelta,
        int degradedTierDelta,
        int budgetExhaustedDelta,
        int failureDelta)
    {
        command.Parameters.AddWithValue("@Date", date);
        command.Parameters.AddWithValue("@Surface", surface);
        command.Parameters.AddWithValue("@ConversationDelta", conversationDelta);
        command.Parameters.AddWithValue("@TurnDelta", turnDelta);
        command.Parameters.AddWithValue("@TokensInDelta", tokensInDelta);
        command.Parameters.AddWithValue("@TokensOutDelta", tokensOutDelta);
        command.Parameters.AddWithValue("@EstimatedCostUsdDelta", estimatedCostUsdDelta);
        command.Parameters.AddWithValue("@DegradedTierDelta", degradedTierDelta);
        command.Parameters.AddWithValue("@BudgetExhaustedDelta", budgetExhaustedDelta);
        command.Parameters.AddWithValue("@FailureDelta", failureDelta);
    }
}
