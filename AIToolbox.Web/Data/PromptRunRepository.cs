using System.Data;
using AIToolbox.Web.Models;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Web.Data;

public sealed class PromptRunRepository : IPromptRunRepository
{
    private readonly SqlConnectionFactory _connectionFactory;

    public PromptRunRepository(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<int> CreatePromptRunAsync(
        string userEmail,
        string question,
        bool isPublic,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.PromptRuns (UserEmail, Question, IsPublic, CreatedUtc)
            OUTPUT INSERTED.Id
            VALUES (@UserEmail, @Question, @IsPublic, SYSUTCDATETIME());
            """;
        command.Parameters.AddWithValue("@UserEmail", userEmail);
        AddNVarCharMax(command, "@Question", question);
        command.Parameters.Add("@IsPublic", SqlDbType.Bit).Value = isPublic;
        return (int)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PromptRunSummary>> GetVisiblePromptRunsAsync(
        string userEmail,
        string scope,
        int maxRows = 100,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (@MaxRows)
                pr.Id,
                pr.UserEmail,
                pr.Question,
                pr.IsPublic,
                pr.CreatedUtc,
                responseCounts.ResponseCount,
                analysisCounts.AnalysisRunCount,
                analysisCounts.LastAnalysisUtc
            FROM dbo.PromptRuns pr
            OUTER APPLY
            (
                SELECT COUNT(*) AS ResponseCount
                FROM dbo.PromptResponses responses
                WHERE responses.PromptRunId = pr.Id
            ) responseCounts
            OUTER APPLY
            (
                SELECT COUNT(*) AS AnalysisRunCount,
                       MAX(CreatedUtc) AS LastAnalysisUtc
                FROM dbo.AnalysisRuns analyses
                WHERE analyses.PromptRunId = pr.Id
            ) analysisCounts
            WHERE
                (@Scope = 'mine' AND pr.UserEmail = @UserEmail)
                OR (@Scope = 'public' AND pr.IsPublic = 1)
                OR (@Scope = 'all' AND (pr.UserEmail = @UserEmail OR pr.IsPublic = 1))
            ORDER BY pr.CreatedUtc DESC;
            """;
        command.Parameters.AddWithValue("@UserEmail", userEmail);
        command.Parameters.AddWithValue("@Scope", scope);
        command.Parameters.AddWithValue("@MaxRows", Math.Clamp(maxRows, 1, 500));

        var runs = new List<PromptRunSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            runs.Add(new PromptRunSummary
            {
                Id = reader.GetRequiredInt32("Id"),
                UserEmail = reader.GetRequiredString("UserEmail"),
                Question = reader.GetRequiredString("Question"),
                IsPublic = reader.GetRequiredBoolean("IsPublic"),
                CreatedUtc = reader.GetRequiredDateTime("CreatedUtc"),
                ResponseCount = reader.GetRequiredInt32("ResponseCount"),
                AnalysisRunCount = reader.GetRequiredInt32("AnalysisRunCount"),
                LastAnalysisUtc = reader.GetNullableDateTime("LastAnalysisUtc")
            });
        }

        return runs;
    }

    public async Task AddPromptResponsesAsync(
        int promptRunId,
        IEnumerable<AiToolCallResult> responses,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        foreach (var response in responses)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO dbo.PromptResponses
                    (PromptRunId, AiProviderId, ResponseText, ErrorMessage, FinishReason,
                     InputTokens, OutputTokens, DurationMs, CreatedUtc)
                VALUES
                    (@PromptRunId, @AiProviderId, @ResponseText, @ErrorMessage, @FinishReason,
                     @InputTokens, @OutputTokens, @DurationMs, SYSUTCDATETIME());
                """;
            command.Parameters.AddWithValue("@PromptRunId", promptRunId);
            command.Parameters.AddWithValue("@AiProviderId", response.ProviderId);
            AddNVarCharMax(command, "@ResponseText", response.ResponseText);
            AddNVarCharMax(command, "@ErrorMessage", response.ErrorMessage);
            AddNVarChar(command, "@FinishReason", response.FinishReason, 100);
            AddNullableInt(command, "@InputTokens", response.InputTokens);
            AddNullableInt(command, "@OutputTokens", response.OutputTokens);
            command.Parameters.AddWithValue("@DurationMs", response.DurationMs);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<PromptRun?> GetPromptRunAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        PromptRun? run;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT Id, UserEmail, Question, IsPublic, CreatedUtc
                FROM dbo.PromptRuns
                WHERE Id = @Id;
                """;
            command.Parameters.AddWithValue("@Id", id);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            run = new PromptRun
            {
                Id = reader.GetRequiredInt32("Id"),
                UserEmail = reader.GetRequiredString("UserEmail"),
                Question = reader.GetRequiredString("Question"),
                IsPublic = reader.GetRequiredBoolean("IsPublic"),
                CreatedUtc = reader.GetRequiredDateTime("CreatedUtc")
            };
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT pr.Id, pr.PromptRunId, pr.AiProviderId, ap.DisplayName AS ProviderName,
                       pr.ResponseText, pr.ErrorMessage, pr.FinishReason, pr.InputTokens,
                       pr.OutputTokens, pr.DurationMs, pr.CreatedUtc
                FROM dbo.PromptResponses pr
                INNER JOIN dbo.AiProviders ap ON ap.Id = pr.AiProviderId
                WHERE pr.PromptRunId = @PromptRunId
                ORDER BY ap.DisplayName;
                """;
            command.Parameters.AddWithValue("@PromptRunId", id);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                run.Responses.Add(new PromptResponse
                {
                    Id = reader.GetRequiredInt32("Id"),
                    PromptRunId = reader.GetRequiredInt32("PromptRunId"),
                    AiProviderId = reader.GetRequiredInt32("AiProviderId"),
                    ProviderName = reader.GetRequiredString("ProviderName"),
                    ResponseText = reader.GetNullableString("ResponseText"),
                    ErrorMessage = reader.GetNullableString("ErrorMessage"),
                    FinishReason = reader.GetNullableString("FinishReason"),
                    InputTokens = reader["InputTokens"] is DBNull ? null : reader.GetRequiredInt32("InputTokens"),
                    OutputTokens = reader["OutputTokens"] is DBNull ? null : reader.GetRequiredInt32("OutputTokens"),
                    DurationMs = reader.GetRequiredInt32("DurationMs"),
                    CreatedUtc = reader.GetRequiredDateTime("CreatedUtc")
                });
            }
        }

        return run;
    }

    public async Task<IReadOnlyList<AnalysisRunSummary>> GetAnalysisRunSummariesAsync(
        int promptRunId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ar.Id,
                   ar.PromptRunId,
                   ar.UserEmail,
                   ar.CreatedUtc,
                   responseCounts.ResponseCount
            FROM dbo.AnalysisRuns ar
            OUTER APPLY
            (
                SELECT COUNT(*) AS ResponseCount
                FROM dbo.AnalysisResponses responses
                WHERE responses.AnalysisRunId = ar.Id
            ) responseCounts
            WHERE ar.PromptRunId = @PromptRunId
            ORDER BY ar.CreatedUtc DESC;
            """;
        command.Parameters.AddWithValue("@PromptRunId", promptRunId);

        var analyses = new List<AnalysisRunSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            analyses.Add(new AnalysisRunSummary
            {
                Id = reader.GetRequiredInt32("Id"),
                PromptRunId = reader.GetRequiredInt32("PromptRunId"),
                UserEmail = reader.GetRequiredString("UserEmail"),
                CreatedUtc = reader.GetRequiredDateTime("CreatedUtc"),
                ResponseCount = reader.GetRequiredInt32("ResponseCount")
            });
        }

        return analyses;
    }

    public async Task<bool> UpdatePromptRunVisibilityAsync(
        int promptRunId,
        string userEmail,
        bool isPublic,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.PromptRuns
            SET IsPublic = @IsPublic
            WHERE Id = @PromptRunId
                AND UserEmail = @UserEmail;
            """;
        command.Parameters.AddWithValue("@PromptRunId", promptRunId);
        command.Parameters.AddWithValue("@UserEmail", userEmail);
        command.Parameters.Add("@IsPublic", SqlDbType.Bit).Value = isPublic;
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<int> CreateAnalysisRunAsync(
        int promptRunId,
        string userEmail,
        string analysisPrompt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.AnalysisRuns (PromptRunId, UserEmail, AnalysisPrompt, CreatedUtc)
            OUTPUT INSERTED.Id
            VALUES (@PromptRunId, @UserEmail, @AnalysisPrompt, SYSUTCDATETIME());
            """;
        command.Parameters.AddWithValue("@PromptRunId", promptRunId);
        command.Parameters.AddWithValue("@UserEmail", userEmail);
        AddNVarCharMax(command, "@AnalysisPrompt", analysisPrompt);
        return (int)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task AddAnalysisResponsesAsync(
        int analysisRunId,
        IEnumerable<AiToolCallResult> responses,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        foreach (var response in responses)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO dbo.AnalysisResponses
                    (AnalysisRunId, AiProviderId, ResponseText, ErrorMessage, FinishReason,
                     InputTokens, OutputTokens, DurationMs, CreatedUtc)
                VALUES
                    (@AnalysisRunId, @AiProviderId, @ResponseText, @ErrorMessage, @FinishReason,
                     @InputTokens, @OutputTokens, @DurationMs, SYSUTCDATETIME());
                """;
            command.Parameters.AddWithValue("@AnalysisRunId", analysisRunId);
            command.Parameters.AddWithValue("@AiProviderId", response.ProviderId);
            AddNVarCharMax(command, "@ResponseText", response.ResponseText);
            AddNVarCharMax(command, "@ErrorMessage", response.ErrorMessage);
            AddNVarChar(command, "@FinishReason", response.FinishReason, 100);
            AddNullableInt(command, "@InputTokens", response.InputTokens);
            AddNullableInt(command, "@OutputTokens", response.OutputTokens);
            command.Parameters.AddWithValue("@DurationMs", response.DurationMs);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<AnalysisRun?> GetAnalysisRunAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        AnalysisRun? run;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT Id, PromptRunId, UserEmail, AnalysisPrompt, CreatedUtc
                FROM dbo.AnalysisRuns
                WHERE Id = @Id;
                """;
            command.Parameters.AddWithValue("@Id", id);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            run = new AnalysisRun
            {
                Id = reader.GetRequiredInt32("Id"),
                PromptRunId = reader.GetRequiredInt32("PromptRunId"),
                UserEmail = reader.GetRequiredString("UserEmail"),
                AnalysisPrompt = reader.GetRequiredString("AnalysisPrompt"),
                CreatedUtc = reader.GetRequiredDateTime("CreatedUtc")
            };
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT ar.Id, ar.AnalysisRunId, ar.AiProviderId, ap.DisplayName AS ProviderName,
                       ar.ResponseText, ar.ErrorMessage, ar.FinishReason, ar.InputTokens,
                       ar.OutputTokens, ar.DurationMs, ar.CreatedUtc
                FROM dbo.AnalysisResponses ar
                INNER JOIN dbo.AiProviders ap ON ap.Id = ar.AiProviderId
                WHERE ar.AnalysisRunId = @AnalysisRunId
                ORDER BY ap.DisplayName;
                """;
            command.Parameters.AddWithValue("@AnalysisRunId", id);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                run.Responses.Add(new AnalysisResponse
                {
                    Id = reader.GetRequiredInt32("Id"),
                    AnalysisRunId = reader.GetRequiredInt32("AnalysisRunId"),
                    AiProviderId = reader.GetRequiredInt32("AiProviderId"),
                    ProviderName = reader.GetRequiredString("ProviderName"),
                    ResponseText = reader.GetNullableString("ResponseText"),
                    ErrorMessage = reader.GetNullableString("ErrorMessage"),
                    FinishReason = reader.GetNullableString("FinishReason"),
                    InputTokens = reader["InputTokens"] is DBNull ? null : reader.GetRequiredInt32("InputTokens"),
                    OutputTokens = reader["OutputTokens"] is DBNull ? null : reader.GetRequiredInt32("OutputTokens"),
                    DurationMs = reader.GetRequiredInt32("DurationMs"),
                    CreatedUtc = reader.GetRequiredDateTime("CreatedUtc")
                });
            }
        }

        return run;
    }

    private static void AddNVarCharMax(SqlCommand command, string name, string? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.NVarChar, -1);
        parameter.Value = (object?)value ?? DBNull.Value;
    }

    private static void AddNVarChar(SqlCommand command, string name, string? value, int size)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.NVarChar, size);
        parameter.Value = (object?)value ?? DBNull.Value;
    }

    private static void AddNullableInt(SqlCommand command, string name, int? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Int);
        parameter.Value = (object?)value ?? DBNull.Value;
    }
}
