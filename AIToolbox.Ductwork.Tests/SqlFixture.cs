using AIToolbox.Ductwork.Core.Data;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Tests;

/// <summary>
/// A throwaway database on LocalDB (or the server named by DUCTWORK_TEST_SQL) with the Ductwork
/// migrations applied, dropped afterwards. T-SQL constraints and triggers are part of the design,
/// so they are tested against a real engine, not mocked.
/// </summary>
public sealed class SqlFixture : IAsyncLifetime
{
    private const string DefaultServer = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true;";

    private readonly string _serverConnection =
        Environment.GetEnvironmentVariable("DUCTWORK_TEST_SQL") ?? DefaultServer;

    public string DatabaseName { get; } = "DuctworkTest_" + Guid.NewGuid().ToString("N");

    public string ConnectionString =>
        new SqlConnectionStringBuilder(_serverConnection) { InitialCatalog = DatabaseName }.ConnectionString;

    public async Task InitializeAsync()
    {
        await using var master = new SqlConnection(_serverConnection);
        await master.OpenAsync();
        await using var create = master.CreateCommand();
        create.CommandText = $"CREATE DATABASE [{DatabaseName}];";
        await create.ExecuteNonQueryAsync();

        await new MigrationRunner().MigrateAsync(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection(_serverConnection);
        await master.OpenAsync();
        await using var drop = master.CreateCommand();
        drop.CommandText =
            $"IF DB_ID('{DatabaseName}') IS NOT NULL BEGIN ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}]; END";
        await drop.ExecuteNonQueryAsync();
    }

    public async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task<int> ExecAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<T?> ScalarAsync<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }
}
