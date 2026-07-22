using AIToolbox.Web.Models;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Web.Data;

public sealed class AiProviderRepository : IAiProviderRepository
{
    private readonly SqlConnectionFactory _connectionFactory;

    public AiProviderRepository(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<AiProvider>> GetAllAsync(
        bool includeDisabled = false,
        CancellationToken cancellationToken = default)
    {
        var providers = new List<AiProvider>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, DisplayName, ProviderType, EndpointUrl, ModelName, ApiVersion,
                   ApiKeyCipherText, ApiKeyLastUpdatedUtc, ExtraHeadersJson, MaxOutputTokens, RequestTimeoutSeconds,
                   IsEnabled, CreatedUtc, UpdatedUtc
            FROM dbo.AiProviders
            {(includeDisabled ? string.Empty : "WHERE IsEnabled = 1")}
            ORDER BY DisplayName;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            providers.Add(MapProvider(reader));
        }

        return providers;
    }

    public async Task<IReadOnlyList<AiProvider>> GetEnabledByIdsAsync(
        IEnumerable<int> ids,
        CancellationToken cancellationToken = default)
    {
        var requested = ids.ToHashSet();
        if (requested.Count == 0)
        {
            return [];
        }

        var providers = await GetAllAsync(includeDisabled: false, cancellationToken);
        return providers.Where(provider => requested.Contains(provider.Id)).ToList();
    }

    public async Task<AiProvider?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, DisplayName, ProviderType, EndpointUrl, ModelName, ApiVersion,
                   ApiKeyCipherText, ApiKeyLastUpdatedUtc, ExtraHeadersJson, MaxOutputTokens, RequestTimeoutSeconds,
                   IsEnabled, CreatedUtc, UpdatedUtc
            FROM dbo.AiProviders
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapProvider(reader) : null;
    }

    public async Task<int> CreateAsync(AiProvider provider, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.AiProviders
                (DisplayName, ProviderType, EndpointUrl, ModelName, ApiVersion, ApiKeyCipherText,
                 ApiKeyLastUpdatedUtc, ExtraHeadersJson, MaxOutputTokens, RequestTimeoutSeconds, IsEnabled, CreatedUtc, UpdatedUtc)
            OUTPUT INSERTED.Id
            VALUES
                (@DisplayName, @ProviderType, @EndpointUrl, @ModelName, @ApiVersion, @ApiKeyCipherText,
                 @ApiKeyLastUpdatedUtc, @ExtraHeadersJson, @MaxOutputTokens, @RequestTimeoutSeconds, @IsEnabled,
                 SYSUTCDATETIME(), SYSUTCDATETIME());
            """;
        AddProviderParameters(command, provider, updateSecret: true);
        return (int)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task UpdateAsync(
        AiProvider provider,
        bool updateSecret,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = updateSecret
            ? """
              UPDATE dbo.AiProviders
              SET DisplayName = @DisplayName,
                  ProviderType = @ProviderType,
                  EndpointUrl = @EndpointUrl,
                  ModelName = @ModelName,
                  ApiVersion = @ApiVersion,
                  ApiKeyCipherText = @ApiKeyCipherText,
                  ApiKeyLastUpdatedUtc = @ApiKeyLastUpdatedUtc,
                  ExtraHeadersJson = @ExtraHeadersJson,
                  MaxOutputTokens = @MaxOutputTokens,
                  RequestTimeoutSeconds = @RequestTimeoutSeconds,
                  IsEnabled = @IsEnabled,
                  UpdatedUtc = SYSUTCDATETIME()
              WHERE Id = @Id;
              """
            : """
              UPDATE dbo.AiProviders
              SET DisplayName = @DisplayName,
                  ProviderType = @ProviderType,
                  EndpointUrl = @EndpointUrl,
                  ModelName = @ModelName,
                  ApiVersion = @ApiVersion,
                  ExtraHeadersJson = @ExtraHeadersJson,
                  MaxOutputTokens = @MaxOutputTokens,
                  RequestTimeoutSeconds = @RequestTimeoutSeconds,
                  IsEnabled = @IsEnabled,
                  UpdatedUtc = SYSUTCDATETIME()
              WHERE Id = @Id;
              """;

        command.Parameters.AddWithValue("@Id", provider.Id);
        AddProviderParameters(command, provider, updateSecret);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddProviderParameters(SqlCommand command, AiProvider provider, bool updateSecret)
    {
        command.Parameters.AddWithValue("@DisplayName", provider.DisplayName.Trim());
        command.Parameters.AddWithValue("@ProviderType", provider.ProviderType.ToString());
        command.Parameters.AddWithValue("@EndpointUrl", provider.EndpointUrl.Trim());
        command.Parameters.AddWithValue("@ModelName", provider.ModelName.Trim());
        command.Parameters.AddWithValue("@ApiVersion", (object?)provider.ApiVersion?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("@ExtraHeadersJson", (object?)provider.ExtraHeadersJson?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("@MaxOutputTokens", (object?)provider.MaxOutputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("@RequestTimeoutSeconds", provider.RequestTimeoutSeconds);
        command.Parameters.AddWithValue("@IsEnabled", provider.IsEnabled);

        if (updateSecret)
        {
            command.Parameters.AddWithValue("@ApiKeyCipherText", (object?)provider.ApiKeyCipherText ?? DBNull.Value);
            command.Parameters.AddWithValue("@ApiKeyLastUpdatedUtc", (object?)provider.ApiKeyLastUpdatedUtc ?? DBNull.Value);
        }
    }

    private static AiProvider MapProvider(SqlDataReader reader)
    {
        var providerTypeValue = reader.GetRequiredString("ProviderType");
        if (!Enum.TryParse<AiProviderType>(providerTypeValue, ignoreCase: true, out var providerType))
        {
            providerType = AiProviderType.OpenAiCompatible;
        }

        return new AiProvider
        {
            Id = reader.GetRequiredInt32("Id"),
            DisplayName = reader.GetRequiredString("DisplayName"),
            ProviderType = providerType,
            EndpointUrl = reader.GetRequiredString("EndpointUrl"),
            ModelName = reader.GetRequiredString("ModelName"),
            ApiVersion = reader.GetNullableString("ApiVersion"),
            ApiKeyCipherText = reader.GetNullableString("ApiKeyCipherText"),
            ApiKeyLastUpdatedUtc = reader.GetNullableDateTime("ApiKeyLastUpdatedUtc"),
            ExtraHeadersJson = reader.GetNullableString("ExtraHeadersJson"),
            MaxOutputTokens = reader["MaxOutputTokens"] is DBNull ? null : reader.GetRequiredInt32("MaxOutputTokens"),
            RequestTimeoutSeconds = reader.GetRequiredInt32("RequestTimeoutSeconds"),
            IsEnabled = reader.GetRequiredBoolean("IsEnabled"),
            CreatedUtc = reader.GetRequiredDateTime("CreatedUtc"),
            UpdatedUtc = reader.GetRequiredDateTime("UpdatedUtc")
        };
    }
}
