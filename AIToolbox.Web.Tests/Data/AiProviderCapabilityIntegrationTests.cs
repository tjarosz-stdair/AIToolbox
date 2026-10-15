using AIToolbox.Web.Configuration;
using AIToolbox.Web.Data;
using AIToolbox.Web.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AIToolbox.Web.Tests.Data;

/// <summary>
/// Runs the real startup schema script and the real repository against a throwaway LocalDB database
/// (or the server in AITOOLBOX_TEST_SQL). The new provider capability columns must be added to an
/// existing database without disturbing existing rows, and read and written by the repository.
/// </summary>
public sealed class AiProviderCapabilityIntegrationTests : IAsyncLifetime
{
    private const string DefaultServer = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true;";

    private readonly string _server = Environment.GetEnvironmentVariable("AITOOLBOX_TEST_SQL") ?? DefaultServer;
    private readonly string _database = "AIToolboxTest_" + Guid.NewGuid().ToString("N");

    private string ConnectionString => new SqlConnectionStringBuilder(_server) { InitialCatalog = _database }.ConnectionString;

    public async Task InitializeAsync()
    {
        await using var master = new SqlConnection(_server);
        await master.OpenAsync();
        await using var create = master.CreateCommand();
        create.CommandText = $"CREATE DATABASE [{_database}];";
        await create.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection(_server);
        await master.OpenAsync();
        await using var drop = master.CreateCommand();
        drop.CommandText =
            $"IF DB_ID('{_database}') IS NOT NULL BEGIN ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END";
        await drop.ExecuteNonQueryAsync();
    }

    private SqlConnectionFactory Factory() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:AITools"] = ConnectionString })
        .Build());

    private Task InitializeSchemaAsync() =>
        new DatabaseInitializer(Factory(), Options.Create(new MicrosoftEntraOptions()), NullLogger<DatabaseInitializer>.Instance)
            .InitializeAsync();

    private async Task ExecAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Initializer_is_idempotent_and_adds_the_capability_columns_with_safe_defaults()
    {
        await InitializeSchemaAsync();
        await InitializeSchemaAsync();

        var repository = new AiProviderRepository(Factory());
        var id = await repository.CreateAsync(new AiProvider
        {
            DisplayName = "Plain",
            EndpointUrl = "https://example.invalid/v1/chat",
            ModelName = "m"
        });

        var loaded = (await repository.GetByIdAsync(id))!;
        Assert.False(loaded.SupportsVision);
        Assert.False(loaded.AllowedForDuctwork);
        Assert.Null(loaded.GovernanceRoute);
    }

    [Fact]
    public async Task Existing_rows_survive_the_upgrade_and_get_the_off_by_default_values()
    {
        await InitializeSchemaAsync();
        var repository = new AiProviderRepository(Factory());
        var id = await repository.CreateAsync(new AiProvider { DisplayName = "Old", EndpointUrl = "https://example.invalid/x", ModelName = "m" });

        // Put the table back to the shape it had before this change, with a row in it.
        await ExecAsync("""
            ALTER TABLE dbo.AiProviders DROP CONSTRAINT DF_AiProviders_SupportsVision;
            ALTER TABLE dbo.AiProviders DROP CONSTRAINT DF_AiProviders_AllowedForDuctwork;
            ALTER TABLE dbo.AiProviders DROP COLUMN SupportsVision, AllowedForDuctwork, GovernanceRoute;
            """);

        await InitializeSchemaAsync();

        var loaded = (await repository.GetByIdAsync(id))!;
        Assert.Equal("Old", loaded.DisplayName);
        Assert.False(loaded.SupportsVision);
        Assert.False(loaded.AllowedForDuctwork);
        Assert.Null(loaded.GovernanceRoute);
    }

    [Fact]
    public async Task Repository_round_trips_the_capability_flags_on_create_and_on_both_update_paths()
    {
        await InitializeSchemaAsync();
        var repository = new AiProviderRepository(Factory());

        var id = await repository.CreateAsync(new AiProvider
        {
            DisplayName = "Vision",
            ProviderType = AiProviderType.Anthropic,
            EndpointUrl = "https://example.invalid/anthropic",
            ModelName = "model-x",
            SupportsVision = true,
            AllowedForDuctwork = true,
            GovernanceRoute = "  Microsoft Foundry  ",
            ApiKeyCipherText = "cipher"
        });

        var created = (await repository.GetByIdAsync(id))!;
        Assert.True(created.SupportsVision);
        Assert.True(created.AllowedForDuctwork);
        Assert.Equal("Microsoft Foundry", created.GovernanceRoute);

        // Update without touching the secret.
        created.SupportsVision = false;
        created.GovernanceRoute = null;
        await repository.UpdateAsync(created, updateSecret: false);
        var second = (await repository.GetByIdAsync(id))!;
        Assert.False(second.SupportsVision);
        Assert.True(second.AllowedForDuctwork);
        Assert.Null(second.GovernanceRoute);
        Assert.Equal("cipher", second.ApiKeyCipherText);

        // Update with the secret.
        second.AllowedForDuctwork = false;
        second.GovernanceRoute = "Foundry";
        second.ApiKeyCipherText = "cipher2";
        await repository.UpdateAsync(second, updateSecret: true);
        var third = (await repository.GetByIdAsync(id))!;
        Assert.False(third.AllowedForDuctwork);
        Assert.Equal("Foundry", third.GovernanceRoute);
        Assert.Equal("cipher2", third.ApiKeyCipherText);

        var all = await repository.GetAllAsync(includeDisabled: true);
        Assert.Contains(all, p => p.Id == id && p.GovernanceRoute == "Foundry");
    }
}
