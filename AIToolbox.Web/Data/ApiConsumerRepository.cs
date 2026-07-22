using AIToolbox.Web.Models;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Web.Data;

public sealed class ApiConsumerRepository : IApiConsumerRepository
{
    private readonly SqlConnectionFactory _connectionFactory;

    public ApiConsumerRepository(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<ApiConsumer>> GetConsumersAsync(CancellationToken cancellationToken = default)
    {
        var consumers = new List<ApiConsumer>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Description, OwnerEmail, IsEnabled, CreatedUtc, UpdatedUtc, LastUsedUtc
            FROM dbo.ApiConsumers
            ORDER BY Name;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            consumers.Add(MapConsumer(reader));
        }

        return consumers;
    }

    public async Task<ApiConsumer?> GetConsumerAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        ApiConsumer? consumer;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT Id, Name, Description, OwnerEmail, IsEnabled, CreatedUtc, UpdatedUtc, LastUsedUtc
                FROM dbo.ApiConsumers
                WHERE Id = @Id;
                """;
            command.Parameters.AddWithValue("@Id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            consumer = await reader.ReadAsync(cancellationToken) ? MapConsumer(reader) : null;
        }

        if (consumer is null)
        {
            return null;
        }

        consumer.Keys = await GetKeysForConsumerAsync(connection, id, cancellationToken);
        return consumer;
    }

    public async Task<int> CreateConsumerAsync(ApiConsumer consumer, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.ApiConsumers (Name, Description, OwnerEmail, IsEnabled, CreatedUtc, UpdatedUtc)
            OUTPUT INSERTED.Id
            VALUES (@Name, @Description, @OwnerEmail, @IsEnabled, SYSUTCDATETIME(), SYSUTCDATETIME());
            """;
        AddConsumerParameters(command, consumer);
        return (int)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task UpdateConsumerAsync(ApiConsumer consumer, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.ApiConsumers
            SET Name = @Name,
                Description = @Description,
                OwnerEmail = @OwnerEmail,
                IsEnabled = @IsEnabled,
                UpdatedUtc = SYSUTCDATETIME()
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", consumer.Id);
        AddConsumerParameters(command, consumer);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ApiConsumerKey?> GetKeyByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT keys.Id, keys.ApiConsumerId, consumers.Name AS ConsumerName, keys.Name, keys.KeyPrefix,
                   keys.KeyHash, keys.HashAlgorithm, keys.ScopesJson, keys.ExpiresUtc,
                   keys.RevokedUtc, keys.CreatedUtc, keys.LastUsedUtc
            FROM dbo.ApiConsumerKeys keys
            INNER JOIN dbo.ApiConsumers consumers ON consumers.Id = keys.ApiConsumerId
            WHERE keys.KeyPrefix = @KeyPrefix
                AND consumers.IsEnabled = 1
                AND keys.RevokedUtc IS NULL
                AND (keys.ExpiresUtc IS NULL OR keys.ExpiresUtc > SYSUTCDATETIME());
            """;
        command.Parameters.AddWithValue("@KeyPrefix", keyPrefix);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapKey(reader) : null;
    }

    public async Task<int> CreateKeyAsync(ApiConsumerKey key, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.ApiConsumerKeys
                (ApiConsumerId, Name, KeyPrefix, KeyHash, HashAlgorithm, ScopesJson, ExpiresUtc, CreatedUtc)
            OUTPUT INSERTED.Id
            VALUES
                (@ApiConsumerId, @Name, @KeyPrefix, @KeyHash, @HashAlgorithm, @ScopesJson, @ExpiresUtc, SYSUTCDATETIME());
            """;
        command.Parameters.AddWithValue("@ApiConsumerId", key.ApiConsumerId);
        command.Parameters.AddWithValue("@Name", key.Name);
        command.Parameters.AddWithValue("@KeyPrefix", key.KeyPrefix);
        command.Parameters.AddWithValue("@KeyHash", key.KeyHash);
        command.Parameters.AddWithValue("@HashAlgorithm", key.HashAlgorithm);
        command.Parameters.AddWithValue("@ScopesJson", key.ScopesJson);
        command.Parameters.AddWithValue("@ExpiresUtc", (object?)key.ExpiresUtc ?? DBNull.Value);
        return (int)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task RevokeKeyAsync(int keyId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.ApiConsumerKeys
            SET RevokedUtc = COALESCE(RevokedUtc, SYSUTCDATETIME())
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", keyId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkKeyUsedAsync(int keyId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE keys
            SET LastUsedUtc = SYSUTCDATETIME()
            FROM dbo.ApiConsumerKeys keys
            WHERE keys.Id = @Id;

            UPDATE consumers
            SET LastUsedUtc = SYSUTCDATETIME()
            FROM dbo.ApiConsumers consumers
            INNER JOIN dbo.ApiConsumerKeys keys ON keys.ApiConsumerId = consumers.Id
            WHERE keys.Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", keyId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<ApiConsumerKey>> GetKeysForConsumerAsync(
        SqlConnection connection,
        int consumerId,
        CancellationToken cancellationToken)
    {
        var keys = new List<ApiConsumerKey>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT keys.Id, keys.ApiConsumerId, consumers.Name AS ConsumerName, keys.Name, keys.KeyPrefix,
                   keys.KeyHash, keys.HashAlgorithm, keys.ScopesJson, keys.ExpiresUtc,
                   keys.RevokedUtc, keys.CreatedUtc, keys.LastUsedUtc
            FROM dbo.ApiConsumerKeys keys
            INNER JOIN dbo.ApiConsumers consumers ON consumers.Id = keys.ApiConsumerId
            WHERE keys.ApiConsumerId = @ConsumerId
            ORDER BY keys.CreatedUtc DESC;
            """;
        command.Parameters.AddWithValue("@ConsumerId", consumerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            keys.Add(MapKey(reader));
        }

        return keys;
    }

    private static void AddConsumerParameters(SqlCommand command, ApiConsumer consumer)
    {
        command.Parameters.AddWithValue("@Name", consumer.Name.Trim());
        command.Parameters.AddWithValue("@Description", (object?)consumer.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("@OwnerEmail", (object?)consumer.OwnerEmail ?? DBNull.Value);
        command.Parameters.AddWithValue("@IsEnabled", consumer.IsEnabled);
    }

    private static ApiConsumer MapConsumer(SqlDataReader reader)
    {
        return new ApiConsumer
        {
            Id = reader.GetRequiredInt32("Id"),
            Name = reader.GetRequiredString("Name"),
            Description = reader.GetNullableString("Description"),
            OwnerEmail = reader.GetNullableString("OwnerEmail"),
            IsEnabled = reader.GetRequiredBoolean("IsEnabled"),
            CreatedUtc = reader.GetRequiredDateTime("CreatedUtc"),
            UpdatedUtc = reader.GetRequiredDateTime("UpdatedUtc"),
            LastUsedUtc = reader.GetNullableDateTime("LastUsedUtc")
        };
    }

    private static ApiConsumerKey MapKey(SqlDataReader reader)
    {
        return new ApiConsumerKey
        {
            Id = reader.GetRequiredInt32("Id"),
            ApiConsumerId = reader.GetRequiredInt32("ApiConsumerId"),
            ConsumerName = reader.GetRequiredString("ConsumerName"),
            Name = reader.GetRequiredString("Name"),
            KeyPrefix = reader.GetRequiredString("KeyPrefix"),
            KeyHash = reader.GetRequiredString("KeyHash"),
            HashAlgorithm = reader.GetRequiredString("HashAlgorithm"),
            ScopesJson = reader.GetRequiredString("ScopesJson"),
            ExpiresUtc = reader.GetNullableDateTime("ExpiresUtc"),
            RevokedUtc = reader.GetNullableDateTime("RevokedUtc"),
            CreatedUtc = reader.GetRequiredDateTime("CreatedUtc"),
            LastUsedUtc = reader.GetNullableDateTime("LastUsedUtc")
        };
    }
}
