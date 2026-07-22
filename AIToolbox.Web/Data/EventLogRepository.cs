using AIToolbox.Web.Models;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Web.Data;

public sealed class EventLogRepository : IEventLogRepository
{
    private readonly SqlConnectionFactory _connectionFactory;

    public EventLogRepository(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task RecordAsync(EventLogEntry entry, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.EventLogs
                (EventType, UserEmail, EmailNormalized, DisplayName, TenantId, ToolKey, AiProviderId,
                 AiProviderName, Operation, DurationMs, Succeeded, IsNamedUser, IsAdmin, WasAllowed,
                 FailureReason, IpAddress, UserAgent, MetadataJson, CreatedUtc)
            VALUES
                (@EventType, @UserEmail, @EmailNormalized, @DisplayName, @TenantId, @ToolKey, @AiProviderId,
                 @AiProviderName, @Operation, @DurationMs, @Succeeded, @IsNamedUser, @IsAdmin, @WasAllowed,
                 @FailureReason, @IpAddress, @UserAgent, @MetadataJson, SYSUTCDATETIME());
            """;
        command.Parameters.AddWithValue("@EventType", entry.EventType.Trim());
        command.Parameters.AddWithValue("@UserEmail", (object?)entry.UserEmail?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("@EmailNormalized", (object?)NormalizeEmail(entry.UserEmail) ?? DBNull.Value);
        command.Parameters.AddWithValue("@DisplayName", (object?)entry.DisplayName?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("@TenantId", (object?)entry.TenantId?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("@ToolKey", (object?)entry.ToolKey?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("@AiProviderId", (object?)entry.AiProviderId ?? DBNull.Value);
        command.Parameters.AddWithValue("@AiProviderName", (object?)entry.AiProviderName?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("@Operation", (object?)entry.Operation?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("@DurationMs", (object?)entry.DurationMs ?? DBNull.Value);
        command.Parameters.AddWithValue("@Succeeded", (object?)entry.Succeeded ?? DBNull.Value);
        command.Parameters.AddWithValue("@IsNamedUser", entry.IsNamedUser);
        command.Parameters.AddWithValue("@IsAdmin", entry.IsAdmin);
        command.Parameters.AddWithValue("@WasAllowed", entry.WasAllowed);
        command.Parameters.AddWithValue("@FailureReason", (object?)entry.FailureReason ?? DBNull.Value);
        command.Parameters.AddWithValue("@IpAddress", (object?)entry.IpAddress ?? DBNull.Value);
        command.Parameters.AddWithValue("@UserAgent", (object?)entry.UserAgent ?? DBNull.Value);
        command.Parameters.AddWithValue("@MetadataJson", (object?)entry.MetadataJson ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<UserLoginEventResult> GetUserLoginEventsAsync(
        UserLoginEventFilter filter,
        CancellationToken cancellationToken = default)
    {
        var normalizedFilter = NormalizeUserType(filter.UserType);
        var page = Math.Max(filter.Page, 1);
        var pageSize = Math.Clamp(filter.PageSize, 10, 200);
        var offset = (page - 1) * pageSize;

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(1)
            FROM dbo.EventLogs
            WHERE EventType = @EventType
                {BuildUserTypeWhereClause(normalizedFilter)};

            SELECT Id, EventType, UserEmail, DisplayName, TenantId, IsNamedUser, IsAdmin,
                   WasAllowed, FailureReason, IpAddress, UserAgent, MetadataJson, CreatedUtc
            FROM dbo.EventLogs
            WHERE EventType = @EventType
                {BuildUserTypeWhereClause(normalizedFilter)}
            ORDER BY CreatedUtc DESC, Id DESC
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;
        command.Parameters.AddWithValue("@EventType", EventLogTypes.UserLogin);
        command.Parameters.AddWithValue("@Offset", offset);
        command.Parameters.AddWithValue("@PageSize", pageSize);

        var events = new List<EventLogEntry>();
        var totalCount = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            totalCount = reader.GetInt32(0);
        }

        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(MapEvent(reader));
        }

        return new UserLoginEventResult
        {
            Items = events,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            UserType = normalizedFilter
        };
    }

    public async Task<AiSubmissionEventResult> GetAiSubmissionEventsAsync(
        AiSubmissionEventFilter filter,
        CancellationToken cancellationToken = default)
    {
        var normalizedToolKey = NormalizeToolKey(filter.ToolKey);
        var page = Math.Max(filter.Page, 1);
        var pageSize = Math.Clamp(filter.PageSize, 10, 200);
        var offset = (page - 1) * pageSize;

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(1)
            FROM dbo.EventLogs
            WHERE EventType = @EventType
                {BuildToolKeyWhereClause(normalizedToolKey)};

            SELECT DISTINCT ToolKey
            FROM dbo.EventLogs
            WHERE EventType = @EventType
                AND ToolKey IS NOT NULL
            ORDER BY ToolKey;

            SELECT Id, EventType, UserEmail, DisplayName, TenantId, ToolKey, AiProviderId,
                   AiProviderName, Operation, DurationMs, Succeeded, IsNamedUser, IsAdmin,
                   WasAllowed, FailureReason, IpAddress, UserAgent, MetadataJson, CreatedUtc
            FROM dbo.EventLogs
            WHERE EventType = @EventType
                {BuildToolKeyWhereClause(normalizedToolKey)}
            ORDER BY CreatedUtc DESC, Id DESC
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;
        command.Parameters.AddWithValue("@EventType", EventLogTypes.AiSubmission);
        command.Parameters.AddWithValue("@Offset", offset);
        command.Parameters.AddWithValue("@PageSize", pageSize);
        if (!string.Equals(normalizedToolKey, "all", StringComparison.OrdinalIgnoreCase))
        {
            command.Parameters.AddWithValue("@ToolKey", normalizedToolKey);
        }

        var events = new List<EventLogEntry>();
        var toolKeys = new List<string>();
        var totalCount = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            totalCount = reader.GetInt32(0);
        }

        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            toolKeys.Add(reader.GetRequiredString("ToolKey"));
        }

        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(MapEvent(reader));
        }

        return new AiSubmissionEventResult
        {
            Items = events,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            ToolKey = normalizedToolKey,
            ToolKeys = toolKeys
        };
    }

    private static string BuildUserTypeWhereClause(string userType)
    {
        return userType switch
        {
            "named" => "AND IsNamedUser = 1",
            "admin" => "AND IsAdmin = 1",
            "tenantOnly" => "AND IsNamedUser = 0",
            _ => string.Empty
        };
    }

    private static string NormalizeUserType(string? userType)
    {
        return userType?.Trim() switch
        {
            "named" => "named",
            "admin" => "admin",
            "tenantOnly" => "tenantOnly",
            _ => "all"
        };
    }

    private static string BuildToolKeyWhereClause(string toolKey)
    {
        return string.Equals(toolKey, "all", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : "AND ToolKey = @ToolKey";
    }

    private static string NormalizeToolKey(string? toolKey)
    {
        return string.IsNullOrWhiteSpace(toolKey) ? "all" : toolKey.Trim();
    }

    private static string? NormalizeEmail(string? email)
    {
        return string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToUpperInvariant();
    }

    private static EventLogEntry MapEvent(SqlDataReader reader)
    {
        return new EventLogEntry
        {
            Id = reader.GetRequiredInt32("Id"),
            EventType = reader.GetRequiredString("EventType"),
            UserEmail = reader.GetNullableString("UserEmail"),
            DisplayName = reader.GetNullableString("DisplayName"),
            TenantId = reader.GetNullableString("TenantId"),
            ToolKey = HasColumn(reader, "ToolKey") ? reader.GetNullableString("ToolKey") : null,
            AiProviderId = HasColumn(reader, "AiProviderId") ? reader.GetNullableInt32("AiProviderId") : null,
            AiProviderName = HasColumn(reader, "AiProviderName") ? reader.GetNullableString("AiProviderName") : null,
            Operation = HasColumn(reader, "Operation") ? reader.GetNullableString("Operation") : null,
            DurationMs = HasColumn(reader, "DurationMs") ? reader.GetNullableInt32("DurationMs") : null,
            Succeeded = HasColumn(reader, "Succeeded") ? reader.GetNullableBoolean("Succeeded") : null,
            IsNamedUser = reader.GetRequiredBoolean("IsNamedUser"),
            IsAdmin = reader.GetRequiredBoolean("IsAdmin"),
            WasAllowed = reader.GetRequiredBoolean("WasAllowed"),
            FailureReason = reader.GetNullableString("FailureReason"),
            IpAddress = reader.GetNullableString("IpAddress"),
            UserAgent = reader.GetNullableString("UserAgent"),
            MetadataJson = reader.GetNullableString("MetadataJson"),
            CreatedUtc = reader.GetRequiredDateTime("CreatedUtc")
        };
    }

    private static bool HasColumn(SqlDataReader reader, string name)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
