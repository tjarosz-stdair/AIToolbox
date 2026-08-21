using AIToolbox.Web.Models;

namespace AIToolbox.Web.Data;

/// <summary>
/// A single row, seeded at table creation (DatabaseInitializer), so this never needs an
/// upsert-with-race-risk path — only ever a plain UPDATE.
/// </summary>
public sealed class RetentionSettingsRepository : IRetentionSettingsRepository
{
    private readonly SqlConnectionFactory _connectionFactory;

    public RetentionSettingsRepository(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<RetentionSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT TOP (1) ConversationRetentionDays, UpdatedUtc FROM dbo.RetentionSettings;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new RetentionSettings
            {
                ConversationRetentionDays = reader.GetRequiredInt32("ConversationRetentionDays"),
                UpdatedUtc = reader.GetRequiredDateTime("UpdatedUtc")
            };
        }

        return new RetentionSettings();
    }

    public async Task UpdateAsync(int conversationRetentionDays, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.RetentionSettings
            SET ConversationRetentionDays = @ConversationRetentionDays,
                UpdatedUtc = SYSUTCDATETIME();
            """;
        command.Parameters.AddWithValue("@ConversationRetentionDays", conversationRetentionDays);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
