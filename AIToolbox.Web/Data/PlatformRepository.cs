using System.Data;
using AIToolbox.Web.Models;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Web.Data;

public sealed class PlatformRepository : IPlatformRepository
{
    private readonly SqlConnectionFactory _connectionFactory;

    public PlatformRepository(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default)
    {
        var tools = new List<ToolDefinition>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT td.Id, td.ToolKey, td.DisplayName, td.Description, td.IsEnabled, td.AccessMode,
                   td.CreatedUtc, td.UpdatedUtc, tpa.AiProviderId AS ActiveProviderId,
                   ap.DisplayName AS ActiveProviderName
            FROM dbo.ToolDefinitions td
            LEFT JOIN dbo.ToolProviderAssignments tpa ON tpa.ToolKey = td.ToolKey AND tpa.IsActive = 1
            LEFT JOIN dbo.AiProviders ap ON ap.Id = tpa.AiProviderId
            ORDER BY td.DisplayName;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tools.Add(MapTool(reader));
        }

        return tools;
    }

    public async Task<ToolDefinition?> GetToolAsync(string toolKey, CancellationToken cancellationToken = default)
    {
        return (await GetToolsAsync(cancellationToken))
            .FirstOrDefault(tool => string.Equals(tool.ToolKey, toolKey, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ToolDefinition?> GetToolByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return (await GetToolsAsync(cancellationToken)).FirstOrDefault(tool => tool.Id == id);
    }

    public async Task<int> SaveToolAsync(ToolDefinition tool, int? activeProviderId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var toolId = tool.Id;

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqlTransaction)transaction;
            command.CommandText = tool.Id == 0
                ? """
                  INSERT INTO dbo.ToolDefinitions
                      (ToolKey, DisplayName, Description, IsEnabled, AccessMode, CreatedUtc, UpdatedUtc)
                  OUTPUT INSERTED.Id
                  VALUES
                      (@ToolKey, @DisplayName, @Description, @IsEnabled, @AccessMode, SYSUTCDATETIME(), SYSUTCDATETIME());
                  """
                : """
                  UPDATE dbo.ToolDefinitions
                  SET ToolKey = @ToolKey,
                      DisplayName = @DisplayName,
                      Description = @Description,
                      IsEnabled = @IsEnabled,
                      AccessMode = @AccessMode,
                      UpdatedUtc = SYSUTCDATETIME()
                  WHERE Id = @Id;
                  SELECT @Id;
                  """;
            command.Parameters.AddWithValue("@Id", tool.Id);
            AddToolParameters(command, tool);
            toolId = (int)await command.ExecuteScalarAsync(cancellationToken);
        }

        await using (var clearCommand = connection.CreateCommand())
        {
            clearCommand.Transaction = (SqlTransaction)transaction;
            clearCommand.CommandText = """
                UPDATE dbo.ToolProviderAssignments
                SET IsActive = 0
                WHERE ToolKey = @ToolKey;
                """;
            clearCommand.Parameters.AddWithValue("@ToolKey", tool.ToolKey.Trim());
            await clearCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (activeProviderId is > 0)
        {
            await using var assignmentCommand = connection.CreateCommand();
            assignmentCommand.Transaction = (SqlTransaction)transaction;
            assignmentCommand.CommandText = """
                INSERT INTO dbo.ToolProviderAssignments (ToolKey, AiProviderId, IsActive, CreatedUtc, UpdatedUtc)
                VALUES (@ToolKey, @AiProviderId, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                """;
            assignmentCommand.Parameters.AddWithValue("@ToolKey", tool.ToolKey.Trim());
            assignmentCommand.Parameters.AddWithValue("@AiProviderId", activeProviderId.Value);
            await assignmentCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return toolId;
    }

    public async Task<IReadOnlyList<ToolUserAllowListEntry>> GetAllowListAsync(string toolKey, CancellationToken cancellationToken = default)
    {
        var entries = new List<ToolUserAllowListEntry>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ToolKey, UserEmail, IsEnabled, CreatedUtc, GrantedByEmail
            FROM dbo.ToolUserAllowList
            WHERE ToolKey = @ToolKey
            ORDER BY UserEmail;
            """;
        command.Parameters.AddWithValue("@ToolKey", toolKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new ToolUserAllowListEntry
            {
                Id = reader.GetRequiredInt32("Id"),
                ToolKey = reader.GetRequiredString("ToolKey"),
                UserEmail = reader.GetRequiredString("UserEmail"),
                IsEnabled = reader.GetRequiredBoolean("IsEnabled"),
                CreatedUtc = reader.GetRequiredDateTime("CreatedUtc"),
                GrantedByEmail = reader.GetNullableString("GrantedByEmail")
            });
        }

        return entries;
    }

    public async Task<bool> IsUserAllowedForToolAsync(string toolKey, string userEmail, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(1)
            FROM dbo.ToolUserAllowList
            WHERE ToolKey = @ToolKey
                AND UPPER(UserEmail) = @UserEmailNormalized
                AND IsEnabled = 1;
            """;
        command.Parameters.AddWithValue("@ToolKey", toolKey);
        command.Parameters.AddWithValue("@UserEmailNormalized", userEmail.Trim().ToUpperInvariant());
        return (int)await command.ExecuteScalarAsync(cancellationToken) > 0;
    }

    public async Task AddAllowListEntryAsync(string toolKey, string userEmail, string? grantedByEmail, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF EXISTS (SELECT 1 FROM dbo.ToolUserAllowList WHERE ToolKey = @ToolKey AND UPPER(UserEmail) = @UserEmailNormalized)
            BEGIN
                UPDATE dbo.ToolUserAllowList
                SET UserEmail = @UserEmail,
                    IsEnabled = 1,
                    GrantedByEmail = @GrantedByEmail
                WHERE ToolKey = @ToolKey AND UPPER(UserEmail) = @UserEmailNormalized;
            END
            ELSE
            BEGIN
                INSERT INTO dbo.ToolUserAllowList (ToolKey, UserEmail, IsEnabled, CreatedUtc, GrantedByEmail)
                VALUES (@ToolKey, @UserEmail, 1, SYSUTCDATETIME(), @GrantedByEmail);
            END
            """;
        command.Parameters.AddWithValue("@ToolKey", toolKey);
        command.Parameters.AddWithValue("@UserEmail", userEmail.Trim());
        command.Parameters.AddWithValue("@UserEmailNormalized", userEmail.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("@GrantedByEmail", (object?)grantedByEmail ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetAllowListEntryEnabledAsync(int id, bool isEnabled, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE dbo.ToolUserAllowList SET IsEnabled = @IsEnabled WHERE Id = @Id;";
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.Add("@IsEnabled", SqlDbType.Bit).Value = isEnabled;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ToolDirective>> GetDirectivesAsync(CancellationToken cancellationToken = default)
    {
        var directives = new List<ToolDirective>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ToolKey, Name, SystemPrompt, TargetSchemaJson, Version, IsActive, CreatedUtc, UpdatedUtc
            FROM dbo.ToolDirectives
            ORDER BY ToolKey, IsActive DESC, Version DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            directives.Add(MapDirective(reader));
        }

        return directives;
    }

    public async Task<ToolDirective?> GetDirectiveAsync(int id, CancellationToken cancellationToken = default)
    {
        return (await GetDirectivesAsync(cancellationToken)).FirstOrDefault(directive => directive.Id == id);
    }

    public async Task<ToolDirective?> GetActiveDirectiveAsync(string toolKey, CancellationToken cancellationToken = default)
    {
        return (await GetDirectivesAsync(cancellationToken))
            .FirstOrDefault(directive => directive.IsActive && string.Equals(directive.ToolKey, toolKey, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<int> SaveDirectiveAsync(ToolDirective directive, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = directive.Id == 0
            ? """
              INSERT INTO dbo.ToolDirectives
                  (ToolKey, Name, SystemPrompt, TargetSchemaJson, Version, IsActive, CreatedUtc, UpdatedUtc)
              OUTPUT INSERTED.Id
              VALUES
                  (@ToolKey, @Name, @SystemPrompt, @TargetSchemaJson, @Version, @IsActive, SYSUTCDATETIME(), SYSUTCDATETIME());
              """
            : """
              UPDATE dbo.ToolDirectives
              SET ToolKey = @ToolKey,
                  Name = @Name,
                  SystemPrompt = @SystemPrompt,
                  TargetSchemaJson = @TargetSchemaJson,
                  Version = @Version,
                  IsActive = @IsActive,
                  UpdatedUtc = SYSUTCDATETIME()
              WHERE Id = @Id;
              SELECT @Id;
              """;
        command.Parameters.AddWithValue("@Id", directive.Id);
        command.Parameters.AddWithValue("@ToolKey", directive.ToolKey.Trim());
        command.Parameters.AddWithValue("@Name", directive.Name.Trim());
        AddNVarCharMax(command, "@SystemPrompt", directive.SystemPrompt);
        AddNVarCharMax(command, "@TargetSchemaJson", directive.TargetSchemaJson);
        command.Parameters.AddWithValue("@Version", directive.Version);
        command.Parameters.Add("@IsActive", SqlDbType.Bit).Value = directive.IsActive;
        return (int)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task<ToolDirectiveSnapshot> GetOrCreateDirectiveSnapshotAsync(
        ToolDirectiveSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.ToolDirectiveSnapshots
                (ToolDirectiveId, ToolKey, Name, SystemPrompt, TargetSchemaJson, Version, ContentHash, CreatedUtc)
            SELECT @ToolDirectiveId, @ToolKey, @Name, @SystemPrompt, @TargetSchemaJson, @Version, @ContentHash, SYSUTCDATETIME()
            WHERE NOT EXISTS
                (
                    SELECT 1
                    FROM dbo.ToolDirectiveSnapshots WITH (UPDLOCK, HOLDLOCK)
                    WHERE ContentHash = @ContentHash
                );

            SELECT TOP (1) Id, ToolDirectiveId, ToolKey, Name, SystemPrompt, TargetSchemaJson,
                   UserAdditiveDirective, Version, ContentHash, CreatedUtc
            FROM dbo.ToolDirectiveSnapshots
            WHERE ContentHash = @ContentHash;
            """;
        AddDirectiveSnapshotParameters(command, snapshot);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return MapDirectiveSnapshot(reader);
        }

        throw new InvalidOperationException("The directive snapshot could not be created.");
    }

    public async Task<ToolDirectiveSnapshot?> GetDirectiveSnapshotAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ToolDirectiveId, ToolKey, Name, SystemPrompt, TargetSchemaJson,
                   UserAdditiveDirective, Version, ContentHash, CreatedUtc
            FROM dbo.ToolDirectiveSnapshots
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? MapDirectiveSnapshot(reader)
            : null;
    }

    public async Task<ToolUserDirectiveSnapshot?> GetOrCreateUserDirectiveSnapshotAsync(
        ToolUserDirectiveSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(snapshot.UserAdditiveDirective))
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.ToolUserDirectiveSnapshots
                (ToolKey, UserEmail, UserAdditiveDirective, ContentHash, CreatedUtc)
            SELECT @ToolKey, @UserEmail, @UserAdditiveDirective, @ContentHash, SYSUTCDATETIME()
            WHERE NOT EXISTS
                (
                    SELECT 1
                    FROM dbo.ToolUserDirectiveSnapshots WITH (UPDLOCK, HOLDLOCK)
                    WHERE ContentHash = @ContentHash
                );

            SELECT TOP (1) Id, ToolKey, UserEmail, UserAdditiveDirective, ContentHash, CreatedUtc
            FROM dbo.ToolUserDirectiveSnapshots
            WHERE ContentHash = @ContentHash;
            """;
        AddUserDirectiveSnapshotParameters(command, snapshot);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return MapUserDirectiveSnapshot(reader);
        }

        throw new InvalidOperationException("The user directive snapshot could not be created.");
    }

    public async Task<ToolUserDirectiveSnapshot?> GetUserDirectiveSnapshotAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ToolKey, UserEmail, UserAdditiveDirective, ContentHash, CreatedUtc
            FROM dbo.ToolUserDirectiveSnapshots
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? MapUserDirectiveSnapshot(reader)
            : null;
    }

    public async Task<int> CreateToolJobAsync(ToolJob job, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.ToolJobs
                (ToolKey, JobType, UserEmail, TenantId, IsPublic, ToolDirectiveSnapshotId, ToolUserDirectiveSnapshotId, Status, RequestJson, ResultJson,
                 ErrorMessage, CreatedUtc, StartedUtc, CompletedUtc)
            OUTPUT INSERTED.Id
            VALUES
                (@ToolKey, @JobType, @UserEmail, @TenantId, @IsPublic, @ToolDirectiveSnapshotId, @ToolUserDirectiveSnapshotId, @Status, @RequestJson, @ResultJson,
                 @ErrorMessage, SYSUTCDATETIME(), @StartedUtc, @CompletedUtc);
            """;
        AddToolJobParameters(command, job);
        return (int)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task UpdateToolJobAsync(ToolJob job, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.ToolJobs
            SET Status = @Status,
                IsPublic = @IsPublic,
                ResultJson = @ResultJson,
                ErrorMessage = @ErrorMessage,
                StartedUtc = @StartedUtc,
                CompletedUtc = @CompletedUtc
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", job.Id);
        AddToolJobParameters(command, job);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ToolJob?> GetToolJobByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ToolKey, JobType, UserEmail, TenantId, IsPublic, ToolDirectiveSnapshotId, ToolUserDirectiveSnapshotId, Status, RequestJson, ResultJson,
                   ErrorMessage, CreatedUtc, StartedUtc, CompletedUtc
            FROM dbo.ToolJobs
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? MapToolJob(reader)
            : null;
    }

    public async Task<ToolJob?> GetToolJobAsync(int id, string userEmail, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ToolKey, JobType, UserEmail, TenantId, IsPublic, ToolDirectiveSnapshotId, ToolUserDirectiveSnapshotId, Status, RequestJson, ResultJson,
                   ErrorMessage, CreatedUtc, StartedUtc, CompletedUtc
            FROM dbo.ToolJobs
            WHERE Id = @Id
                AND UPPER(UserEmail) = @UserEmailNormalized;
            """;
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@UserEmailNormalized", userEmail.Trim().ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? MapToolJob(reader)
            : null;
    }

    public async Task<ToolJob?> GetVisibleToolJobAsync(int id, string userEmail, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ToolKey, JobType, UserEmail, TenantId, IsPublic, ToolDirectiveSnapshotId, ToolUserDirectiveSnapshotId, Status, RequestJson, ResultJson,
                   ErrorMessage, CreatedUtc, StartedUtc, CompletedUtc
            FROM dbo.ToolJobs
            WHERE Id = @Id
                AND (UPPER(UserEmail) = @UserEmailNormalized OR IsPublic = 1);
            """;
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@UserEmailNormalized", userEmail.Trim().ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? MapToolJob(reader)
            : null;
    }

    public async Task<IReadOnlyList<ToolJob>> GetActiveToolJobsAsync(string userEmail, CancellationToken cancellationToken = default)
    {
        var jobs = new List<ToolJob>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (25) Id, ToolKey, JobType, UserEmail, TenantId, IsPublic, ToolDirectiveSnapshotId, ToolUserDirectiveSnapshotId, Status, RequestJson, ResultJson,
                   ErrorMessage, CreatedUtc, StartedUtc, CompletedUtc
            FROM dbo.ToolJobs
            WHERE UPPER(UserEmail) = @UserEmailNormalized
                AND Status IN ('Queued', 'Processing', 'CancelRequested')
            ORDER BY CreatedUtc DESC;
            """;
        command.Parameters.AddWithValue("@UserEmailNormalized", userEmail.Trim().ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(MapToolJob(reader));
        }

        return jobs;
    }

    public async Task<IReadOnlyList<ToolJob>> GetActiveToolJobsForToolAsync(string toolKey, CancellationToken cancellationToken = default)
    {
        var jobs = new List<ToolJob>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (100) Id, ToolKey, JobType, UserEmail, TenantId, IsPublic, ToolDirectiveSnapshotId, ToolUserDirectiveSnapshotId, Status, RequestJson, ResultJson,
                   ErrorMessage, CreatedUtc, StartedUtc, CompletedUtc
            FROM dbo.ToolJobs
            WHERE ToolKey = @ToolKey
                AND Status IN ('Queued', 'Processing', 'CancelRequested')
            ORDER BY CreatedUtc;
            """;
        command.Parameters.AddWithValue("@ToolKey", toolKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(MapToolJob(reader));
        }

        return jobs;
    }

    public async Task<ToolJob?> GetActiveToolJobByRequestFingerprintAsync(
        string userEmail,
        string toolKey,
        string requestFingerprint,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1) Id, ToolKey, JobType, UserEmail, TenantId, IsPublic, ToolDirectiveSnapshotId, ToolUserDirectiveSnapshotId, Status, RequestJson, ResultJson,
                   ErrorMessage, CreatedUtc, StartedUtc, CompletedUtc
            FROM dbo.ToolJobs
            WHERE UPPER(UserEmail) = @UserEmailNormalized
                AND ToolKey = @ToolKey
                AND Status IN ('Queued', 'Processing', 'CancelRequested')
                AND JSON_VALUE(RequestJson, '$.requestFingerprint') = @RequestFingerprint
            ORDER BY CreatedUtc DESC;
            """;
        command.Parameters.AddWithValue("@UserEmailNormalized", userEmail.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("@ToolKey", toolKey);
        command.Parameters.AddWithValue("@RequestFingerprint", requestFingerprint);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? MapToolJob(reader)
            : null;
    }

    public async Task<int> CreateProductParseJobAsync(ProductParseJob job, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.PdfParseJobs
                (ToolJobId, UserEmail, TenantId, IsPublic, ToolDirectiveId, ToolDirectiveSnapshotId, ToolUserDirectiveSnapshotId, AiProviderId, OriginalFileName, StoredFilePath,
                 FileSizeBytes, Status, ErrorMessage, InputTokens, OutputTokens, DurationMs, CreatedUtc, CompletedUtc)
            OUTPUT INSERTED.Id
            VALUES
                (@ToolJobId, @UserEmail, @TenantId, @IsPublic, @ToolDirectiveId, @ToolDirectiveSnapshotId, @ToolUserDirectiveSnapshotId, @AiProviderId, @OriginalFileName, @StoredFilePath,
                 @FileSizeBytes, @Status, @ErrorMessage, @InputTokens, @OutputTokens, @DurationMs,
                 SYSUTCDATETIME(), @CompletedUtc);
            """;
        AddProductJobParameters(command, job);
        return (int)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task UpdateProductParseJobAsync(ProductParseJob job, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.PdfParseJobs
            SET Status = @Status,
                IsPublic = @IsPublic,
                ErrorMessage = @ErrorMessage,
                InputTokens = @InputTokens,
                OutputTokens = @OutputTokens,
                DurationMs = @DurationMs,
                CompletedUtc = @CompletedUtc
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", job.Id);
        AddProductJobParameters(command, job);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ProductParseJob?> UpdateProductParseJobVisibilityAsync(
        int id,
        string userEmail,
        bool isPublic,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = userEmail.Trim().ToUpperInvariant();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using var lookupCommand = connection.CreateCommand();
        lookupCommand.Transaction = (SqlTransaction)transaction;
        lookupCommand.CommandText = """
            SELECT ToolJobId
            FROM dbo.PdfParseJobs
            WHERE Id = @Id
                AND UPPER(UserEmail) = @UserEmailNormalized;
            """;
        lookupCommand.Parameters.AddWithValue("@Id", id);
        lookupCommand.Parameters.AddWithValue("@UserEmailNormalized", normalizedEmail);
        var toolJobValue = await lookupCommand.ExecuteScalarAsync(cancellationToken);
        if (toolJobValue is null)
        {
            return null;
        }

        var toolJobId = toolJobValue is DBNull ? 0 : (int)toolJobValue;
        if (toolJobId > 0)
        {
            await using var updateBatchCommand = connection.CreateCommand();
            updateBatchCommand.Transaction = (SqlTransaction)transaction;
            updateBatchCommand.CommandText = """
                UPDATE dbo.PdfParseJobs
                SET IsPublic = @IsPublic
                WHERE ToolJobId = @ToolJobId
                    AND UPPER(UserEmail) = @UserEmailNormalized;

                UPDATE dbo.ToolJobs
                SET IsPublic = @IsPublic
                WHERE Id = @ToolJobId
                    AND UPPER(UserEmail) = @UserEmailNormalized;

                UPDATE items
                SET IsPublic = @IsPublic,
                    UpdatedUtc = SYSUTCDATETIME()
                FROM dbo.ProductLibraryItems items
                WHERE UPPER(items.UserEmail) = @UserEmailNormalized
                    AND EXISTS (
                        SELECT 1
                        FROM dbo.ProductLibraryDocuments documents
                        INNER JOIN dbo.PdfParseJobs jobs ON jobs.Id = documents.PdfParseJobId
                        WHERE documents.ProductLibraryItemId = items.Id
                            AND jobs.ToolJobId = @ToolJobId
                    );
                """;
            updateBatchCommand.Parameters.AddWithValue("@IsPublic", isPublic);
            updateBatchCommand.Parameters.AddWithValue("@ToolJobId", toolJobId);
            updateBatchCommand.Parameters.AddWithValue("@UserEmailNormalized", normalizedEmail);
            await updateBatchCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            await using var updateJobCommand = connection.CreateCommand();
            updateJobCommand.Transaction = (SqlTransaction)transaction;
            updateJobCommand.CommandText = """
                UPDATE dbo.PdfParseJobs
                SET IsPublic = @IsPublic
                WHERE Id = @Id
                    AND UPPER(UserEmail) = @UserEmailNormalized;

                UPDATE items
                SET IsPublic = @IsPublic,
                    UpdatedUtc = SYSUTCDATETIME()
                FROM dbo.ProductLibraryItems items
                WHERE UPPER(items.UserEmail) = @UserEmailNormalized
                    AND EXISTS (
                        SELECT 1
                        FROM dbo.ProductLibraryDocuments documents
                        WHERE documents.ProductLibraryItemId = items.Id
                            AND documents.PdfParseJobId = @Id
                    );
                """;
            updateJobCommand.Parameters.AddWithValue("@IsPublic", isPublic);
            updateJobCommand.Parameters.AddWithValue("@Id", id);
            updateJobCommand.Parameters.AddWithValue("@UserEmailNormalized", normalizedEmail);
            await updateJobCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await GetProductParseJobAsync(id, userEmail, cancellationToken);
    }

    public async Task<ProductParseJob?> GetProductParseJobAsync(int id, string userEmail, CancellationToken cancellationToken = default)
    {
        var jobs = await GetProductParseJobsAsync(userEmail, "all", cancellationToken);
        return jobs.FirstOrDefault(job => job.Id == id);
    }

    public async Task<IReadOnlyList<ProductParseJob>> GetProductParseJobsForToolJobAsync(
        int toolJobId,
        string userEmail,
        CancellationToken cancellationToken = default)
    {
        var jobs = new List<ProductParseJob>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT jobs.Id, jobs.ToolJobId, jobs.UserEmail, jobs.TenantId, jobs.IsPublic,
                   jobs.ToolDirectiveId, jobs.ToolDirectiveSnapshotId, jobs.ToolUserDirectiveSnapshotId, jobs.AiProviderId, jobs.OriginalFileName, jobs.StoredFilePath,
                   jobs.FileSizeBytes, jobs.Status, jobs.ErrorMessage, jobs.InputTokens, jobs.OutputTokens,
                   jobs.DurationMs, jobs.CreatedUtc, jobs.CompletedUtc,
                   ap.DisplayName AS AiProviderName,
                   ap.ModelName AS AiProviderModelName,
                   COALESCE(snapshots.Name, directives.Name) AS DirectiveName,
                   COALESCE(snapshots.Version, directives.Version) AS DirectiveVersion,
                   snapshots.ContentHash AS DirectiveSnapshotHash,
                   COUNT(pp.Id) AS ParsedProductCount,
                   STRING_AGG(COALESCE(pp.ProductName, pp.ProductCode, CONCAT('Product ', pp.Id)), ', ') AS ParsedProductNames
            FROM dbo.PdfParseJobs jobs
            LEFT JOIN dbo.AiProviders ap ON ap.Id = jobs.AiProviderId
            LEFT JOIN dbo.ToolDirectives directives ON directives.Id = jobs.ToolDirectiveId
            LEFT JOIN dbo.ToolDirectiveSnapshots snapshots ON snapshots.Id = jobs.ToolDirectiveSnapshotId
            LEFT JOIN dbo.ParsedProducts pp ON pp.PdfParseJobId = jobs.Id
            WHERE jobs.ToolJobId = @ToolJobId
                AND (UPPER(jobs.UserEmail) = @UserEmailNormalized OR jobs.IsPublic = 1)
            GROUP BY jobs.Id, jobs.ToolJobId, jobs.UserEmail, jobs.TenantId, jobs.IsPublic,
                     jobs.ToolDirectiveId, jobs.ToolDirectiveSnapshotId, jobs.ToolUserDirectiveSnapshotId, jobs.AiProviderId, jobs.OriginalFileName, jobs.StoredFilePath,
                     jobs.FileSizeBytes, jobs.Status, jobs.ErrorMessage, jobs.InputTokens, jobs.OutputTokens,
                     jobs.DurationMs, jobs.CreatedUtc, jobs.CompletedUtc, ap.DisplayName, ap.ModelName,
                     directives.Name, directives.Version, snapshots.Name, snapshots.Version, snapshots.ContentHash
            ORDER BY jobs.Id;
            """;
        command.Parameters.AddWithValue("@ToolJobId", toolJobId);
        command.Parameters.AddWithValue("@UserEmailNormalized", userEmail.Trim().ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(MapProductJob(reader));
        }

        return jobs;
    }

    public async Task<IReadOnlyList<ProductParseJob>> GetProductParseJobsAsync(string userEmail, string scope, CancellationToken cancellationToken = default)
    {
        var jobs = new List<ProductParseJob>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (100) jobs.Id, jobs.ToolJobId, jobs.UserEmail, jobs.TenantId, jobs.IsPublic,
                   jobs.ToolDirectiveId, jobs.ToolDirectiveSnapshotId, jobs.ToolUserDirectiveSnapshotId, jobs.AiProviderId, jobs.OriginalFileName, jobs.StoredFilePath,
                   jobs.FileSizeBytes, jobs.Status, jobs.ErrorMessage, jobs.InputTokens, jobs.OutputTokens,
                   jobs.DurationMs, jobs.CreatedUtc, jobs.CompletedUtc,
                   ap.DisplayName AS AiProviderName,
                   ap.ModelName AS AiProviderModelName,
                   COALESCE(snapshots.Name, directives.Name) AS DirectiveName,
                   COALESCE(snapshots.Version, directives.Version) AS DirectiveVersion,
                   snapshots.ContentHash AS DirectiveSnapshotHash,
                   COUNT(pp.Id) AS ParsedProductCount,
                   STRING_AGG(COALESCE(pp.ProductName, pp.ProductCode, CONCAT('Product ', pp.Id)), ', ') AS ParsedProductNames
            FROM dbo.PdfParseJobs jobs
            LEFT JOIN dbo.AiProviders ap ON ap.Id = jobs.AiProviderId
            LEFT JOIN dbo.ToolDirectives directives ON directives.Id = jobs.ToolDirectiveId
            LEFT JOIN dbo.ToolDirectiveSnapshots snapshots ON snapshots.Id = jobs.ToolDirectiveSnapshotId
            LEFT JOIN dbo.ParsedProducts pp ON pp.PdfParseJobId = jobs.Id
            WHERE
                (@Scope = 'mine' AND UPPER(jobs.UserEmail) = @UserEmailNormalized)
                OR (@Scope = 'public' AND jobs.IsPublic = 1)
                OR (@Scope = 'all' AND (UPPER(jobs.UserEmail) = @UserEmailNormalized OR jobs.IsPublic = 1))
            GROUP BY jobs.Id, jobs.ToolJobId, jobs.UserEmail, jobs.TenantId, jobs.IsPublic,
                     jobs.ToolDirectiveId, jobs.ToolDirectiveSnapshotId, jobs.ToolUserDirectiveSnapshotId, jobs.AiProviderId, jobs.OriginalFileName, jobs.StoredFilePath,
                     jobs.FileSizeBytes, jobs.Status, jobs.ErrorMessage, jobs.InputTokens, jobs.OutputTokens,
                     jobs.DurationMs, jobs.CreatedUtc, jobs.CompletedUtc, ap.DisplayName, ap.ModelName,
                     directives.Name, directives.Version, snapshots.Name, snapshots.Version, snapshots.ContentHash
            ORDER BY jobs.CreatedUtc DESC;
            """;
        command.Parameters.AddWithValue("@UserEmailNormalized", userEmail.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("@Scope", NormalizeScope(scope));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(MapProductJob(reader));
        }

        return jobs;
    }

    public async Task<ProductParseJobHistoryResult> GetProductParseJobHistoryAsync(
        string userEmail,
        string scope,
        int page,
        int pageSize,
        string? sortBy,
        string? sortDirection,
        CancellationToken cancellationToken = default)
    {
        var normalizedScope = NormalizeScope(scope);
        var normalizedSortBy = NormalizeJobHistorySortBy(sortBy);
        var normalizedSortDirection = string.Equals(sortDirection, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
        var normalizedPageSize = Math.Clamp(pageSize, 10, 100);
        var normalizedPage = Math.Max(page, 1);
        var offset = (normalizedPage - 1) * normalizedPageSize;
        var items = new List<ProductParseJobHistoryItem>();
        var orderBy = BuildJobHistoryOrderBy(normalizedSortBy, normalizedSortDirection);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = """
            SELECT COUNT(1)
            FROM dbo.ToolJobs toolJobs
            WHERE toolJobs.ToolKey = @ToolKey
                AND (
                    (@Scope = 'mine' AND UPPER(toolJobs.UserEmail) = @UserEmailNormalized)
                    OR (@Scope = 'public' AND toolJobs.IsPublic = 1)
                    OR (@Scope = 'all' AND (UPPER(toolJobs.UserEmail) = @UserEmailNormalized OR toolJobs.IsPublic = 1))
                );
            """;
        countCommand.Parameters.AddWithValue("@ToolKey", "ProductParser");
        countCommand.Parameters.AddWithValue("@UserEmailNormalized", userEmail.Trim().ToUpperInvariant());
        countCommand.Parameters.AddWithValue("@Scope", normalizedScope);
        var totalCount = (int)await countCommand.ExecuteScalarAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH ProductSummary AS
            (
                SELECT jobs.ToolJobId,
                       MIN(products.Id) AS FirstParsedProductId,
                       COUNT(DISTINCT products.Id) AS ProductCount,
                       STRING_AGG(COALESCE(products.ProductName, products.ProductCode, CONCAT('Product ', products.Id)), ', ') AS ProductNames
                FROM dbo.PdfParseJobs jobs
                LEFT JOIN dbo.ParsedProducts products ON products.PdfParseJobId = jobs.Id
                GROUP BY jobs.ToolJobId
            ),
            FileSummary AS
            (
                SELECT ToolJobId,
                       COUNT(1) AS FileCount,
                       MIN(Id) AS FirstProductParseJobId,
                       STRING_AGG(OriginalFileName, ', ') AS OriginalFileNames,
                       MAX(DurationMs) AS DurationMs,
                       SUM(InputTokens) AS InputTokens,
                       SUM(OutputTokens) AS OutputTokens,
                       STRING_AGG(CAST(NULLIF(ErrorMessage, '') AS NVARCHAR(MAX)), CHAR(10)) AS ErrorMessage,
                       MAX(AiProviderId) AS AiProviderId,
                       MAX(ToolDirectiveId) AS ToolDirectiveId,
                       MAX(ToolDirectiveSnapshotId) AS ToolDirectiveSnapshotId
                FROM dbo.PdfParseJobs
                GROUP BY ToolJobId
            )
            SELECT toolJobs.Id AS ToolJobId,
                   fileSummary.FirstProductParseJobId,
                   productSummary.FirstParsedProductId,
                   toolJobs.UserEmail,
                   toolJobs.IsPublic,
                   toolJobs.Status,
                   toolJobs.CreatedUtc,
                   toolJobs.CompletedUtc,
                   COALESCE(fileSummary.FileCount, 0) AS FileCount,
                   COALESCE(productSummary.ProductCount, 0) AS ProductCount,
                   productSummary.ProductNames,
                   fileSummary.OriginalFileNames,
                   COALESCE(snapshots.Name, directives.Name) AS DirectiveName,
                   COALESCE(snapshots.Version, directives.Version) AS DirectiveVersion,
                   providers.DisplayName AS AiProviderName,
                   providers.ModelName AS AiProviderModelName,
                   fileSummary.InputTokens,
                   fileSummary.OutputTokens,
                   fileSummary.DurationMs,
                   COALESCE(toolJobs.ErrorMessage, fileSummary.ErrorMessage) AS ErrorMessage
            FROM dbo.ToolJobs toolJobs
            LEFT JOIN FileSummary fileSummary ON fileSummary.ToolJobId = toolJobs.Id
            LEFT JOIN ProductSummary productSummary ON productSummary.ToolJobId = toolJobs.Id
            LEFT JOIN dbo.ToolDirectiveSnapshots snapshots ON snapshots.Id = COALESCE(toolJobs.ToolDirectiveSnapshotId, fileSummary.ToolDirectiveSnapshotId)
            LEFT JOIN dbo.ToolDirectives directives ON directives.Id = fileSummary.ToolDirectiveId
            LEFT JOIN dbo.AiProviders providers ON providers.Id = fileSummary.AiProviderId
            WHERE toolJobs.ToolKey = @ToolKey
                AND (
                    (@Scope = 'mine' AND UPPER(toolJobs.UserEmail) = @UserEmailNormalized)
                    OR (@Scope = 'public' AND toolJobs.IsPublic = 1)
                    OR (@Scope = 'all' AND (UPPER(toolJobs.UserEmail) = @UserEmailNormalized OR toolJobs.IsPublic = 1))
                )
            ORDER BY {orderBy}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;
        command.Parameters.AddWithValue("@ToolKey", "ProductParser");
        command.Parameters.AddWithValue("@UserEmailNormalized", userEmail.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("@Scope", normalizedScope);
        command.Parameters.AddWithValue("@Offset", offset);
        command.Parameters.AddWithValue("@PageSize", normalizedPageSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(MapProductParseJobHistoryItem(reader));
        }

        return new ProductParseJobHistoryResult
        {
            Items = items,
            TotalCount = totalCount,
            Page = normalizedPage,
            PageSize = normalizedPageSize,
            SortBy = normalizedSortBy,
            SortDirection = normalizedSortDirection
        };
    }

    public async Task<int> CreateParsedProductAsync(ParsedProduct product, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.ParsedProducts
                (PdfParseJobId, UserEmail, ProductDataJson, ProductName, ProductCode, IsSentToZoho, CreatedUtc, UpdatedUtc)
            OUTPUT INSERTED.Id
            VALUES
                (@ProductParseJobId, @UserEmail, @ProductDataJson, @ProductName, @ProductCode, @IsSentToZoho,
                 SYSUTCDATETIME(), SYSUTCDATETIME());
            """;
        AddParsedProductParameters(command, product);
        return (int)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task<ParsedProduct?> GetParsedProductAsync(int id, string userEmail, CancellationToken cancellationToken = default)
    {
        return (await GetParsedProductsAsync(userEmail, null, cancellationToken)).FirstOrDefault(product => product.Id == id);
    }

    public async Task<ParsedProductDetail?> GetParsedProductDetailAsync(int id, string userEmail, CancellationToken cancellationToken = default)
    {
        var product = await GetParsedProductAsync(id, userEmail, cancellationToken);
        if (product is null)
        {
            return null;
        }

        return new ParsedProductDetail
        {
            Product = product,
            Job = await GetProductParseJobAsync(product.ProductParseJobId, userEmail, cancellationToken),
            Versions = await GetParsedProductVersionsAsync(id, userEmail, cancellationToken)
        };
    }

    public async Task<IReadOnlyList<ParsedProduct>> GetParsedProductsAsync(string userEmail, string? search, CancellationToken cancellationToken = default)
    {
        var products = new List<ParsedProduct>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (100) Id, PdfParseJobId AS ProductParseJobId, UserEmail, ProductDataJson, ProductName, ProductCode,
                   IsSentToZoho, CreatedUtc, UpdatedUtc
            FROM dbo.ParsedProducts pp
            WHERE EXISTS
                (
                    SELECT 1
                    FROM dbo.PdfParseJobs jobs
                    WHERE jobs.Id = pp.PdfParseJobId
                        AND (UPPER(jobs.UserEmail) = @UserEmailNormalized OR jobs.IsPublic = 1)
                )
                AND (
                    @Search IS NULL
                    OR pp.ProductName LIKE @SearchLike
                    OR pp.ProductCode LIKE @SearchLike
                    OR pp.ProductDataJson LIKE @SearchLike
                )
            ORDER BY pp.CreatedUtc DESC;
            """;
        command.Parameters.AddWithValue("@UserEmailNormalized", userEmail.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("@Search", string.IsNullOrWhiteSpace(search) ? DBNull.Value : search.Trim());
        command.Parameters.AddWithValue("@SearchLike", string.IsNullOrWhiteSpace(search) ? DBNull.Value : $"%{search.Trim()}%");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            products.Add(MapParsedProduct(reader));
        }

        return products;
    }

    public async Task<IReadOnlyList<ParsedProduct>> GetParsedProductsForToolJobAsync(
        int toolJobId,
        string userEmail,
        CancellationToken cancellationToken = default)
    {
        var products = new List<ParsedProduct>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT pp.Id, pp.PdfParseJobId AS ProductParseJobId, pp.UserEmail, pp.ProductDataJson, pp.ProductName, pp.ProductCode,
                   pp.IsSentToZoho, pp.CreatedUtc, pp.UpdatedUtc
            FROM dbo.ParsedProducts pp
            INNER JOIN dbo.PdfParseJobs jobs ON jobs.Id = pp.PdfParseJobId
            WHERE jobs.ToolJobId = @ToolJobId
                AND (UPPER(jobs.UserEmail) = @UserEmailNormalized OR jobs.IsPublic = 1)
            ORDER BY COALESCE(pp.ProductCode, pp.ProductName), pp.Id;
            """;
        command.Parameters.AddWithValue("@ToolJobId", toolJobId);
        command.Parameters.AddWithValue("@UserEmailNormalized", userEmail.Trim().ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            products.Add(MapParsedProduct(reader));
        }

        return products;
    }

    public async Task<IReadOnlyList<ParsedProduct>> GetParsedProductVersionsAsync(int parsedProductId, string userEmail, CancellationToken cancellationToken = default)
    {
        var versions = new List<ParsedProduct>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @ProductCode NVARCHAR(160);
            DECLARE @ProductName NVARCHAR(300);

            SELECT @ProductCode = NULLIF(LTRIM(RTRIM(ProductCode)), ''),
                   @ProductName = NULLIF(LTRIM(RTRIM(ProductName)), '')
            FROM dbo.ParsedProducts
            WHERE Id = @ParsedProductId;

            SELECT TOP (25) pp.Id, pp.PdfParseJobId AS ProductParseJobId, pp.UserEmail, pp.ProductDataJson, pp.ProductName, pp.ProductCode,
                   pp.IsSentToZoho, pp.CreatedUtc, pp.UpdatedUtc
            FROM dbo.ParsedProducts pp
            INNER JOIN dbo.PdfParseJobs jobs ON jobs.Id = pp.PdfParseJobId
            WHERE (UPPER(jobs.UserEmail) = @UserEmailNormalized OR jobs.IsPublic = 1)
                AND (
                    (@ProductCode IS NOT NULL AND pp.ProductCode = @ProductCode)
                    OR (@ProductCode IS NULL AND @ProductName IS NOT NULL AND pp.ProductName = @ProductName)
                    OR (@ProductCode IS NULL AND @ProductName IS NULL AND pp.Id = @ParsedProductId)
                )
            ORDER BY pp.CreatedUtc DESC;
            """;
        command.Parameters.AddWithValue("@ParsedProductId", parsedProductId);
        command.Parameters.AddWithValue("@UserEmailNormalized", userEmail.Trim().ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            versions.Add(MapParsedProduct(reader));
        }

        return versions;
    }

    public async Task<bool> UpdateParsedProductAsync(ParsedProduct product, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.ParsedProducts
            SET ProductDataJson = @ProductDataJson,
                ProductName = @ProductName,
                ProductCode = @ProductCode,
                UpdatedUtc = SYSUTCDATETIME()
            WHERE Id = @Id
                AND UPPER(UserEmail) = @UserEmailNormalized;
            """;
        command.Parameters.AddWithValue("@Id", product.Id);
        command.Parameters.AddWithValue("@UserEmailNormalized", product.UserEmail.Trim().ToUpperInvariant());
        AddNVarCharMax(command, "@ProductDataJson", product.ProductDataJson);
        command.Parameters.AddWithValue("@ProductName", (object?)product.ProductName ?? DBNull.Value);
        command.Parameters.AddWithValue("@ProductCode", (object?)product.ProductCode ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<int> CreateProductLibraryItemAsync(
        ProductLibraryItem item,
        IReadOnlyList<int> productParseJobIds,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = (SqlTransaction)transaction;
        command.CommandText = """
            INSERT INTO dbo.ProductLibraryItems
                (UserEmail, IsPublic, PrimaryParsedProductId, ProductDataJson, ProductName, ProductCode,
                 Supplier, Manufacturer, Category, ProductValue, Currency, SourceSummary, IsSentToZoho,
                 CreatedUtc, UpdatedUtc)
            OUTPUT INSERTED.Id
            VALUES
                (@UserEmail, @IsPublic, @PrimaryParsedProductId, @ProductDataJson, @ProductName, @ProductCode,
                 @Supplier, @Manufacturer, @Category, @ProductValue, @Currency, @SourceSummary, @IsSentToZoho,
                 SYSUTCDATETIME(), SYSUTCDATETIME());
            """;
        AddProductLibraryItemParameters(command, item);
        var itemId = (int)await command.ExecuteScalarAsync(cancellationToken);

        foreach (var productParseJobId in productParseJobIds.Distinct())
        {
            await using var linkCommand = connection.CreateCommand();
            linkCommand.Transaction = (SqlTransaction)transaction;
            linkCommand.CommandText = """
                INSERT INTO dbo.ProductLibraryDocuments (ProductLibraryItemId, PdfParseJobId, CreatedUtc)
                VALUES (@ProductLibraryItemId, @ProductParseJobId, SYSUTCDATETIME());
                """;
            linkCommand.Parameters.AddWithValue("@ProductLibraryItemId", itemId);
            linkCommand.Parameters.AddWithValue("@ProductParseJobId", productParseJobId);
            await linkCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return itemId;
    }

    public async Task<ProductLibraryItem?> GetProductLibraryItemAsync(int id, string userEmail, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, UserEmail, IsPublic, PrimaryParsedProductId, ProductDataJson, ProductName,
                   ProductCode, Supplier, Manufacturer, Category, ProductValue, Currency, SourceSummary,
                   IsSentToZoho, CreatedUtc, UpdatedUtc
            FROM dbo.ProductLibraryItems
            WHERE Id = @Id
                AND (UPPER(UserEmail) = @UserEmailNormalized OR IsPublic = 1);
            """;
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@UserEmailNormalized", userEmail.Trim().ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapProductLibraryItem(reader) : null;
    }

    public async Task<IReadOnlyList<ProductLibraryItem>> GetProductLibraryItemsAsync(
        string userEmail,
        ProductLibraryFilter filter,
        CancellationToken cancellationToken = default)
    {
        var items = new List<ProductLibraryItem>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (200) Id, UserEmail, IsPublic, PrimaryParsedProductId, ProductDataJson, ProductName,
                   ProductCode, Supplier, Manufacturer, Category, ProductValue, Currency, SourceSummary,
                   IsSentToZoho, CreatedUtc, UpdatedUtc
            FROM dbo.ProductLibraryItems
            WHERE
                (
                    (@Scope = 'mine' AND UPPER(UserEmail) = @UserEmailNormalized)
                    OR (@Scope = 'public' AND IsPublic = 1)
                    OR (@Scope = 'all' AND (UPPER(UserEmail) = @UserEmailNormalized OR IsPublic = 1))
                )
                AND (@Search IS NULL
                    OR ProductName LIKE @SearchLike
                    OR ProductCode LIKE @SearchLike
                    OR Supplier LIKE @SearchLike
                    OR Manufacturer LIKE @SearchLike
                    OR Category LIKE @SearchLike
                    OR ProductDataJson LIKE @SearchLike)
                AND (@Supplier IS NULL OR Supplier = @Supplier)
                AND (@Manufacturer IS NULL OR Manufacturer = @Manufacturer)
                AND (@Category IS NULL OR Category = @Category)
                AND (@MinValue IS NULL OR ProductValue >= @MinValue)
                AND (@MaxValue IS NULL OR ProductValue <= @MaxValue)
            ORDER BY CreatedUtc DESC;
            """;
        AddProductLibraryFilterParameters(command, userEmail, filter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(MapProductLibraryItem(reader));
        }

        return items;
    }

    public async Task<IReadOnlyList<ProductLibraryGroup>> GetProductLibraryGroupsAsync(
        string userEmail,
        ProductLibraryFilter filter,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<ProductLibraryGroupRow>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (500)
                   items.Id AS ProductLibraryItemId,
                   items.UserEmail,
                   items.IsPublic,
                   items.PrimaryParsedProductId,
                   items.ProductName,
                   items.ProductCode,
                   items.Supplier,
                   items.Manufacturer,
                   items.Category,
                   items.ProductValue,
                   items.Currency,
                   items.CreatedUtc,
                   jobs.ToolJobId,
                   jobs.Status,
                   jobs.OriginalFileName,
                   COALESCE(snapshots.Name, directives.Name) AS DirectiveName,
                   COALESCE(snapshots.Version, directives.Version) AS DirectiveVersion
            FROM dbo.ProductLibraryItems items
            LEFT JOIN dbo.ProductLibraryDocuments documents ON documents.ProductLibraryItemId = items.Id
            LEFT JOIN dbo.PdfParseJobs jobs ON jobs.Id = documents.PdfParseJobId
            LEFT JOIN dbo.ToolDirectiveSnapshots snapshots ON snapshots.Id = jobs.ToolDirectiveSnapshotId
            LEFT JOIN dbo.ToolDirectives directives ON directives.Id = jobs.ToolDirectiveId
            WHERE
                (
                    (@Scope = 'mine' AND UPPER(items.UserEmail) = @UserEmailNormalized)
                    OR (@Scope = 'public' AND items.IsPublic = 1)
                    OR (@Scope = 'all' AND (UPPER(items.UserEmail) = @UserEmailNormalized OR items.IsPublic = 1))
                )
                AND (@Search IS NULL
                    OR items.ProductName LIKE @SearchLike
                    OR items.ProductCode LIKE @SearchLike
                    OR items.Supplier LIKE @SearchLike
                    OR items.Manufacturer LIKE @SearchLike
                    OR items.Category LIKE @SearchLike)
                AND (@Supplier IS NULL OR items.Supplier = @Supplier)
                AND (@Manufacturer IS NULL OR items.Manufacturer = @Manufacturer)
                AND (@Category IS NULL OR items.Category = @Category)
                AND (@MinValue IS NULL OR items.ProductValue >= @MinValue)
                AND (@MaxValue IS NULL OR items.ProductValue <= @MaxValue)
            ORDER BY items.CreatedUtc DESC;
            """;
        AddProductLibraryFilterParameters(command, userEmail, filter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(MapProductLibraryGroupRow(reader));
        }

        return rows
            .GroupBy(row => new
            {
                ProductName = NormalizeLibraryKey(row.ProductName),
                ProductCode = NormalizeLibraryKey(row.ProductCode),
                Supplier = NormalizeLibraryKey(row.Supplier),
                Manufacturer = NormalizeLibraryKey(row.Manufacturer)
            })
            .Select(group =>
            {
                var orderedRows = group.OrderByDescending(row => row.CreatedUtc).ToList();
                var latest = orderedRows[0];
                return new ProductLibraryGroup
                {
                    ProductName = latest.ProductName,
                    ProductCode = latest.ProductCode,
                    Supplier = latest.Supplier,
                    Manufacturer = latest.Manufacturer,
                    Category = latest.Category,
                    ProductValue = latest.ProductValue,
                    Currency = latest.Currency,
                    RunCount = orderedRows.Count,
                    LastRunUtc = latest.CreatedUtc,
                    LatestParsedProductId = latest.PrimaryParsedProductId,
                    Runs = orderedRows.Select(row => new ProductLibraryRun
                    {
                        ProductLibraryItemId = row.ProductLibraryItemId,
                        ParsedProductId = row.PrimaryParsedProductId,
                        ToolJobId = row.ToolJobId,
                        UserEmail = row.UserEmail,
                        IsPublic = row.IsPublic,
                        CreatedUtc = row.CreatedUtc,
                        Status = row.Status,
                        OriginalFileName = row.OriginalFileName,
                        DirectiveName = row.DirectiveName,
                        DirectiveVersion = row.DirectiveVersion
                    }).ToList()
                };
            })
            .OrderByDescending(group => group.LastRunUtc)
            .ToList();
    }

    public async Task<IReadOnlyList<string>> GetProductLibraryFilterValuesAsync(
        string userEmail,
        string fieldName,
        CancellationToken cancellationToken = default)
    {
        var safeFieldName = fieldName switch
        {
            "Supplier" => "Supplier",
            "Manufacturer" => "Manufacturer",
            "Category" => "Category",
            _ => throw new ArgumentOutOfRangeException(nameof(fieldName), "Unsupported library filter field.")
        };
        var values = new List<string>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT DISTINCT {safeFieldName}
            FROM dbo.ProductLibraryItems
            WHERE (UPPER(UserEmail) = @UserEmailNormalized OR IsPublic = 1)
                AND {safeFieldName} IS NOT NULL
                AND LTRIM(RTRIM({safeFieldName})) <> ''
            ORDER BY {safeFieldName};
            """;
        command.Parameters.AddWithValue("@UserEmailNormalized", userEmail.Trim().ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    public async Task LogUsageAsync(ToolUsageLog usage, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.ToolUsageLog
                (UserEmail, TenantId, ToolKey, AiProviderId, RequestType, TokensIn, TokensOut,
                 DurationMs, Succeeded, ErrorMessage, EstimatedCostUsd, CreatedUtc)
            VALUES
                (@UserEmail, @TenantId, @ToolKey, @AiProviderId, @RequestType, @TokensIn, @TokensOut,
                 @DurationMs, @Succeeded, @ErrorMessage, @EstimatedCostUsd, SYSUTCDATETIME());
            """;
        command.Parameters.AddWithValue("@UserEmail", usage.UserEmail);
        command.Parameters.AddWithValue("@TenantId", (object?)usage.TenantId ?? DBNull.Value);
        command.Parameters.AddWithValue("@ToolKey", usage.ToolKey);
        command.Parameters.AddWithValue("@AiProviderId", usage.AiProviderId);
        command.Parameters.AddWithValue("@RequestType", usage.RequestType);
        AddNullableInt(command, "@TokensIn", usage.TokensIn);
        AddNullableInt(command, "@TokensOut", usage.TokensOut);
        command.Parameters.AddWithValue("@DurationMs", usage.DurationMs);
        command.Parameters.Add("@Succeeded", SqlDbType.Bit).Value = usage.Succeeded;
        AddNVarCharMax(command, "@ErrorMessage", usage.ErrorMessage);
        AddNullableDecimal(command, "@EstimatedCostUsd", usage.EstimatedCostUsd, 10, 4);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ToolUsageLog>> GetUsageAsync(string? userEmail, string? toolKey, CancellationToken cancellationToken = default)
    {
        var usage = new List<ToolUsageLog>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (250) Id, UserEmail, TenantId, ToolKey, AiProviderId, RequestType, TokensIn,
                   TokensOut, DurationMs, Succeeded, ErrorMessage, EstimatedCostUsd, CreatedUtc
            FROM dbo.ToolUsageLog
            WHERE (@UserEmail IS NULL OR UPPER(UserEmail) = @UserEmailNormalized)
                AND (@ToolKey IS NULL OR ToolKey = @ToolKey)
            ORDER BY CreatedUtc DESC;
            """;
        command.Parameters.AddWithValue("@UserEmail", string.IsNullOrWhiteSpace(userEmail) ? DBNull.Value : userEmail.Trim());
        command.Parameters.AddWithValue("@UserEmailNormalized", string.IsNullOrWhiteSpace(userEmail) ? DBNull.Value : userEmail.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("@ToolKey", string.IsNullOrWhiteSpace(toolKey) ? DBNull.Value : toolKey.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            usage.Add(new ToolUsageLog
            {
                Id = reader.GetRequiredInt32("Id"),
                UserEmail = reader.GetRequiredString("UserEmail"),
                TenantId = reader.GetNullableString("TenantId"),
                ToolKey = reader.GetRequiredString("ToolKey"),
                AiProviderId = reader.GetRequiredInt32("AiProviderId"),
                RequestType = reader.GetRequiredString("RequestType"),
                TokensIn = reader["TokensIn"] is DBNull ? null : reader.GetRequiredInt32("TokensIn"),
                TokensOut = reader["TokensOut"] is DBNull ? null : reader.GetRequiredInt32("TokensOut"),
                DurationMs = reader.GetRequiredInt32("DurationMs"),
                Succeeded = reader.GetRequiredBoolean("Succeeded"),
                ErrorMessage = reader.GetNullableString("ErrorMessage"),
                EstimatedCostUsd = reader["EstimatedCostUsd"] is DBNull ? null : (decimal)reader["EstimatedCostUsd"],
                CreatedUtc = reader.GetRequiredDateTime("CreatedUtc")
            });
        }

        return usage;
    }

    public async Task<IReadOnlyList<ModelPricing>> GetPricingAsync(CancellationToken cancellationToken = default)
    {
        var pricing = new List<ModelPricing>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ProviderType, ModelName, InputPricePer1kUsd, OutputPricePer1kUsd, EffectiveFromUtc, IsActive
            FROM dbo.ModelPricing
            ORDER BY IsActive DESC, ProviderType, ModelName;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            pricing.Add(MapPricing(reader));
        }

        return pricing;
    }

    public async Task AddPricingAsync(ModelPricing pricing, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var deactivateCommand = connection.CreateCommand();
        deactivateCommand.CommandText = """
            UPDATE dbo.ModelPricing
            SET IsActive = 0
            WHERE ProviderType = @ProviderType AND ModelName = @ModelName;
            """;
        deactivateCommand.Parameters.AddWithValue("@ProviderType", pricing.ProviderType.ToString());
        deactivateCommand.Parameters.AddWithValue("@ModelName", pricing.ModelName.Trim());
        await deactivateCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.ModelPricing
                (ProviderType, ModelName, InputPricePer1kUsd, OutputPricePer1kUsd, EffectiveFromUtc, IsActive)
            VALUES
                (@ProviderType, @ModelName, @InputPricePer1kUsd, @OutputPricePer1kUsd, SYSUTCDATETIME(), 1);
            """;
        command.Parameters.AddWithValue("@ProviderType", pricing.ProviderType.ToString());
        command.Parameters.AddWithValue("@ModelName", pricing.ModelName.Trim());
        AddNullableDecimal(command, "@InputPricePer1kUsd", pricing.InputPricePer1kUsd, 10, 6);
        AddNullableDecimal(command, "@OutputPricePer1kUsd", pricing.OutputPricePer1kUsd, 10, 6);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<decimal?> EstimateCostAsync(AiProvider provider, int? inputTokens, int? outputTokens, CancellationToken cancellationToken = default)
    {
        if (inputTokens is null && outputTokens is null)
        {
            return null;
        }

        var pricing = (await GetPricingAsync(cancellationToken))
            .FirstOrDefault(item => item.IsActive
                && item.ProviderType == provider.ProviderType
                && string.Equals(item.ModelName, provider.ModelName, StringComparison.OrdinalIgnoreCase));
        if (pricing is null)
        {
            return null;
        }

        var inputCost = (inputTokens.GetValueOrDefault() / 1000m) * pricing.InputPricePer1kUsd;
        var outputCost = (outputTokens.GetValueOrDefault() / 1000m) * pricing.OutputPricePer1kUsd;
        return Math.Round(inputCost + outputCost, 4);
    }

    private static ToolDefinition MapTool(SqlDataReader reader)
    {
        var accessMode = Enum.TryParse<ToolAccessMode>(reader.GetRequiredString("AccessMode"), out var parsed)
            ? parsed
            : ToolAccessMode.RequiresApproval;

        return new ToolDefinition
        {
            Id = reader.GetRequiredInt32("Id"),
            ToolKey = reader.GetRequiredString("ToolKey"),
            DisplayName = reader.GetRequiredString("DisplayName"),
            Description = reader.GetNullableString("Description"),
            IsEnabled = reader.GetRequiredBoolean("IsEnabled"),
            AccessMode = accessMode,
            CreatedUtc = reader.GetRequiredDateTime("CreatedUtc"),
            UpdatedUtc = reader.GetRequiredDateTime("UpdatedUtc"),
            ActiveProviderId = reader["ActiveProviderId"] is DBNull ? null : reader.GetRequiredInt32("ActiveProviderId"),
            ActiveProviderName = reader.GetNullableString("ActiveProviderName")
        };
    }

    private static ToolDirective MapDirective(SqlDataReader reader)
    {
        return new ToolDirective
        {
            Id = reader.GetRequiredInt32("Id"),
            ToolKey = reader.GetRequiredString("ToolKey"),
            Name = reader.GetRequiredString("Name"),
            SystemPrompt = reader.GetRequiredString("SystemPrompt"),
            TargetSchemaJson = reader.GetRequiredString("TargetSchemaJson"),
            Version = reader.GetRequiredInt32("Version"),
            IsActive = reader.GetRequiredBoolean("IsActive"),
            CreatedUtc = reader.GetRequiredDateTime("CreatedUtc"),
            UpdatedUtc = reader.GetRequiredDateTime("UpdatedUtc")
        };
    }

    private static ToolDirectiveSnapshot MapDirectiveSnapshot(SqlDataReader reader)
    {
        return new ToolDirectiveSnapshot
        {
            Id = reader.GetRequiredInt32("Id"),
            ToolDirectiveId = reader.GetRequiredInt32("ToolDirectiveId"),
            ToolKey = reader.GetRequiredString("ToolKey"),
            Name = reader.GetRequiredString("Name"),
            SystemPrompt = reader.GetRequiredString("SystemPrompt"),
            TargetSchemaJson = reader.GetRequiredString("TargetSchemaJson"),
            UserAdditiveDirective = reader.GetNullableString("UserAdditiveDirective"),
            Version = reader.GetRequiredInt32("Version"),
            ContentHash = reader.GetRequiredString("ContentHash"),
            CreatedUtc = reader.GetRequiredDateTime("CreatedUtc")
        };
    }

    private static ToolUserDirectiveSnapshot MapUserDirectiveSnapshot(SqlDataReader reader)
    {
        return new ToolUserDirectiveSnapshot
        {
            Id = reader.GetRequiredInt32("Id"),
            ToolKey = reader.GetRequiredString("ToolKey"),
            UserEmail = reader.GetRequiredString("UserEmail"),
            UserAdditiveDirective = reader.GetRequiredString("UserAdditiveDirective"),
            ContentHash = reader.GetRequiredString("ContentHash"),
            CreatedUtc = reader.GetRequiredDateTime("CreatedUtc")
        };
    }

    private static ToolJob MapToolJob(SqlDataReader reader)
    {
        return new ToolJob
        {
            Id = reader.GetRequiredInt32("Id"),
            ToolKey = reader.GetRequiredString("ToolKey"),
            JobType = reader.GetRequiredString("JobType"),
            UserEmail = reader.GetRequiredString("UserEmail"),
            TenantId = reader.GetNullableString("TenantId"),
            IsPublic = reader.GetRequiredBoolean("IsPublic"),
            ToolDirectiveSnapshotId = HasColumn(reader, "ToolDirectiveSnapshotId") && reader["ToolDirectiveSnapshotId"] is not DBNull
                ? reader.GetRequiredInt32("ToolDirectiveSnapshotId")
                : null,
            ToolUserDirectiveSnapshotId = HasColumn(reader, "ToolUserDirectiveSnapshotId") && reader["ToolUserDirectiveSnapshotId"] is not DBNull
                ? reader.GetRequiredInt32("ToolUserDirectiveSnapshotId")
                : null,
            Status = reader.GetRequiredString("Status"),
            RequestJson = reader.GetNullableString("RequestJson"),
            ResultJson = reader.GetNullableString("ResultJson"),
            ErrorMessage = reader.GetNullableString("ErrorMessage"),
            CreatedUtc = reader.GetRequiredDateTime("CreatedUtc"),
            StartedUtc = reader.GetNullableDateTime("StartedUtc"),
            CompletedUtc = reader.GetNullableDateTime("CompletedUtc")
        };
    }

    private static ProductParseJob MapProductJob(SqlDataReader reader)
    {
        return new ProductParseJob
        {
            Id = reader.GetRequiredInt32("Id"),
            ToolJobId = reader["ToolJobId"] is DBNull ? 0 : reader.GetRequiredInt32("ToolJobId"),
            UserEmail = reader.GetRequiredString("UserEmail"),
            TenantId = reader.GetNullableString("TenantId"),
            IsPublic = reader.GetRequiredBoolean("IsPublic"),
            ToolDirectiveId = reader.GetRequiredInt32("ToolDirectiveId"),
            ToolDirectiveSnapshotId = HasColumn(reader, "ToolDirectiveSnapshotId") && reader["ToolDirectiveSnapshotId"] is not DBNull
                ? reader.GetRequiredInt32("ToolDirectiveSnapshotId")
                : null,
            ToolUserDirectiveSnapshotId = HasColumn(reader, "ToolUserDirectiveSnapshotId") && reader["ToolUserDirectiveSnapshotId"] is not DBNull
                ? reader.GetRequiredInt32("ToolUserDirectiveSnapshotId")
                : null,
            AiProviderId = reader.GetRequiredInt32("AiProviderId"),
            OriginalFileName = reader.GetRequiredString("OriginalFileName"),
            StoredFilePath = reader.GetRequiredString("StoredFilePath"),
            FileSizeBytes = (long)reader["FileSizeBytes"],
            Status = reader.GetRequiredString("Status"),
            ErrorMessage = reader.GetNullableString("ErrorMessage"),
            InputTokens = reader["InputTokens"] is DBNull ? null : reader.GetRequiredInt32("InputTokens"),
            OutputTokens = reader["OutputTokens"] is DBNull ? null : reader.GetRequiredInt32("OutputTokens"),
            DurationMs = reader["DurationMs"] is DBNull ? null : reader.GetRequiredInt32("DurationMs"),
            CreatedUtc = reader.GetRequiredDateTime("CreatedUtc"),
            CompletedUtc = reader.GetNullableDateTime("CompletedUtc"),
            AiProviderName = HasColumn(reader, "AiProviderName") ? reader.GetNullableString("AiProviderName") : null,
            AiProviderModelName = HasColumn(reader, "AiProviderModelName") ? reader.GetNullableString("AiProviderModelName") : null,
            DirectiveName = HasColumn(reader, "DirectiveName") ? reader.GetNullableString("DirectiveName") : null,
            DirectiveVersion = HasColumn(reader, "DirectiveVersion") && reader["DirectiveVersion"] is not DBNull
                ? reader.GetRequiredInt32("DirectiveVersion")
                : null,
            DirectiveSnapshotHash = HasColumn(reader, "DirectiveSnapshotHash") ? reader.GetNullableString("DirectiveSnapshotHash") : null,
            ParsedProductCount = HasColumn(reader, "ParsedProductCount") && reader["ParsedProductCount"] is not DBNull
                ? reader.GetRequiredInt32("ParsedProductCount")
                : 0,
            ParsedProductNames = HasColumn(reader, "ParsedProductNames") ? reader.GetNullableString("ParsedProductNames") : null
        };
    }

    private static ParsedProduct MapParsedProduct(SqlDataReader reader)
    {
        return new ParsedProduct
        {
            Id = reader.GetRequiredInt32("Id"),
            ProductParseJobId = reader.GetRequiredInt32("ProductParseJobId"),
            UserEmail = reader.GetRequiredString("UserEmail"),
            ProductDataJson = reader.GetRequiredString("ProductDataJson"),
            ProductName = reader.GetNullableString("ProductName"),
            ProductCode = reader.GetNullableString("ProductCode"),
            IsSentToZoho = reader.GetRequiredBoolean("IsSentToZoho"),
            CreatedUtc = reader.GetRequiredDateTime("CreatedUtc"),
            UpdatedUtc = reader.GetRequiredDateTime("UpdatedUtc")
        };
    }

    private static ProductLibraryItem MapProductLibraryItem(SqlDataReader reader)
    {
        return new ProductLibraryItem
        {
            Id = reader.GetRequiredInt32("Id"),
            UserEmail = reader.GetRequiredString("UserEmail"),
            IsPublic = reader.GetRequiredBoolean("IsPublic"),
            PrimaryParsedProductId = reader["PrimaryParsedProductId"] is DBNull ? null : reader.GetRequiredInt32("PrimaryParsedProductId"),
            ProductDataJson = reader.GetRequiredString("ProductDataJson"),
            ProductName = reader.GetNullableString("ProductName"),
            ProductCode = reader.GetNullableString("ProductCode"),
            Supplier = reader.GetNullableString("Supplier"),
            Manufacturer = reader.GetNullableString("Manufacturer"),
            Category = reader.GetNullableString("Category"),
            ProductValue = reader["ProductValue"] is DBNull ? null : (decimal)reader["ProductValue"],
            Currency = reader.GetNullableString("Currency"),
            SourceSummary = reader.GetNullableString("SourceSummary"),
            IsSentToZoho = reader.GetRequiredBoolean("IsSentToZoho"),
            CreatedUtc = reader.GetRequiredDateTime("CreatedUtc"),
            UpdatedUtc = reader.GetRequiredDateTime("UpdatedUtc")
        };
    }

    private static ProductParseJobHistoryItem MapProductParseJobHistoryItem(SqlDataReader reader)
    {
        return new ProductParseJobHistoryItem
        {
            ToolJobId = reader.GetRequiredInt32("ToolJobId"),
            FirstProductParseJobId = reader["FirstProductParseJobId"] is DBNull ? null : reader.GetRequiredInt32("FirstProductParseJobId"),
            FirstParsedProductId = reader["FirstParsedProductId"] is DBNull ? null : reader.GetRequiredInt32("FirstParsedProductId"),
            UserEmail = reader.GetRequiredString("UserEmail"),
            IsPublic = reader.GetRequiredBoolean("IsPublic"),
            Status = reader.GetRequiredString("Status"),
            CreatedUtc = reader.GetRequiredDateTime("CreatedUtc"),
            CompletedUtc = reader.GetNullableDateTime("CompletedUtc"),
            FileCount = reader["FileCount"] is DBNull ? 0 : reader.GetRequiredInt32("FileCount"),
            ProductCount = reader["ProductCount"] is DBNull ? 0 : reader.GetRequiredInt32("ProductCount"),
            ProductNames = reader.GetNullableString("ProductNames"),
            OriginalFileNames = reader.GetNullableString("OriginalFileNames"),
            DirectiveName = reader.GetNullableString("DirectiveName"),
            DirectiveVersion = reader["DirectiveVersion"] is DBNull ? null : reader.GetRequiredInt32("DirectiveVersion"),
            AiProviderName = reader.GetNullableString("AiProviderName"),
            AiProviderModelName = reader.GetNullableString("AiProviderModelName"),
            InputTokens = reader["InputTokens"] is DBNull ? null : reader.GetRequiredInt32("InputTokens"),
            OutputTokens = reader["OutputTokens"] is DBNull ? null : reader.GetRequiredInt32("OutputTokens"),
            DurationMs = reader["DurationMs"] is DBNull ? null : reader.GetRequiredInt32("DurationMs"),
            ErrorMessage = reader.GetNullableString("ErrorMessage")
        };
    }

    private static ProductLibraryGroupRow MapProductLibraryGroupRow(SqlDataReader reader)
    {
        return new ProductLibraryGroupRow(
            reader.GetRequiredInt32("ProductLibraryItemId"),
            reader.GetRequiredString("UserEmail"),
            reader.GetRequiredBoolean("IsPublic"),
            reader["PrimaryParsedProductId"] is DBNull ? null : reader.GetRequiredInt32("PrimaryParsedProductId"),
            reader.GetNullableString("ProductName"),
            reader.GetNullableString("ProductCode"),
            reader.GetNullableString("Supplier"),
            reader.GetNullableString("Manufacturer"),
            reader.GetNullableString("Category"),
            reader["ProductValue"] is DBNull ? null : (decimal)reader["ProductValue"],
            reader.GetNullableString("Currency"),
            reader.GetRequiredDateTime("CreatedUtc"),
            reader["ToolJobId"] is DBNull ? null : reader.GetRequiredInt32("ToolJobId"),
            reader.GetNullableString("Status"),
            reader.GetNullableString("OriginalFileName"),
            reader.GetNullableString("DirectiveName"),
            reader["DirectiveVersion"] is DBNull ? null : reader.GetRequiredInt32("DirectiveVersion"));
    }

    private static ModelPricing MapPricing(SqlDataReader reader)
    {
        var providerType = Enum.TryParse<AiProviderType>(reader.GetRequiredString("ProviderType"), out var parsed)
            ? parsed
            : AiProviderType.OpenAiCompatible;
        return new ModelPricing
        {
            Id = reader.GetRequiredInt32("Id"),
            ProviderType = providerType,
            ModelName = reader.GetRequiredString("ModelName"),
            InputPricePer1kUsd = (decimal)reader["InputPricePer1kUsd"],
            OutputPricePer1kUsd = (decimal)reader["OutputPricePer1kUsd"],
            EffectiveFromUtc = reader.GetRequiredDateTime("EffectiveFromUtc"),
            IsActive = reader.GetRequiredBoolean("IsActive")
        };
    }

    private static void AddToolParameters(SqlCommand command, ToolDefinition tool)
    {
        command.Parameters.AddWithValue("@ToolKey", tool.ToolKey.Trim());
        command.Parameters.AddWithValue("@DisplayName", tool.DisplayName.Trim());
        command.Parameters.AddWithValue("@Description", (object?)tool.Description?.Trim() ?? DBNull.Value);
        command.Parameters.Add("@IsEnabled", SqlDbType.Bit).Value = tool.IsEnabled;
        command.Parameters.AddWithValue("@AccessMode", tool.AccessMode.ToString());
    }

    private static void AddProductJobParameters(SqlCommand command, ProductParseJob job)
    {
        command.Parameters.AddWithValue("@ToolJobId", job.ToolJobId == 0 ? DBNull.Value : job.ToolJobId);
        command.Parameters.AddWithValue("@UserEmail", job.UserEmail);
        command.Parameters.AddWithValue("@TenantId", (object?)job.TenantId ?? DBNull.Value);
        command.Parameters.Add("@IsPublic", SqlDbType.Bit).Value = job.IsPublic;
        command.Parameters.AddWithValue("@ToolDirectiveId", job.ToolDirectiveId);
        command.Parameters.AddWithValue("@ToolDirectiveSnapshotId", (object?)job.ToolDirectiveSnapshotId ?? DBNull.Value);
        command.Parameters.AddWithValue("@ToolUserDirectiveSnapshotId", (object?)job.ToolUserDirectiveSnapshotId ?? DBNull.Value);
        command.Parameters.AddWithValue("@AiProviderId", job.AiProviderId);
        command.Parameters.AddWithValue("@OriginalFileName", job.OriginalFileName);
        command.Parameters.AddWithValue("@StoredFilePath", job.StoredFilePath);
        command.Parameters.AddWithValue("@FileSizeBytes", job.FileSizeBytes);
        command.Parameters.AddWithValue("@Status", job.Status);
        AddNVarCharMax(command, "@ErrorMessage", job.ErrorMessage);
        AddNullableInt(command, "@InputTokens", job.InputTokens);
        AddNullableInt(command, "@OutputTokens", job.OutputTokens);
        AddNullableInt(command, "@DurationMs", job.DurationMs);
        command.Parameters.AddWithValue("@CompletedUtc", (object?)job.CompletedUtc ?? DBNull.Value);
    }

    private static void AddToolJobParameters(SqlCommand command, ToolJob job)
    {
        command.Parameters.AddWithValue("@ToolKey", job.ToolKey.Trim());
        command.Parameters.AddWithValue("@JobType", job.JobType.Trim());
        command.Parameters.AddWithValue("@UserEmail", job.UserEmail.Trim());
        command.Parameters.AddWithValue("@TenantId", (object?)job.TenantId ?? DBNull.Value);
        command.Parameters.Add("@IsPublic", SqlDbType.Bit).Value = job.IsPublic;
        command.Parameters.AddWithValue("@ToolDirectiveSnapshotId", (object?)job.ToolDirectiveSnapshotId ?? DBNull.Value);
        command.Parameters.AddWithValue("@ToolUserDirectiveSnapshotId", (object?)job.ToolUserDirectiveSnapshotId ?? DBNull.Value);
        command.Parameters.AddWithValue("@Status", job.Status.Trim());
        AddNVarCharMax(command, "@RequestJson", job.RequestJson);
        AddNVarCharMax(command, "@ResultJson", job.ResultJson);
        AddNVarCharMax(command, "@ErrorMessage", job.ErrorMessage);
        command.Parameters.AddWithValue("@StartedUtc", (object?)job.StartedUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("@CompletedUtc", (object?)job.CompletedUtc ?? DBNull.Value);
    }

    private static void AddDirectiveSnapshotParameters(SqlCommand command, ToolDirectiveSnapshot snapshot)
    {
        command.Parameters.AddWithValue("@ToolDirectiveId", snapshot.ToolDirectiveId);
        command.Parameters.AddWithValue("@ToolKey", snapshot.ToolKey.Trim());
        command.Parameters.AddWithValue("@Name", snapshot.Name.Trim());
        AddNVarCharMax(command, "@SystemPrompt", snapshot.SystemPrompt);
        AddNVarCharMax(command, "@TargetSchemaJson", snapshot.TargetSchemaJson);
        AddNVarCharMax(command, "@UserAdditiveDirective", snapshot.UserAdditiveDirective);
        command.Parameters.AddWithValue("@Version", snapshot.Version);
        command.Parameters.AddWithValue("@ContentHash", snapshot.ContentHash);
    }

    private static void AddUserDirectiveSnapshotParameters(SqlCommand command, ToolUserDirectiveSnapshot snapshot)
    {
        command.Parameters.AddWithValue("@ToolKey", snapshot.ToolKey.Trim());
        command.Parameters.AddWithValue("@UserEmail", snapshot.UserEmail.Trim());
        AddNVarCharMax(command, "@UserAdditiveDirective", snapshot.UserAdditiveDirective.Trim());
        command.Parameters.AddWithValue("@ContentHash", snapshot.ContentHash);
    }

    private static string NormalizeScope(string? scope)
    {
        return string.Equals(scope, "mine", StringComparison.OrdinalIgnoreCase)
            || string.Equals(scope, "public", StringComparison.OrdinalIgnoreCase)
                ? scope!.ToLowerInvariant()
                : "all";
    }

    private static string NormalizeJobHistorySortBy(string? sortBy)
    {
        return sortBy?.Trim().ToLowerInvariant() switch
        {
            "user" => "user",
            "product" => "product",
            "filecount" => "fileCount",
            "status" => "status",
            "directive" => "directive",
            "duration" => "duration",
            _ => "date"
        };
    }

    private static string BuildJobHistoryOrderBy(string sortBy, string sortDirection)
    {
        var direction = string.Equals(sortDirection, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
        var primary = sortBy switch
        {
            "user" => $"toolJobs.UserEmail {direction}",
            "product" => $"productSummary.ProductNames {direction}",
            "fileCount" => $"COALESCE(fileSummary.FileCount, 0) {direction}",
            "status" => $"toolJobs.Status {direction}",
            "directive" => $"COALESCE(snapshots.Name, directives.Name) {direction}",
            "duration" => $"fileSummary.DurationMs {direction}",
            _ => $"toolJobs.CreatedUtc {direction}"
        };

        return $"{primary}, toolJobs.Id DESC";
    }

    private static string NormalizeLibraryKey(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToUpperInvariant();
    }

    private static void AddParsedProductParameters(SqlCommand command, ParsedProduct product)
    {
        command.Parameters.AddWithValue("@ProductParseJobId", product.ProductParseJobId);
        command.Parameters.AddWithValue("@UserEmail", product.UserEmail);
        AddNVarCharMax(command, "@ProductDataJson", product.ProductDataJson);
        command.Parameters.AddWithValue("@ProductName", (object?)product.ProductName ?? DBNull.Value);
        command.Parameters.AddWithValue("@ProductCode", (object?)product.ProductCode ?? DBNull.Value);
        command.Parameters.Add("@IsSentToZoho", SqlDbType.Bit).Value = product.IsSentToZoho;
    }

    private static void AddProductLibraryItemParameters(SqlCommand command, ProductLibraryItem item)
    {
        command.Parameters.AddWithValue("@UserEmail", item.UserEmail.Trim());
        command.Parameters.Add("@IsPublic", SqlDbType.Bit).Value = item.IsPublic;
        command.Parameters.AddWithValue("@PrimaryParsedProductId", (object?)item.PrimaryParsedProductId ?? DBNull.Value);
        AddNVarCharMax(command, "@ProductDataJson", item.ProductDataJson);
        command.Parameters.AddWithValue("@ProductName", (object?)item.ProductName ?? DBNull.Value);
        command.Parameters.AddWithValue("@ProductCode", (object?)item.ProductCode ?? DBNull.Value);
        command.Parameters.AddWithValue("@Supplier", (object?)item.Supplier ?? DBNull.Value);
        command.Parameters.AddWithValue("@Manufacturer", (object?)item.Manufacturer ?? DBNull.Value);
        command.Parameters.AddWithValue("@Category", (object?)item.Category ?? DBNull.Value);
        AddNullableDecimal(command, "@ProductValue", item.ProductValue, 18, 2);
        command.Parameters.AddWithValue("@Currency", (object?)item.Currency ?? DBNull.Value);
        command.Parameters.AddWithValue("@SourceSummary", (object?)item.SourceSummary ?? DBNull.Value);
        command.Parameters.Add("@IsSentToZoho", SqlDbType.Bit).Value = item.IsSentToZoho;
    }

    private static void AddProductLibraryFilterParameters(SqlCommand command, string userEmail, ProductLibraryFilter filter)
    {
        command.Parameters.AddWithValue("@UserEmailNormalized", userEmail.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("@Scope", NormalizeScope(filter.Scope));
        command.Parameters.AddWithValue("@Search", string.IsNullOrWhiteSpace(filter.Search) ? DBNull.Value : filter.Search.Trim());
        command.Parameters.AddWithValue("@SearchLike", string.IsNullOrWhiteSpace(filter.Search) ? DBNull.Value : $"%{filter.Search.Trim()}%");
        command.Parameters.AddWithValue("@Supplier", string.IsNullOrWhiteSpace(filter.Supplier) ? DBNull.Value : filter.Supplier.Trim());
        command.Parameters.AddWithValue("@Manufacturer", string.IsNullOrWhiteSpace(filter.Manufacturer) ? DBNull.Value : filter.Manufacturer.Trim());
        command.Parameters.AddWithValue("@Category", string.IsNullOrWhiteSpace(filter.Category) ? DBNull.Value : filter.Category.Trim());
        AddNullableDecimal(command, "@MinValue", filter.MinValue, 18, 2);
        AddNullableDecimal(command, "@MaxValue", filter.MaxValue, 18, 2);
    }

    private static void AddNVarCharMax(SqlCommand command, string name, string? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.NVarChar, -1);
        parameter.Value = (object?)value ?? DBNull.Value;
    }

    private static void AddNullableInt(SqlCommand command, string name, int? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Int);
        parameter.Value = (object?)value ?? DBNull.Value;
    }

    private static void AddNullableDecimal(SqlCommand command, string name, decimal? value, byte precision, byte scale)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = precision;
        parameter.Scale = scale;
        parameter.Value = (object?)value ?? DBNull.Value;
    }

    private sealed record ProductLibraryGroupRow(
        int ProductLibraryItemId,
        string UserEmail,
        bool IsPublic,
        int? PrimaryParsedProductId,
        string? ProductName,
        string? ProductCode,
        string? Supplier,
        string? Manufacturer,
        string? Category,
        decimal? ProductValue,
        string? Currency,
        DateTime CreatedUtc,
        int? ToolJobId,
        string? Status,
        string? OriginalFileName,
        string? DirectiveName,
        int? DirectiveVersion);

    private static bool HasColumn(SqlDataReader reader, string columnName)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
