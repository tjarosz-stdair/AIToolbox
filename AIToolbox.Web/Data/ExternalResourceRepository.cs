using AIToolbox.Web.Models;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Web.Data;

public sealed class ExternalResourceRepository : IExternalResourceRepository
{
    private readonly SqlConnectionFactory _connectionFactory;

    public ExternalResourceRepository(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<ExternalResource>> GetAllAsync(
        bool includeDisabled = false,
        CancellationToken cancellationToken = default)
    {
        var resources = new List<ExternalResource>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, ResourceKey, ResourceType, DisplayName, ConfigJson,
                   SecretCipherText, SecretLastUpdatedUtc, IsEnabled, CreatedUtc, UpdatedUtc
            FROM dbo.ExternalResources
            {(includeDisabled ? string.Empty : "WHERE IsEnabled = 1")}
            ORDER BY DisplayName;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            resources.Add(MapResource(reader));
        }

        return resources;
    }

    public async Task<ExternalResource?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ResourceKey, ResourceType, DisplayName, ConfigJson,
                   SecretCipherText, SecretLastUpdatedUtc, IsEnabled, CreatedUtc, UpdatedUtc
            FROM dbo.ExternalResources
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapResource(reader) : null;
    }

    public async Task<ExternalResource?> GetEnabledByTypeAsync(
        ExternalResourceType type,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1) Id, ResourceKey, ResourceType, DisplayName, ConfigJson,
                   SecretCipherText, SecretLastUpdatedUtc, IsEnabled, CreatedUtc, UpdatedUtc
            FROM dbo.ExternalResources
            WHERE ResourceType = @ResourceType AND IsEnabled = 1
            ORDER BY Id;
            """;
        command.Parameters.AddWithValue("@ResourceType", type.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapResource(reader) : null;
    }

    public async Task<int> CreateAsync(ExternalResource resource, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.ExternalResources
                (ResourceKey, ResourceType, DisplayName, ConfigJson, SecretCipherText,
                 SecretLastUpdatedUtc, IsEnabled, CreatedUtc, UpdatedUtc)
            OUTPUT INSERTED.Id
            VALUES
                (@ResourceKey, @ResourceType, @DisplayName, @ConfigJson, @SecretCipherText,
                 @SecretLastUpdatedUtc, @IsEnabled, SYSUTCDATETIME(), SYSUTCDATETIME());
            """;
        AddResourceParameters(command, resource, updateSecret: true);
        return (int)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task UpdateAsync(
        ExternalResource resource,
        bool updateSecret,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = updateSecret
            ? """
              UPDATE dbo.ExternalResources
              SET ResourceKey = @ResourceKey,
                  ResourceType = @ResourceType,
                  DisplayName = @DisplayName,
                  ConfigJson = @ConfigJson,
                  SecretCipherText = @SecretCipherText,
                  SecretLastUpdatedUtc = @SecretLastUpdatedUtc,
                  IsEnabled = @IsEnabled,
                  UpdatedUtc = SYSUTCDATETIME()
              WHERE Id = @Id;
              """
            : """
              UPDATE dbo.ExternalResources
              SET ResourceKey = @ResourceKey,
                  ResourceType = @ResourceType,
                  DisplayName = @DisplayName,
                  ConfigJson = @ConfigJson,
                  IsEnabled = @IsEnabled,
                  UpdatedUtc = SYSUTCDATETIME()
              WHERE Id = @Id;
              """;

        command.Parameters.AddWithValue("@Id", resource.Id);
        AddResourceParameters(command, resource, updateSecret);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddResourceParameters(SqlCommand command, ExternalResource resource, bool updateSecret)
    {
        command.Parameters.AddWithValue("@ResourceKey", resource.ResourceKey.Trim());
        command.Parameters.AddWithValue("@ResourceType", resource.ResourceType.ToString());
        command.Parameters.AddWithValue("@DisplayName", resource.DisplayName.Trim());
        command.Parameters.AddWithValue("@ConfigJson", (object?)resource.ConfigJson ?? DBNull.Value);
        command.Parameters.AddWithValue("@IsEnabled", resource.IsEnabled);

        if (updateSecret)
        {
            command.Parameters.AddWithValue("@SecretCipherText", (object?)resource.SecretCipherText ?? DBNull.Value);
            command.Parameters.AddWithValue("@SecretLastUpdatedUtc", (object?)resource.SecretLastUpdatedUtc ?? DBNull.Value);
        }
    }

    private static ExternalResource MapResource(SqlDataReader reader)
    {
        var resourceTypeValue = reader.GetRequiredString("ResourceType");
        if (!Enum.TryParse<ExternalResourceType>(resourceTypeValue, ignoreCase: true, out var resourceType))
        {
            resourceType = ExternalResourceType.AzureSpeech;
        }

        return new ExternalResource
        {
            Id = reader.GetRequiredInt32("Id"),
            ResourceKey = reader.GetRequiredString("ResourceKey"),
            ResourceType = resourceType,
            DisplayName = reader.GetRequiredString("DisplayName"),
            ConfigJson = reader.GetNullableString("ConfigJson"),
            SecretCipherText = reader.GetNullableString("SecretCipherText"),
            SecretLastUpdatedUtc = reader.GetNullableDateTime("SecretLastUpdatedUtc"),
            IsEnabled = reader.GetRequiredBoolean("IsEnabled"),
            CreatedUtc = reader.GetRequiredDateTime("CreatedUtc"),
            UpdatedUtc = reader.GetRequiredDateTime("UpdatedUtc")
        };
    }
}
