using System.Security.Cryptography;
using System.Text;
using AIToolbox.Ductwork.Contracts;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Data;

/// <summary>
/// What a provider looked like when a result was produced. Provider rows in AIToolbox are mutable, so
/// anything that records provenance stores this snapshot instead. It never contains the API key.
/// </summary>
public sealed record ProviderSnapshotInfo(
    int ProviderType,
    string EndpointHost,
    string ModelName,
    string? ApiVersion,
    int? MaxOutputTokens,
    bool SupportsVision,
    string? GovernanceRoute)
{
    public string ComputeHash()
    {
        var canonical = string.Join('\n',
            ProviderType,
            EndpointHost.Trim().ToLowerInvariant(),
            ModelName.Trim(),
            ApiVersion?.Trim() ?? "",
            MaxOutputTokens?.ToString() ?? "",
            SupportsVision ? "1" : "0",
            GovernanceRoute?.Trim() ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

/// <summary>
/// Reuses one row per distinct producer and provider snapshot. Inserts are race-safe: a concurrent
/// duplicate loses to the unique key and re-reads the winner, rather than check-then-insert.
/// </summary>
public static class ProducerStore
{
    private const int UniqueKeyViolation = 2627;
    private const int UniqueIndexViolation = 2601;

    public static async Task<int> EnsureProviderSnapshotAsync(
        SqlConnection connection, ProviderSnapshotInfo info, CancellationToken cancellationToken = default)
    {
        var hash = info.ComputeHash();
        var existing = await FindProviderSnapshotAsync(connection, hash, cancellationToken);
        if (existing is not null) return existing.Value;

        try
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO ductwork.ProviderSnapshot (ProviderType, EndpointHost, ModelName, ApiVersion, MaxOutputTokens, SupportsVision, GovernanceRoute, ContentHash)
                VALUES (@t, @h, @m, @v, @x, @s, @g, @c);
                SELECT CAST(SCOPE_IDENTITY() AS INT);
                """;
            insert.Parameters.AddWithValue("@t", info.ProviderType);
            insert.Parameters.AddWithValue("@h", info.EndpointHost.Trim().ToLowerInvariant());
            insert.Parameters.AddWithValue("@m", info.ModelName.Trim());
            insert.Parameters.AddWithValue("@v", (object?)info.ApiVersion ?? DBNull.Value);
            insert.Parameters.AddWithValue("@x", (object?)info.MaxOutputTokens ?? DBNull.Value);
            insert.Parameters.AddWithValue("@s", info.SupportsVision);
            insert.Parameters.AddWithValue("@g", (object?)info.GovernanceRoute ?? DBNull.Value);
            insert.Parameters.AddWithValue("@c", hash);
            return (int)(await insert.ExecuteScalarAsync(cancellationToken))!;
        }
        catch (SqlException ex) when (ex.Number is UniqueKeyViolation or UniqueIndexViolation)
        {
            return await FindProviderSnapshotAsync(connection, hash, cancellationToken)
                ?? throw new InvalidOperationException("Provider snapshot vanished after a duplicate-key conflict.", ex);
        }
    }

    public static async Task<long> EnsureProducerAsync(
        SqlConnection connection, ProducerRef producer, CancellationToken cancellationToken = default)
    {
        var key = producer.Key(); // also validates
        var existing = await FindProducerAsync(connection, key, cancellationToken);
        if (existing is not null) return existing.Value;

        try
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO ductwork.Producer (ProducerKey, Kind, DirectiveSnapshotId, ProviderSnapshotId, AlgorithmName, AlgorithmVersion, ParamsHash, UserEmail)
                VALUES (@k, @kind, @d, @p, @an, @av, @ph, @u);
                SELECT CAST(SCOPE_IDENTITY() AS BIGINT);
                """;
            insert.Parameters.AddWithValue("@k", key);
            insert.Parameters.AddWithValue("@kind", producer.Kind.ToString());
            insert.Parameters.AddWithValue("@d", (object?)producer.DirectiveSnapshotId ?? DBNull.Value);
            insert.Parameters.AddWithValue("@p", (object?)producer.ProviderSnapshotId ?? DBNull.Value);
            insert.Parameters.AddWithValue("@an", (object?)producer.AlgorithmName?.Trim() ?? DBNull.Value);
            insert.Parameters.AddWithValue("@av", (object?)producer.AlgorithmVersion?.Trim() ?? DBNull.Value);
            insert.Parameters.AddWithValue("@ph", (object?)producer.ParamsHash?.Trim() ?? DBNull.Value);
            insert.Parameters.AddWithValue("@u", (object?)producer.UserEmail?.Trim() ?? DBNull.Value);
            return (long)(await insert.ExecuteScalarAsync(cancellationToken))!;
        }
        catch (SqlException ex) when (ex.Number is UniqueKeyViolation or UniqueIndexViolation)
        {
            return await FindProducerAsync(connection, key, cancellationToken)
                ?? throw new InvalidOperationException("Producer vanished after a duplicate-key conflict.", ex);
        }
    }

    private static async Task<int?> FindProviderSnapshotAsync(SqlConnection connection, string hash, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ProviderSnapshotId FROM ductwork.ProviderSnapshot WHERE ContentHash = @c;";
        command.Parameters.AddWithValue("@c", hash);
        return await command.ExecuteScalarAsync(cancellationToken) as int?;
    }

    private static async Task<long?> FindProducerAsync(SqlConnection connection, string key, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ProducerId FROM ductwork.Producer WHERE ProducerKey = @k;";
        command.Parameters.AddWithValue("@k", key);
        return await command.ExecuteScalarAsync(cancellationToken) as long?;
    }
}
