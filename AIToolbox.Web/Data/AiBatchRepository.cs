using AIToolbox.Web.Models;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Web.Data;

public sealed class AiBatchRepository : IAiBatchRepository
{
    private readonly SqlConnectionFactory _connectionFactory;

    public AiBatchRepository(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<int> CreateJobAsync(AiBatchJob job, IReadOnlyList<AiBatchRequest> requests, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = (SqlTransaction)transaction;
        command.CommandText = """
            INSERT INTO dbo.AiBatchJobs
                (ToolKey, ConsumerId, UserEmail, AiProviderId, JobType, Status, InputMode, RequestJson, ResultJson,
                 ErrorMessage, ProviderBatchId, ProviderInputFileId, ProviderOutputFileId, ProviderErrorFileId,
                 ProviderResultsUrl, TotalRequestCount, SucceededRequestCount, FailedRequestCount, CanceledRequestCount,
                 ExpiredRequestCount, CallbackUrl, CallbackSecretCipherText, CallbackStatus, CallbackAttemptCount,
                 LastCallbackAttemptUtc, CreatedUtc, SubmittedUtc, LastPolledUtc, CompletedUtc, ExpiresUtc)
            OUTPUT INSERTED.Id
            VALUES
                (@ToolKey, @ConsumerId, @UserEmail, @AiProviderId, @JobType, @Status, @InputMode, @RequestJson, @ResultJson,
                 @ErrorMessage, @ProviderBatchId, @ProviderInputFileId, @ProviderOutputFileId, @ProviderErrorFileId,
                 @ProviderResultsUrl, @TotalRequestCount, @SucceededRequestCount, @FailedRequestCount, @CanceledRequestCount,
                 @ExpiredRequestCount, @CallbackUrl, @CallbackSecretCipherText, @CallbackStatus, @CallbackAttemptCount,
                 @LastCallbackAttemptUtc, SYSUTCDATETIME(), @SubmittedUtc, @LastPolledUtc, @CompletedUtc, @ExpiresUtc);
            """;
        AddJobParameters(command, job);
        var jobId = (int)await command.ExecuteScalarAsync(cancellationToken);

        foreach (var request in requests)
        {
            await using var requestCommand = connection.CreateCommand();
            requestCommand.Transaction = (SqlTransaction)transaction;
            requestCommand.CommandText = """
                INSERT INTO dbo.AiBatchRequests
                    (AiBatchJobId, CustomId, SequenceNumber, SourceObjectType, SourceObjectId, Status,
                     RequestJson, ResponseJson, ErrorJson, InputTokens, OutputTokens, ProviderRequestId, CreatedUtc, CompletedUtc)
                VALUES
                    (@AiBatchJobId, @CustomId, @SequenceNumber, @SourceObjectType, @SourceObjectId, @Status,
                     @RequestJson, @ResponseJson, @ErrorJson, @InputTokens, @OutputTokens, @ProviderRequestId,
                     SYSUTCDATETIME(), @CompletedUtc);
                """;
            requestCommand.Parameters.AddWithValue("@AiBatchJobId", jobId);
            AddRequestParameters(requestCommand, request);
            await requestCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return jobId;
    }

    public async Task<IReadOnlyList<AiBatchJob>> GetJobsAsync(
        string? userEmail,
        int? consumerId,
        bool includeAll,
        CancellationToken cancellationToken = default)
    {
        var jobs = new List<AiBatchJob>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            {JobSelectSql}
            WHERE @IncludeAll = 1
                OR (@ConsumerId IS NOT NULL AND jobs.ConsumerId = @ConsumerId)
                OR (@UserEmailNormalized IS NOT NULL AND UPPER(jobs.UserEmail) = @UserEmailNormalized)
            ORDER BY jobs.CreatedUtc DESC;
            """;
        AddOwnershipParameters(command, userEmail, consumerId, includeAll);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(MapJob(reader));
        }

        return jobs;
    }

    public async Task<IReadOnlyList<AiBatchJob>> GetPollableJobsAsync(CancellationToken cancellationToken = default)
    {
        var jobs = new List<AiBatchJob>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            {JobSelectSql}
            WHERE jobs.Status IN ('Submitted', 'WaitingOnProvider', 'Cancelling', 'RetrievingResults')
            ORDER BY COALESCE(jobs.LastPolledUtc, jobs.SubmittedUtc, jobs.CreatedUtc);
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(MapJob(reader));
        }

        return jobs;
    }

    public async Task<AiBatchJob?> GetJobAsync(
        int id,
        string? userEmail,
        int? consumerId,
        bool includeAll,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            {JobSelectSql}
            WHERE jobs.Id = @Id
                AND (
                    @IncludeAll = 1
                    OR (@ConsumerId IS NOT NULL AND jobs.ConsumerId = @ConsumerId)
                    OR (@UserEmailNormalized IS NOT NULL AND UPPER(jobs.UserEmail) = @UserEmailNormalized)
                );
            """;
        command.Parameters.AddWithValue("@Id", id);
        AddOwnershipParameters(command, userEmail, consumerId, includeAll);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapJob(reader) : null;
    }

    public async Task<AiBatchJobDetail?> GetJobDetailAsync(
        int id,
        string? userEmail,
        int? consumerId,
        bool includeAll,
        CancellationToken cancellationToken = default)
    {
        var job = await GetJobAsync(id, userEmail, consumerId, includeAll, cancellationToken);
        if (job is null)
        {
            return null;
        }

        return new AiBatchJobDetail
        {
            Job = job,
            Requests = await GetRequestsAsync(id, cancellationToken)
        };
    }

    public async Task<IReadOnlyList<AiBatchRequest>> GetRequestsAsync(int jobId, CancellationToken cancellationToken = default)
    {
        var requests = new List<AiBatchRequest>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, AiBatchJobId, CustomId, SequenceNumber, SourceObjectType, SourceObjectId, Status,
                   RequestJson, ResponseJson, ErrorJson, InputTokens, OutputTokens, ProviderRequestId,
                   CreatedUtc, CompletedUtc
            FROM dbo.AiBatchRequests
            WHERE AiBatchJobId = @AiBatchJobId
            ORDER BY SequenceNumber, Id;
            """;
        command.Parameters.AddWithValue("@AiBatchJobId", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            requests.Add(MapRequest(reader));
        }

        return requests;
    }

    public async Task UpdateJobAsync(AiBatchJob job, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.AiBatchJobs
            SET ToolKey = @ToolKey,
                ConsumerId = @ConsumerId,
                UserEmail = @UserEmail,
                AiProviderId = @AiProviderId,
                JobType = @JobType,
                Status = @Status,
                InputMode = @InputMode,
                RequestJson = @RequestJson,
                ResultJson = @ResultJson,
                ErrorMessage = @ErrorMessage,
                ProviderBatchId = @ProviderBatchId,
                ProviderInputFileId = @ProviderInputFileId,
                ProviderOutputFileId = @ProviderOutputFileId,
                ProviderErrorFileId = @ProviderErrorFileId,
                ProviderResultsUrl = @ProviderResultsUrl,
                TotalRequestCount = @TotalRequestCount,
                SucceededRequestCount = @SucceededRequestCount,
                FailedRequestCount = @FailedRequestCount,
                CanceledRequestCount = @CanceledRequestCount,
                ExpiredRequestCount = @ExpiredRequestCount,
                CallbackUrl = @CallbackUrl,
                CallbackSecretCipherText = @CallbackSecretCipherText,
                CallbackStatus = @CallbackStatus,
                CallbackAttemptCount = @CallbackAttemptCount,
                LastCallbackAttemptUtc = @LastCallbackAttemptUtc,
                SubmittedUtc = @SubmittedUtc,
                LastPolledUtc = @LastPolledUtc,
                CompletedUtc = @CompletedUtc,
                ExpiresUtc = @ExpiresUtc
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", job.Id);
        AddJobParameters(command, job);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateRequestAsync(AiBatchRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.AiBatchRequests
            SET Status = @Status,
                ResponseJson = @ResponseJson,
                ErrorJson = @ErrorJson,
                InputTokens = @InputTokens,
                OutputTokens = @OutputTokens,
                ProviderRequestId = @ProviderRequestId,
                CompletedUtc = @CompletedUtc
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", request.Id);
        AddRequestParameters(command, request);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkJobCancelRequestedAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.AiBatchJobs
            SET Status = CASE
                    WHEN ProviderBatchId IS NULL THEN 'Cancelled'
                    ELSE 'CancelRequested'
                END,
                CompletedUtc = CASE
                    WHEN ProviderBatchId IS NULL THEN SYSUTCDATETIME()
                    ELSE CompletedUtc
                END
            WHERE Id = @Id
                AND Status NOT IN ('Succeeded', 'PartiallySucceeded', 'Failed', 'Cancelled', 'Expired');
            """;
        command.Parameters.AddWithValue("@Id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string JobSelectSql = """
        SELECT jobs.Id, jobs.ToolKey, jobs.ConsumerId, consumers.Name AS ConsumerName, jobs.UserEmail,
               jobs.AiProviderId, providers.DisplayName AS AiProviderName, providers.ProviderType,
               jobs.JobType, jobs.Status, jobs.InputMode, jobs.RequestJson, jobs.ResultJson, jobs.ErrorMessage,
               jobs.ProviderBatchId, jobs.ProviderInputFileId, jobs.ProviderOutputFileId, jobs.ProviderErrorFileId,
               jobs.ProviderResultsUrl, jobs.TotalRequestCount, jobs.SucceededRequestCount, jobs.FailedRequestCount,
               jobs.CanceledRequestCount, jobs.ExpiredRequestCount, jobs.CallbackUrl, jobs.CallbackSecretCipherText,
               jobs.CallbackStatus, jobs.CallbackAttemptCount, jobs.LastCallbackAttemptUtc, jobs.CreatedUtc,
               jobs.SubmittedUtc, jobs.LastPolledUtc, jobs.CompletedUtc, jobs.ExpiresUtc
        FROM dbo.AiBatchJobs jobs
        LEFT JOIN dbo.ApiConsumers consumers ON consumers.Id = jobs.ConsumerId
        INNER JOIN dbo.AiProviders providers ON providers.Id = jobs.AiProviderId
        """;

    private static void AddOwnershipParameters(SqlCommand command, string? userEmail, int? consumerId, bool includeAll)
    {
        command.Parameters.AddWithValue("@IncludeAll", includeAll);
        command.Parameters.AddWithValue("@ConsumerId", (object?)consumerId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@UserEmailNormalized",
            string.IsNullOrWhiteSpace(userEmail) ? DBNull.Value : userEmail.Trim().ToUpperInvariant());
    }

    private static void AddJobParameters(SqlCommand command, AiBatchJob job)
    {
        command.Parameters.AddWithValue("@ToolKey", job.ToolKey.Trim());
        command.Parameters.AddWithValue("@ConsumerId", (object?)job.ConsumerId ?? DBNull.Value);
        command.Parameters.AddWithValue("@UserEmail", job.UserEmail.Trim());
        command.Parameters.AddWithValue("@AiProviderId", job.AiProviderId);
        command.Parameters.AddWithValue("@JobType", job.JobType.Trim());
        command.Parameters.AddWithValue("@Status", job.Status);
        command.Parameters.AddWithValue("@InputMode", job.InputMode);
        command.Parameters.AddWithValue("@RequestJson", (object?)job.RequestJson ?? DBNull.Value);
        command.Parameters.AddWithValue("@ResultJson", (object?)job.ResultJson ?? DBNull.Value);
        command.Parameters.AddWithValue("@ErrorMessage", (object?)job.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("@ProviderBatchId", (object?)job.ProviderBatchId ?? DBNull.Value);
        command.Parameters.AddWithValue("@ProviderInputFileId", (object?)job.ProviderInputFileId ?? DBNull.Value);
        command.Parameters.AddWithValue("@ProviderOutputFileId", (object?)job.ProviderOutputFileId ?? DBNull.Value);
        command.Parameters.AddWithValue("@ProviderErrorFileId", (object?)job.ProviderErrorFileId ?? DBNull.Value);
        command.Parameters.AddWithValue("@ProviderResultsUrl", (object?)job.ProviderResultsUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("@TotalRequestCount", job.TotalRequestCount);
        command.Parameters.AddWithValue("@SucceededRequestCount", job.SucceededRequestCount);
        command.Parameters.AddWithValue("@FailedRequestCount", job.FailedRequestCount);
        command.Parameters.AddWithValue("@CanceledRequestCount", job.CanceledRequestCount);
        command.Parameters.AddWithValue("@ExpiredRequestCount", job.ExpiredRequestCount);
        command.Parameters.AddWithValue("@CallbackUrl", (object?)job.CallbackUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("@CallbackSecretCipherText", (object?)job.CallbackSecretCipherText ?? DBNull.Value);
        command.Parameters.AddWithValue("@CallbackStatus", (object?)job.CallbackStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("@CallbackAttemptCount", job.CallbackAttemptCount);
        command.Parameters.AddWithValue("@LastCallbackAttemptUtc", (object?)job.LastCallbackAttemptUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("@SubmittedUtc", (object?)job.SubmittedUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("@LastPolledUtc", (object?)job.LastPolledUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("@CompletedUtc", (object?)job.CompletedUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("@ExpiresUtc", (object?)job.ExpiresUtc ?? DBNull.Value);
    }

    private static void AddRequestParameters(SqlCommand command, AiBatchRequest request)
    {
        command.Parameters.AddWithValue("@CustomId", request.CustomId);
        command.Parameters.AddWithValue("@SequenceNumber", request.SequenceNumber);
        command.Parameters.AddWithValue("@SourceObjectType", (object?)request.SourceObjectType ?? DBNull.Value);
        command.Parameters.AddWithValue("@SourceObjectId", (object?)request.SourceObjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("@Status", request.Status);
        command.Parameters.AddWithValue("@RequestJson", request.RequestJson);
        command.Parameters.AddWithValue("@ResponseJson", (object?)request.ResponseJson ?? DBNull.Value);
        command.Parameters.AddWithValue("@ErrorJson", (object?)request.ErrorJson ?? DBNull.Value);
        command.Parameters.AddWithValue("@InputTokens", (object?)request.InputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("@OutputTokens", (object?)request.OutputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("@ProviderRequestId", (object?)request.ProviderRequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("@CompletedUtc", (object?)request.CompletedUtc ?? DBNull.Value);
    }

    private static AiBatchJob MapJob(SqlDataReader reader)
    {
        Enum.TryParse<AiProviderType>(reader.GetRequiredString("ProviderType"), ignoreCase: true, out var providerType);
        return new AiBatchJob
        {
            Id = reader.GetRequiredInt32("Id"),
            ToolKey = reader.GetRequiredString("ToolKey"),
            ConsumerId = reader["ConsumerId"] is DBNull ? null : reader.GetRequiredInt32("ConsumerId"),
            ConsumerName = reader.GetNullableString("ConsumerName"),
            UserEmail = reader.GetRequiredString("UserEmail"),
            AiProviderId = reader.GetRequiredInt32("AiProviderId"),
            AiProviderName = reader.GetNullableString("AiProviderName"),
            ProviderType = providerType,
            JobType = reader.GetRequiredString("JobType"),
            Status = reader.GetRequiredString("Status"),
            InputMode = reader.GetRequiredString("InputMode"),
            RequestJson = reader.GetNullableString("RequestJson"),
            ResultJson = reader.GetNullableString("ResultJson"),
            ErrorMessage = reader.GetNullableString("ErrorMessage"),
            ProviderBatchId = reader.GetNullableString("ProviderBatchId"),
            ProviderInputFileId = reader.GetNullableString("ProviderInputFileId"),
            ProviderOutputFileId = reader.GetNullableString("ProviderOutputFileId"),
            ProviderErrorFileId = reader.GetNullableString("ProviderErrorFileId"),
            ProviderResultsUrl = reader.GetNullableString("ProviderResultsUrl"),
            TotalRequestCount = reader.GetRequiredInt32("TotalRequestCount"),
            SucceededRequestCount = reader.GetRequiredInt32("SucceededRequestCount"),
            FailedRequestCount = reader.GetRequiredInt32("FailedRequestCount"),
            CanceledRequestCount = reader.GetRequiredInt32("CanceledRequestCount"),
            ExpiredRequestCount = reader.GetRequiredInt32("ExpiredRequestCount"),
            CallbackUrl = reader.GetNullableString("CallbackUrl"),
            CallbackSecretCipherText = reader.GetNullableString("CallbackSecretCipherText"),
            CallbackStatus = reader.GetNullableString("CallbackStatus"),
            CallbackAttemptCount = reader.GetRequiredInt32("CallbackAttemptCount"),
            LastCallbackAttemptUtc = reader.GetNullableDateTime("LastCallbackAttemptUtc"),
            CreatedUtc = reader.GetRequiredDateTime("CreatedUtc"),
            SubmittedUtc = reader.GetNullableDateTime("SubmittedUtc"),
            LastPolledUtc = reader.GetNullableDateTime("LastPolledUtc"),
            CompletedUtc = reader.GetNullableDateTime("CompletedUtc"),
            ExpiresUtc = reader.GetNullableDateTime("ExpiresUtc")
        };
    }

    private static AiBatchRequest MapRequest(SqlDataReader reader)
    {
        return new AiBatchRequest
        {
            Id = reader.GetRequiredInt32("Id"),
            AiBatchJobId = reader.GetRequiredInt32("AiBatchJobId"),
            CustomId = reader.GetRequiredString("CustomId"),
            SequenceNumber = reader.GetRequiredInt32("SequenceNumber"),
            SourceObjectType = reader.GetNullableString("SourceObjectType"),
            SourceObjectId = reader.GetNullableString("SourceObjectId"),
            Status = reader.GetRequiredString("Status"),
            RequestJson = reader.GetRequiredString("RequestJson"),
            ResponseJson = reader.GetNullableString("ResponseJson"),
            ErrorJson = reader.GetNullableString("ErrorJson"),
            InputTokens = reader["InputTokens"] is DBNull ? null : reader.GetRequiredInt32("InputTokens"),
            OutputTokens = reader["OutputTokens"] is DBNull ? null : reader.GetRequiredInt32("OutputTokens"),
            ProviderRequestId = reader.GetNullableString("ProviderRequestId"),
            CreatedUtc = reader.GetRequiredDateTime("CreatedUtc"),
            CompletedUtc = reader.GetNullableDateTime("CompletedUtc")
        };
    }
}
