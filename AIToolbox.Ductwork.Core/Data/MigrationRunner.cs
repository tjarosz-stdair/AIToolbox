using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Data;

/// <summary>One embedded, ordered migration script.</summary>
public sealed record Migration(int Version, string Name, string Sql, string Checksum);

public sealed record MigrationResult(IReadOnlyList<int> Applied, IReadOnlyList<int> AlreadyApplied);

public sealed class MigrationTamperedException(int version, string name)
    : Exception($"Migration {version:D4}_{name} was changed after it was applied. Add a new migration instead of editing an applied one.");

/// <summary>
/// Ordered, versioned migration runner for the <c>ductwork</c> schema (plan section 6.6). It is
/// separate from AIToolbox's startup initializer: scripts are embedded resources named
/// <c>NNNN_name.sql</c>, applied in order inside one transaction each, serialized across instances
/// with an application lock, and verified by checksum so an applied script can never be edited.
/// Batches inside a script are separated by a line containing only <c>GO</c>.
/// </summary>
public sealed partial class MigrationRunner
{
    public const string Schema = "ductwork";
    private const string LockResource = "ductwork-migrations";

    private readonly IReadOnlyList<Migration> _migrations;

    public MigrationRunner() : this(LoadEmbedded(typeof(MigrationRunner).Assembly)) { }

    public MigrationRunner(IReadOnlyList<Migration> migrations)
    {
        var ordered = migrations.OrderBy(m => m.Version).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Version == ordered[i - 1].Version)
                throw new InvalidOperationException($"Duplicate migration version {ordered[i].Version:D4}.");
        }

        _migrations = ordered;
    }

    public IReadOnlyList<Migration> Migrations => _migrations;

    public static IReadOnlyList<Migration> LoadEmbedded(Assembly assembly)
    {
        var list = new List<Migration>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var match = ResourceNameRegex().Match(resource);
            if (!match.Success) continue;

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var sql = reader.ReadToEnd();
            list.Add(new Migration(int.Parse(match.Groups[1].Value), match.Groups[2].Value, sql, Checksum(sql)));
        }

        return list.OrderBy(m => m.Version).ToList();
    }

    /// <summary>Line endings are normalized so a git checkout setting cannot change the checksum.</summary>
    public static string Checksum(string sql)
    {
        var normalized = sql.Replace("\r\n", "\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    public static IReadOnlyList<string> SplitBatches(string sql) =>
        BatchSeparatorRegex().Split(sql).Select(b => b.Trim()).Where(b => b.Length > 0).ToList();

    public async Task<MigrationResult> MigrateAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await AcquireLockAsync(connection, cancellationToken);
        try
        {
            await ExecuteAsync(connection, null,
                $"IF SCHEMA_ID('{Schema}') IS NULL EXEC('CREATE SCHEMA {Schema}');", cancellationToken);
            await ExecuteAsync(connection, null,
                $"""
                IF OBJECT_ID('{Schema}.SchemaVersion', 'U') IS NULL
                CREATE TABLE {Schema}.SchemaVersion
                (
                    Version    INT NOT NULL CONSTRAINT PK_SchemaVersion PRIMARY KEY,
                    Name       NVARCHAR(200) NOT NULL,
                    Checksum   CHAR(64) NOT NULL,
                    AppliedUtc DATETIME2 NOT NULL CONSTRAINT DF_SchemaVersion_AppliedUtc DEFAULT (SYSUTCDATETIME())
                );
                """, cancellationToken);

            var recorded = await ReadAppliedAsync(connection, cancellationToken);
            var applied = new List<int>();
            var already = new List<int>();

            foreach (var migration in _migrations)
            {
                if (recorded.TryGetValue(migration.Version, out var existing))
                {
                    if (!string.Equals(existing, migration.Checksum, StringComparison.OrdinalIgnoreCase))
                        throw new MigrationTamperedException(migration.Version, migration.Name);
                    already.Add(migration.Version);
                    continue;
                }

                await ApplyAsync(connection, migration, cancellationToken);
                applied.Add(migration.Version);
            }

            return new MigrationResult(applied, already);
        }
        finally
        {
            await ReleaseLockAsync(connection);
        }
    }

    private static async Task ApplyAsync(SqlConnection connection, Migration migration, CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var batch in SplitBatches(migration.Sql))
                await ExecuteAsync(connection, transaction, batch, cancellationToken);

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"INSERT INTO {Schema}.SchemaVersion (Version, Name, Checksum) VALUES (@v, @n, @c);";
            insert.Parameters.AddWithValue("@v", migration.Version);
            insert.Parameters.AddWithValue("@n", migration.Name);
            insert.Parameters.AddWithValue("@c", migration.Checksum);
            await insert.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<Dictionary<int, string>> ReadAppliedAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, string>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Version, Checksum FROM {Schema}.SchemaVersion;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result[reader.GetInt32(0)] = reader.GetString(1).Trim();
        return result;
    }

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AcquireLockAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXEC @result = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 60000;";
        command.Parameters.AddWithValue("@res", LockResource);
        var result = command.Parameters.Add("@result", System.Data.SqlDbType.Int);
        result.Direction = System.Data.ParameterDirection.Output;
        await command.ExecuteNonQueryAsync(cancellationToken);
        if ((int)result.Value < 0)
            throw new TimeoutException("Could not acquire the Ductwork migration lock within 60 seconds.");
    }

    private static async Task ReleaseLockAsync(SqlConnection connection)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "EXEC sp_releaseapplock @Resource = @res, @LockOwner = 'Session';";
            command.Parameters.AddWithValue("@res", LockResource);
            await command.ExecuteNonQueryAsync();
        }
        catch
        {
            // The lock is session scoped and disappears with the connection.
        }
    }

    [GeneratedRegex(@"\.(\d{4})_([A-Za-z0-9_]+)\.sql$")]
    private static partial Regex ResourceNameRegex();

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex BatchSeparatorRegex();
}
