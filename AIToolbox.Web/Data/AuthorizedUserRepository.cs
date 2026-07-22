using AIToolbox.Web.Models;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Web.Data;

public sealed class AuthorizedUserRepository : IAuthorizedUserRepository
{
    private readonly SqlConnectionFactory _connectionFactory;

    public AuthorizedUserRepository(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<AuthorizedUser>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var users = new List<AuthorizedUser>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Email, DisplayName, TenantId, IsAdmin, IsEnabled, CreatedUtc, UpdatedUtc, LastLoginUtc
            FROM dbo.AuthorizedUsers
            ORDER BY Email;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(MapUser(reader));
        }

        return users;
    }

    public async Task<AuthorizedUser?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Email, DisplayName, TenantId, IsAdmin, IsEnabled, CreatedUtc, UpdatedUtc, LastLoginUtc
            FROM dbo.AuthorizedUsers
            WHERE EmailNormalized = @EmailNormalized;
            """;
        command.Parameters.AddWithValue("@EmailNormalized", NormalizeEmail(email));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapUser(reader) : null;
    }

    public async Task<AuthorizedUser?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Email, DisplayName, TenantId, IsAdmin, IsEnabled, CreatedUtc, UpdatedUtc, LastLoginUtc
            FROM dbo.AuthorizedUsers
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapUser(reader) : null;
    }

    public async Task<int> CreateAsync(AuthorizedUser user, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.AuthorizedUsers
                (Email, EmailNormalized, DisplayName, TenantId, IsAdmin, IsEnabled, CreatedUtc, UpdatedUtc)
            OUTPUT INSERTED.Id
            VALUES
                (@Email, @EmailNormalized, @DisplayName, @TenantId, @IsAdmin, @IsEnabled,
                 SYSUTCDATETIME(), SYSUTCDATETIME());
            """;
        AddUserParameters(command, user);
        return (int)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task UpdateAsync(AuthorizedUser user, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.AuthorizedUsers
            SET Email = @Email,
                EmailNormalized = @EmailNormalized,
                DisplayName = @DisplayName,
                TenantId = @TenantId,
                IsAdmin = @IsAdmin,
                IsEnabled = @IsEnabled,
                UpdatedUtc = SYSUTCDATETIME()
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", user.Id);
        AddUserParameters(command, user);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkLastLoginAsync(string email, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.AuthorizedUsers
            SET LastLoginUtc = SYSUTCDATETIME(),
                UpdatedUtc = SYSUTCDATETIME()
            WHERE EmailNormalized = @EmailNormalized;
            """;
        command.Parameters.AddWithValue("@EmailNormalized", NormalizeEmail(email));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddUserParameters(SqlCommand command, AuthorizedUser user)
    {
        var email = user.Email.Trim();
        command.Parameters.AddWithValue("@Email", email);
        command.Parameters.AddWithValue("@EmailNormalized", NormalizeEmail(email));
        command.Parameters.AddWithValue("@DisplayName", (object?)user.DisplayName?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("@TenantId", (object?)user.TenantId?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("@IsAdmin", user.IsAdmin);
        command.Parameters.AddWithValue("@IsEnabled", user.IsEnabled);
    }

    private static string NormalizeEmail(string email)
    {
        return email.Trim().ToUpperInvariant();
    }

    private static AuthorizedUser MapUser(SqlDataReader reader)
    {
        return new AuthorizedUser
        {
            Id = reader.GetRequiredInt32("Id"),
            Email = reader.GetRequiredString("Email"),
            DisplayName = reader.GetNullableString("DisplayName"),
            TenantId = reader.GetNullableString("TenantId"),
            IsAdmin = reader.GetRequiredBoolean("IsAdmin"),
            IsEnabled = reader.GetRequiredBoolean("IsEnabled"),
            CreatedUtc = reader.GetRequiredDateTime("CreatedUtc"),
            UpdatedUtc = reader.GetRequiredDateTime("UpdatedUtc"),
            LastLoginUtc = reader.GetNullableDateTime("LastLoginUtc")
        };
    }
}
