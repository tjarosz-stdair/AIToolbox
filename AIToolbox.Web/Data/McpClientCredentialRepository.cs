using AIToolbox.Web.Models;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Web.Data;

public sealed class McpClientCredentialRepository : IMcpClientCredentialRepository
{
    private readonly SqlConnectionFactory _connectionFactory;

    public McpClientCredentialRepository(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<McpClientCredential>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var credentials = new List<McpClientCredential>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Surface, ClientId, ClientSecretCipherText, ClientSecretLastUpdatedUtc,
                   TokenEndpointUrl, McpEndpointUrl, GrantedScopes, IsEnabled, CreatedUtc, UpdatedUtc
            FROM dbo.McpClientCredentials
            ORDER BY Surface;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            credentials.Add(MapCredential(reader));
        }

        return credentials;
    }

    public async Task<McpClientCredential?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Surface, ClientId, ClientSecretCipherText, ClientSecretLastUpdatedUtc,
                   TokenEndpointUrl, McpEndpointUrl, GrantedScopes, IsEnabled, CreatedUtc, UpdatedUtc
            FROM dbo.McpClientCredentials
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapCredential(reader) : null;
    }

    public async Task<McpClientCredential?> GetBySurfaceAsync(string surface, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Surface, ClientId, ClientSecretCipherText, ClientSecretLastUpdatedUtc,
                   TokenEndpointUrl, McpEndpointUrl, GrantedScopes, IsEnabled, CreatedUtc, UpdatedUtc
            FROM dbo.McpClientCredentials
            WHERE Surface = @Surface AND IsEnabled = 1;
            """;
        command.Parameters.AddWithValue("@Surface", surface.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapCredential(reader) : null;
    }

    public async Task<int> CreateAsync(McpClientCredential credential, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.McpClientCredentials
                (Surface, ClientId, ClientSecretCipherText, ClientSecretLastUpdatedUtc,
                 TokenEndpointUrl, McpEndpointUrl, GrantedScopes, IsEnabled, CreatedUtc, UpdatedUtc)
            OUTPUT INSERTED.Id
            VALUES
                (@Surface, @ClientId, @ClientSecretCipherText, @ClientSecretLastUpdatedUtc,
                 @TokenEndpointUrl, @McpEndpointUrl, @GrantedScopes, @IsEnabled, SYSUTCDATETIME(), SYSUTCDATETIME());
            """;
        AddCredentialParameters(command, credential, updateSecret: true);
        return (int)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task UpdateAsync(McpClientCredential credential, bool updateSecret, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = updateSecret
            ? """
              UPDATE dbo.McpClientCredentials
              SET Surface = @Surface,
                  ClientId = @ClientId,
                  ClientSecretCipherText = @ClientSecretCipherText,
                  ClientSecretLastUpdatedUtc = @ClientSecretLastUpdatedUtc,
                  TokenEndpointUrl = @TokenEndpointUrl,
                  McpEndpointUrl = @McpEndpointUrl,
                  GrantedScopes = @GrantedScopes,
                  IsEnabled = @IsEnabled,
                  UpdatedUtc = SYSUTCDATETIME()
              WHERE Id = @Id;
              """
            : """
              UPDATE dbo.McpClientCredentials
              SET Surface = @Surface,
                  ClientId = @ClientId,
                  TokenEndpointUrl = @TokenEndpointUrl,
                  McpEndpointUrl = @McpEndpointUrl,
                  GrantedScopes = @GrantedScopes,
                  IsEnabled = @IsEnabled,
                  UpdatedUtc = SYSUTCDATETIME()
              WHERE Id = @Id;
              """;

        command.Parameters.AddWithValue("@Id", credential.Id);
        AddCredentialParameters(command, credential, updateSecret);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddCredentialParameters(SqlCommand command, McpClientCredential credential, bool updateSecret)
    {
        command.Parameters.AddWithValue("@Surface", credential.Surface.Trim());
        command.Parameters.AddWithValue("@ClientId", credential.ClientId.Trim());
        command.Parameters.AddWithValue("@TokenEndpointUrl", credential.TokenEndpointUrl.Trim());
        command.Parameters.AddWithValue("@McpEndpointUrl", credential.McpEndpointUrl.Trim());
        command.Parameters.AddWithValue("@GrantedScopes", (object?)credential.GrantedScopes?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("@IsEnabled", credential.IsEnabled);

        if (updateSecret)
        {
            command.Parameters.AddWithValue("@ClientSecretCipherText", (object?)credential.ClientSecretCipherText ?? DBNull.Value);
            command.Parameters.AddWithValue("@ClientSecretLastUpdatedUtc", (object?)credential.ClientSecretLastUpdatedUtc ?? DBNull.Value);
        }
    }

    private static McpClientCredential MapCredential(SqlDataReader reader)
    {
        return new McpClientCredential
        {
            Id = reader.GetRequiredInt32("Id"),
            Surface = reader.GetRequiredString("Surface"),
            ClientId = reader.GetRequiredString("ClientId"),
            ClientSecretCipherText = reader.GetNullableString("ClientSecretCipherText"),
            ClientSecretLastUpdatedUtc = reader.GetNullableDateTime("ClientSecretLastUpdatedUtc"),
            TokenEndpointUrl = reader.GetRequiredString("TokenEndpointUrl"),
            McpEndpointUrl = reader.GetRequiredString("McpEndpointUrl"),
            GrantedScopes = reader.GetNullableString("GrantedScopes"),
            IsEnabled = reader.GetRequiredBoolean("IsEnabled"),
            CreatedUtc = reader.GetRequiredDateTime("CreatedUtc"),
            UpdatedUtc = reader.GetRequiredDateTime("UpdatedUtc")
        };
    }
}
