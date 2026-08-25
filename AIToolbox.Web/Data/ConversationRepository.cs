using AIToolbox.Web.Models;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Web.Data;

public sealed class ConversationRepository : IConversationRepository
{
    private readonly SqlConnectionFactory _connectionFactory;

    public ConversationRepository(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<Conversation?> GetActiveConversationAsync(
        string sub,
        string surface,
        string contextKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Sub, Surface, ContextKey, Status, UserTurnCount, TokensIn, TokensOut,
                   CreatedUtc, LastActiveUtc, ClosedUtc, RolledUp
            FROM dbo.Conversations
            WHERE Sub = @Sub AND Surface = @Surface AND ContextKey = @ContextKey AND Status = 'Active';
            """;
        command.Parameters.AddWithValue("@Sub", sub);
        command.Parameters.AddWithValue("@Surface", surface);
        command.Parameters.AddWithValue("@ContextKey", contextKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapConversation(reader) : null;
    }

    public async Task<Conversation?> GetActiveConversationForSurfaceAsync(
        string sub,
        string surface,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Sub, Surface, ContextKey, Status, UserTurnCount, TokensIn, TokensOut,
                   CreatedUtc, LastActiveUtc, ClosedUtc, RolledUp
            FROM dbo.Conversations
            WHERE Sub = @Sub AND Surface = @Surface AND Status = 'Active';
            """;
        command.Parameters.AddWithValue("@Sub", sub);
        command.Parameters.AddWithValue("@Surface", surface);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapConversation(reader) : null;
    }

    public async Task<Conversation?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Sub, Surface, ContextKey, Status, UserTurnCount, TokensIn, TokensOut,
                   CreatedUtc, LastActiveUtc, ClosedUtc, RolledUp
            FROM dbo.Conversations
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapConversation(reader) : null;
    }

    public async Task<ConversationListResult> GetConversationsAsync(
        ConversationListFilter filter,
        CancellationToken cancellationToken = default)
    {
        var surface = NormalizeSurface(filter.Surface);
        var status = NormalizeStatus(filter.Status);
        var contextKey = string.IsNullOrWhiteSpace(filter.ContextKey) ? null : filter.ContextKey.Trim();
        var page = Math.Max(filter.Page, 1);
        var pageSize = Math.Clamp(filter.PageSize, 10, 200);
        var offset = (page - 1) * pageSize;

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(1)
            FROM dbo.Conversations
            WHERE 1 = 1
                {BuildSurfaceWhereClause(surface)}
                {BuildStatusWhereClause(status)}
                {BuildContextKeyWhereClause(contextKey)};

            SELECT DISTINCT Surface
            FROM dbo.Conversations
            ORDER BY Surface;

            SELECT Id, Sub, Surface, ContextKey, Status, UserTurnCount, TokensIn, TokensOut,
                   CreatedUtc, LastActiveUtc, ClosedUtc, RolledUp
            FROM dbo.Conversations
            WHERE 1 = 1
                {BuildSurfaceWhereClause(surface)}
                {BuildStatusWhereClause(status)}
                {BuildContextKeyWhereClause(contextKey)}
            ORDER BY LastActiveUtc DESC, Id DESC
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;
        command.Parameters.AddWithValue("@Offset", offset);
        command.Parameters.AddWithValue("@PageSize", pageSize);
        if (!string.Equals(surface, "all", StringComparison.OrdinalIgnoreCase))
        {
            command.Parameters.AddWithValue("@Surface", surface);
        }
        if (!string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
        {
            command.Parameters.AddWithValue("@Status", status);
        }
        if (contextKey is not null)
        {
            command.Parameters.AddWithValue("@ContextKey", $"%{contextKey}%");
        }

        var items = new List<Conversation>();
        var surfaces = new List<string>();
        var totalCount = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            totalCount = reader.GetInt32(0);
        }

        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            surfaces.Add(reader.GetRequiredString("Surface"));
        }

        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(MapConversation(reader));
        }

        return new ConversationListResult
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            Surface = surface,
            Status = status,
            ContextKey = contextKey,
            Surfaces = surfaces
        };
    }

    public async Task<int> CreateConversationAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.Conversations (Sub, Surface, ContextKey, Status, CreatedUtc, LastActiveUtc)
            OUTPUT INSERTED.Id
            VALUES (@Sub, @Surface, @ContextKey, 'Active', SYSUTCDATETIME(), SYSUTCDATETIME());
            """;
        command.Parameters.AddWithValue("@Sub", conversation.Sub);
        command.Parameters.AddWithValue("@Surface", conversation.Surface);
        command.Parameters.AddWithValue("@ContextKey", conversation.ContextKey);
        return (int)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(int conversationId, CancellationToken cancellationToken = default)
    {
        var messages = new List<ConversationMessage>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ConversationId, Role, Sequence, ContentJson, ModelId, AiProviderId, CreatedUtc
            FROM dbo.ConversationMessages
            WHERE ConversationId = @ConversationId
            ORDER BY Sequence;
            """;
        command.Parameters.AddWithValue("@ConversationId", conversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(MapMessage(reader));
        }

        return messages;
    }

    public async Task<int> AppendMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.ConversationMessages (ConversationId, Role, Sequence, ContentJson, ModelId, AiProviderId, CreatedUtc)
            OUTPUT INSERTED.Id
            VALUES (@ConversationId, @Role, @Sequence, @ContentJson, @ModelId, @AiProviderId, SYSUTCDATETIME());
            """;
        command.Parameters.AddWithValue("@ConversationId", message.ConversationId);
        command.Parameters.AddWithValue("@Role", message.Role);
        command.Parameters.AddWithValue("@Sequence", message.Sequence);
        command.Parameters.AddWithValue("@ContentJson", message.ContentJson);
        command.Parameters.AddWithValue("@ModelId", (object?)message.ModelId ?? DBNull.Value);
        command.Parameters.AddWithValue("@AiProviderId", (object?)message.AiProviderId ?? DBNull.Value);
        return (int)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task RecordTurnAsync(int conversationId, int tokensInDelta, int tokensOutDelta, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.Conversations
            SET UserTurnCount = UserTurnCount + 1,
                TokensIn = TokensIn + @TokensInDelta,
                TokensOut = TokensOut + @TokensOutDelta,
                LastActiveUtc = SYSUTCDATETIME()
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", conversationId);
        command.Parameters.AddWithValue("@TokensInDelta", tokensInDelta);
        command.Parameters.AddWithValue("@TokensOutDelta", tokensOutDelta);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CloseConversationAsync(int conversationId, ConversationStatus status, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.Conversations
            SET Status = @Status,
                ClosedUtc = COALESCE(ClosedUtc, SYSUTCDATETIME())
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", conversationId);
        command.Parameters.AddWithValue("@Status", status.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<int>> GetPurgeableConversationIdsAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        var ids = new List<int>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id
            FROM dbo.Conversations
            WHERE Status <> 'Active' AND ClosedUtc IS NOT NULL AND ClosedUtc <= @CutoffUtc;
            """;
        command.Parameters.AddWithValue("@CutoffUtc", cutoffUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetRequiredInt32("Id"));
        }

        return ids;
    }

    public async Task DeleteConversationAsync(int conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM dbo.ConversationMessages WHERE ConversationId = @Id;
            DELETE FROM dbo.Conversations WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", conversationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizeSurface(string? surface)
    {
        return string.IsNullOrWhiteSpace(surface) ? "all" : surface.Trim();
    }

    private static string NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status) || string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
        {
            return "all";
        }

        // Status is stored as the enum's own .ToString() (e.g. 'ClosedManual') -- validate against
        // the real enum here rather than trusting the query string directly into SQL.
        return Enum.TryParse<ConversationStatus>(status, ignoreCase: true, out var parsed) ? parsed.ToString() : "all";
    }

    private static string BuildSurfaceWhereClause(string surface)
    {
        return string.Equals(surface, "all", StringComparison.OrdinalIgnoreCase) ? string.Empty : "AND Surface = @Surface";
    }

    private static string BuildStatusWhereClause(string status)
    {
        return string.Equals(status, "all", StringComparison.OrdinalIgnoreCase) ? string.Empty : "AND Status = @Status";
    }

    private static string BuildContextKeyWhereClause(string? contextKey)
    {
        return contextKey is null ? string.Empty : "AND ContextKey LIKE @ContextKey";
    }

    private static Conversation MapConversation(SqlDataReader reader)
    {
        var statusValue = reader.GetRequiredString("Status");
        var status = Enum.TryParse<ConversationStatus>(statusValue, ignoreCase: true, out var parsed)
            ? parsed
            : ConversationStatus.Active;

        return new Conversation
        {
            Id = reader.GetRequiredInt32("Id"),
            Sub = reader.GetRequiredString("Sub"),
            Surface = reader.GetRequiredString("Surface"),
            ContextKey = reader.GetRequiredString("ContextKey"),
            Status = status,
            UserTurnCount = reader.GetRequiredInt32("UserTurnCount"),
            TokensIn = reader.GetRequiredInt32("TokensIn"),
            TokensOut = reader.GetRequiredInt32("TokensOut"),
            CreatedUtc = reader.GetRequiredDateTime("CreatedUtc"),
            LastActiveUtc = reader.GetRequiredDateTime("LastActiveUtc"),
            ClosedUtc = reader.GetNullableDateTime("ClosedUtc"),
            RolledUp = reader.GetRequiredBoolean("RolledUp")
        };
    }

    private static ConversationMessage MapMessage(SqlDataReader reader)
    {
        return new ConversationMessage
        {
            Id = reader.GetRequiredInt32("Id"),
            ConversationId = reader.GetRequiredInt32("ConversationId"),
            Role = reader.GetRequiredString("Role"),
            Sequence = reader.GetRequiredInt32("Sequence"),
            ContentJson = reader.GetRequiredString("ContentJson"),
            ModelId = reader.GetNullableString("ModelId"),
            AiProviderId = reader.GetNullableInt32("AiProviderId"),
            CreatedUtc = reader.GetRequiredDateTime("CreatedUtc")
        };
    }
}
