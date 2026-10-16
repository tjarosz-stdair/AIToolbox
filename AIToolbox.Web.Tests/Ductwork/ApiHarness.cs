using System.Security.Claims;
using System.Text.Json;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Activity;
using AIToolbox.Ductwork.Core.Counting;
using AIToolbox.Ductwork.Core.Drawings;
using AIToolbox.Ductwork.Core.Rules;
using AIToolbox.Ductwork.Core.Data;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Tasks;
using AIToolbox.Web.Controllers.Ductwork;
using AIToolbox.Web.Ductwork;
using AIToolbox.Web.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace AIToolbox.Web.Tests.Ductwork;

/// <summary>A throwaway LocalDB database (or the server in AITOOLBOX_TEST_SQL) with the ductwork schema applied.</summary>
public sealed class DuctworkDatabase : IAsyncLifetime
{
    private const string DefaultServer = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true;";
    private readonly string _server = Environment.GetEnvironmentVariable("AITOOLBOX_TEST_SQL") ?? DefaultServer;
    private readonly string _name = "DuctworkWebTest_" + Guid.NewGuid().ToString("N");

    public string ConnectionString => new SqlConnectionStringBuilder(_server) { InitialCatalog = _name }.ConnectionString;

    public async Task InitializeAsync()
    {
        await using var master = new SqlConnection(_server);
        await master.OpenAsync();
        await using var create = master.CreateCommand();
        create.CommandText = $"CREATE DATABASE [{_name}];";
        await create.ExecuteNonQueryAsync();
        await new MigrationRunner().MigrateAsync(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection(_server);
        await master.OpenAsync();
        await using var drop = master.CreateCommand();
        drop.CommandText = $"IF DB_ID('{_name}') IS NOT NULL BEGIN ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}]; END";
        await drop.ExecuteNonQueryAsync();
    }

    public async Task ExecAsync(string sql, params (string, object?)[] args)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (n, v) in args) command.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql, params (string, object?)[] args)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (n, v) in args) command.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T));
    }
}

/// <summary>What a controller call returned: the HTTP status and the JSON body, as a client would see them.</summary>
public sealed record Reply(int Status, JsonElement Body)
{
    /// <summary>The content of a file answer. The caller disposes it.</summary>
    public Stream? Stream { get; init; }

    public JsonElement this[string property] => Body.GetProperty(property);
    public string Code => Body.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
    public bool Ok => Status is >= 200 and < 300;
}

/// <summary>
/// Builds the real controllers over a real database, with authenticated principals and the headers a browser
/// would send. Calls go through the same exception mapping the app uses, so what a test sees is what a client gets.
/// </summary>
public sealed class ApiHarness(DuctworkDatabase db)
{
    public const string Owner = "owner@example.com";
    public const string Editor = "editor@example.com";
    public const string Viewer = "viewer@example.com";
    public const string Stranger = "stranger@example.com";
    public const string Admin = "admin@example.com";

    private Func<CancellationToken, Task<SqlConnection>> Open => async ct =>
    {
        var connection = new SqlConnection(db.ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    };

    public LockSettings Locks { get; init; } = new();
    public TaskSettings TaskSettings { get; init; } = new();

    public JobRepository JobRepo => new(Open);
    public EditLockService LockService => new(Open, Locks);
    public ProcessingTaskRepository TaskRepo => new(Open, TaskSettings);

    /// <summary>A private folder for this harness's drawing files, so tests never share storage.</summary>
    public string ArtifactRoot { get; } = Path.Combine(Path.GetTempPath(), "ductwork-web-artifacts-" + Guid.NewGuid().ToString("N"));

    public UploadService Uploads => new(Open, new FileSystemArtifactStore(ArtifactRoot), new UploadSettings { ChunkBytes = 1000 });
    public TaskBindingRepository BindingRepo => new(Open);

    private static int _tab;
    public static string NewTab() => "tab-" + Interlocked.Increment(ref _tab).ToString("D8");

    public static ClaimsPrincipal Person(string email, bool admin = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.Email, email), new(ClaimTypes.Name, email.Split('@')[0]) };
        if (admin) claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    public static ClaimsPrincipal ApiConsumer(int id, string name, params string[] scopes)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Role, "ApiConsumer"), new(ApiConsumerClaimTypes.ConsumerId, id.ToString()), new(ApiConsumerClaimTypes.ConsumerName, name)
        };
        claims.AddRange(scopes.Select(s => new Claim(ApiConsumerClaimTypes.Scope, s)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static T With<T>(T controller, ClaimsPrincipal user, string? tab, long? fencing) where T : ControllerBase
    {
        var http = new DefaultHttpContext { User = user };
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.1.2.3");
        http.Request.Headers.UserAgent = "tests";
        if (tab is not null) http.Request.Headers[DuctworkTool.TabHeader] = tab;
        if (fencing is not null) http.Request.Headers[DuctworkTool.FencingHeader] = fencing.Value.ToString();
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    public DuctworkJobsController JobsApi(string email, string? tab = null, long? fencing = null, bool admin = false) =>
        With(new DuctworkJobsController(JobRepo, LockService, TaskRepo, new EntityRepository(Open), new CountSheetRepository(Open),
            new ActivityRepository(Open, Locks.HeartbeatInterval)), Person(email, admin), tab ?? NewTab(), fencing);

    public DuctworkLockController LockApi(string email, string tab, long? fencing = null, bool admin = false) =>
        With(new DuctworkLockController(JobRepo, LockService), Person(email, admin), tab, fencing);

    public DuctworkBuildingTypesController TypesApi(string email, string tab, long? fencing = null) =>
        With(new DuctworkBuildingTypesController(JobRepo, LockService, new BuildingTypeRepository(Open)), Person(email), tab, fencing);

    public DuctworkUploadsController UploadsApi(string email, string tab, long? fencing = null, Stream? body = null, string? chunkSha256 = null)
    {
        var controller = With(new DuctworkUploadsController(JobRepo, LockService, Uploads), Person(email), tab, fencing);
        if (body is not null)
        {
            controller.HttpContext.Request.Body = body;
            if (body.CanSeek) controller.HttpContext.Request.ContentLength = body.Length;
        }

        if (chunkSha256 is not null) controller.HttpContext.Request.Headers[DuctworkUploadsController.ChunkChecksumHeader] = chunkSha256;
        return controller;
    }

    public DuctworkAdminController AdminApi(string email = Admin, string? tab = null) =>
        With(new DuctworkAdminController(BindingRepo), Person(email, admin: true), tab ?? NewTab(), null);

    public DuctworkWorkerApiController WorkerApi(int consumerId = 1, string consumerName = "worker-key", string[]? scopes = null, int bodyLimit = 0,
        Stream? body = null, IOptions<DuctworkOptions>? options = null)
    {
        var controller = new DuctworkWorkerApiController(TaskRepo, new TaskResultIngestor(Open), new SheetRepository(Open), Uploads, new BuildingTypeRepository(Open), Open, Locks, TaskSettings, options ?? Options.Create(new DuctworkOptions()));
        var principal = ApiConsumer(consumerId, consumerName, scopes ?? [KnownApiScopes.DuctworkWorker, "tool:" + DuctworkTool.ToolKey]);
        With(controller, principal, null, null);
        if (body is not null)
        {
            controller.HttpContext.Request.Body = body;
            if (body.CanSeek) controller.HttpContext.Request.ContentLength = body.Length;
        }

        return controller;
    }

    /// <summary>Runs a controller action the way the app does: a data-layer refusal becomes its HTTP answer.</summary>
    public static async Task<Reply> RunAsync(Func<Task<IActionResult>> action)
    {
        IActionResult result;
        try
        {
            result = await action();
        }
        catch (Exception ex)
        {
            var mapped = DuctworkApiExceptionAttribute.Map(ex);
            if (mapped is null) throw;
            result = DuctworkJson.Result(mapped.Value.Body, mapped.Value.Status);
        }

        switch (result)
        {
            case JsonResult json:
            {
                var text = JsonSerializer.Serialize(json.Value, DuctworkJson.Options);
                return new Reply(json.StatusCode ?? 200, JsonDocument.Parse(text).RootElement.Clone());
            }
            case NoContentResult:
                return new Reply(204, JsonDocument.Parse("null").RootElement.Clone());
            case FileStreamResult file:
                return new Reply(200, JsonDocument.Parse(JsonSerializer.Serialize(new { contentType = file.ContentType, downloadName = file.FileDownloadName })).RootElement.Clone())
                    { Stream = file.FileStream };
            case StatusCodeResult status:
                return new Reply(status.StatusCode, JsonDocument.Parse("null").RootElement.Clone());
            default:
                throw new InvalidOperationException($"Unexpected result {result.GetType().Name}.");
        }
    }

    // ------------------------------------------------------------------ common set-up

    /// <summary>A job owned by <see cref="Owner"/> with an editor and a viewer.</summary>
    public async Task<long> NewJobAsync(string? name = null)
    {
        var created = await RunAsync(() => JobsApi(Owner).Create(new CreateJobRequest(name ?? "Api job " + Guid.NewGuid().ToString("N")[..8], null, null, null, null), default));
        var id = created["jobId"].GetInt64();
        await JobRepo.AddMemberAsync(id, Editor, JobRole.Editor, AuditContext.ForUser(Owner, sessionId: "setup"));
        await JobRepo.AddMemberAsync(id, Viewer, JobRole.Viewer, AuditContext.ForUser(Owner, sessionId: "setup"));
        return id;
    }

    /// <summary>The editor's tab takes the lock and returns the fencing token the browser would hold.</summary>
    public async Task<(string Tab, long Token)> EditorLocksAsync(long jobId, string email = Editor)
    {
        var tab = NewTab();
        var reply = await RunAsync(() => LockApi(email, tab).Acquire(jobId, default));
        return (tab, reply["fencingToken"].GetInt64());
    }

    public DuctworkRulesController RulesApi(string email, string tab, long? fencing = null) =>
        With(new DuctworkRulesController(JobRepo, LockService, new RuleRepository(Open), new FindingRepository(Open), new SheetRepository(Open)), Person(email), tab, fencing);

    public DuctworkReviewController ReviewApi(string email, string tab, long? fencing = null) =>
        With(new DuctworkReviewController(JobRepo, LockService, new EntityReviewRepository(Open)), Person(email), tab, fencing);

    public DuctworkSheetsController SheetsApi(string email, string tab, long? fencing = null) =>
        With(new DuctworkSheetsController(JobRepo, LockService, new SheetRepository(Open)), Person(email), tab, fencing);

    /// <summary>Uploads a small PDF through the real upload endpoints and returns its public file id.</summary>
    public async Task<Guid> UploadedFileAsync(long jobId, string tab, long token, byte seed = 5)
    {
        var data = new byte[400];
        Array.Fill(data, seed);
        System.Text.Encoding.ASCII.GetBytes("%PDF-1.7\n").CopyTo(data, 0);
        var begun = await RunAsync(() => UploadsApi(Editor, tab, token).Begin(jobId, new BeginUploadRequest("sheet-" + seed + ".pdf", data.Length, null), default));
        var upload = begun["uploadId"].GetGuid();
        await RunAsync(() => UploadsApi(Editor, tab, body: new MemoryStream(data)).Append(jobId, upload, 0, default));
        return (await RunAsync(() => UploadsApi(Editor, tab, token).Complete(jobId, upload, default)))["fileId"].GetGuid();
    }

    public async Task<long> ProducerAsync(ProducerRef producer)
    {
        await using var connection = new SqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        return await ProducerStore.EnsureProducerAsync(connection, producer);
    }

    public async Task<int> EvalRunAsync(long producerId)
    {
        var gold = await db.ScalarAsync<int>("INSERT INTO ductwork.GoldSet (Name, Role) OUTPUT INSERTED.GoldSetId VALUES (@n, 'Development');", ("@n", "gs-" + Guid.NewGuid().ToString("N")));
        return await db.ScalarAsync<int>(
            "INSERT INTO ductwork.EvalRun (GoldSetId, ProducerId, TaskKey, MatchMode, ContractVersion) VALUES (@g, @p, 'bind', 'Count', '0.1.0'); SELECT CAST(SCOPE_IDENTITY() AS INT);",
            ("@g", gold), ("@p", producerId));
    }
}

public static class JsonExtensions
{
    /// <summary>Reads a number that the API wrote as a JSON number, whichever integer width it has.</summary>
    public static long GetInt64Safe(this JsonElement element) => element.GetInt64();
}
