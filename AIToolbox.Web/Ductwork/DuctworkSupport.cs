using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIToolbox.Ductwork.Core.Activity;
using AIToolbox.Ductwork.Core.Counting;
using AIToolbox.Ductwork.Core.Data;
using AIToolbox.Ductwork.Core.Drawings;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Review;
using AIToolbox.Ductwork.Core.Tasks;
using AIToolbox.Web.Data;
using AIToolbox.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace AIToolbox.Web.Ductwork;

public static class DuctworkTool
{
    /// <summary>The tool's key in the registry and the suffix of its API scope (tool:DuctworkTakeoff).</summary>
    public const string ToolKey = "DuctworkTakeoff";

    public const string TabHeader = "X-Ductwork-Tab";
    public const string FencingHeader = "X-Ductwork-Fencing-Token";
}

public static class DuctworkUploadLimits
{
    /// <summary>The largest single piece of an upload. The request-size limit on the endpoint is this plus headroom.</summary>
    public const long MaxChunkBytes = 24L * 1024 * 1024;

    public const long MaxRequestBytes = 32L * 1024 * 1024;
}

public static class DuctworkPolicies
{
    /// <summary>A signed-in person with access to the Ductwork tool. An API key is not a person, so it is refused here.</summary>
    public const string User = "DuctworkUser";

    /// <summary>The central worker: an API consumer holding the worker scope and the tool scope.</summary>
    public const string Worker = "DuctworkWorker";
}

public sealed class DuctworkOptions
{
    /// <summary>
    /// Applies the ductwork schema migrations when the app starts. Off unless explicitly turned on, so
    /// deploying the app never changes a production database as a side effect: the schema is applied by a
    /// deliberate step.
    /// </summary>
    public bool RunMigrationsOnStartup { get; set; }

    public int LockExpirySeconds { get; set; } = 60;
    public int LockHeartbeatSeconds { get; set; } = 15;
    public int TakeoverTimeoutSeconds { get; set; } = 60;
    public int TaskLeaseSeconds { get; set; } = 120;
    public int TaskRetryBackoffSeconds { get; set; } = 15;

    /// <summary>The largest result a worker may submit for one task.</summary>
    public long MaxResultBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>
    /// The folder drawing files are stored in. Blank means <c>App_Data/ductwork</c> under the app. Point it at
    /// storage that is backed up and has room for the drawings; the app pool needs to write there.
    /// </summary>
    public string? ArtifactRoot { get; set; }

    /// <summary>The size the browser cuts a file into. Kept well under the request limits of IIS and the proxy in front of it.</summary>
    public long UploadChunkBytes { get; set; } = 8L * 1024 * 1024;

    public long MaxUploadBytes { get; set; } = 1024L * 1024 * 1024;
    public int UploadSessionHours { get; set; } = 24;
    public int MaxOpenUploadsPerJob { get; set; } = 5;

    public UploadSettings ToUploadSettings() => new()
    {
        ChunkBytes = Math.Clamp(UploadChunkBytes, 64 * 1024, DuctworkUploadLimits.MaxChunkBytes),
        MaxFileBytes = Math.Max(1024 * 1024, MaxUploadBytes),
        SessionLifetime = TimeSpan.FromHours(Math.Max(1, UploadSessionHours)),
        MaxOpenPerJob = Math.Max(1, MaxOpenUploadsPerJob)
    };

    public LockSettings ToLockSettings() => new()
    {
        Expiry = TimeSpan.FromSeconds(Math.Max(5, LockExpirySeconds)),
        HeartbeatInterval = TimeSpan.FromSeconds(Math.Max(1, LockHeartbeatSeconds)),
        TakeoverTimeout = TimeSpan.FromSeconds(Math.Max(1, TakeoverTimeoutSeconds))
    };

    public TaskSettings ToTaskSettings() => new()
    {
        LeaseDuration = TimeSpan.FromSeconds(Math.Max(10, TaskLeaseSeconds)),
        RetryBackoffBase = TimeSpan.FromSeconds(Math.Max(1, TaskRetryBackoffSeconds))
    };
}

/// <summary>JSON for Ductwork responses: camelCase, enums as their names, nulls written, so clients in any language read them the same way.</summary>
public static class DuctworkJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static IActionResult Result(object? value, int status = 200) => new JsonResult(value, Options) { StatusCode = status };
}

/// <summary>A request the server cannot act on because of something the caller sent.</summary>
public sealed class DuctworkBadRequestException(string message) : ArgumentException(message);

public static class DuctworkParse
{
    public static T Enum<T>(string? value, string field) where T : struct, Enum =>
        System.Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && System.Enum.IsDefined(parsed)
            ? parsed
            : throw new DuctworkBadRequestException($"'{value}' is not a valid {field}. Use one of: {string.Join(", ", System.Enum.GetNames<T>())}.");

    public static T? OptionalEnum<T>(string? value, string field) where T : struct, Enum =>
        string.IsNullOrWhiteSpace(value) ? null : Enum<T>(value, field);
}

/// <summary>
/// Builds the "who is acting" record from the authenticated request, never from the request body. The
/// browser supplies only a tab identifier; the session is derived from the signed-in person plus that tab,
/// so one person's tabs are separate sessions and nobody can present another person's session.
/// </summary>
public static class DuctworkAuditContexts
{
    private const int MaxUserAgent = 400;

    public static AuditContext ForUser(HttpContext http, string? reason = null)
    {
        var email = http.User.GetEmailAddress();
        if (string.IsNullOrWhiteSpace(email)) throw new DuctworkBadRequestException("The signed-in user has no email address.");

        return AuditContext.ForUser(
            email,
            displayName: Truncate(http.User.Identity?.Name, 200),
            sessionId: SessionFor(email, TabId(http)),
            clientIp: http.Connection.RemoteIpAddress?.ToString(),
            userAgent: Truncate(http.Request.Headers.UserAgent.ToString(), MaxUserAgent),
            isAdmin: http.User.IsInRole("Admin"),
            reason: reason);
    }

    /// <summary>
    /// A worker is identified by the API consumer that authenticated, plus the name it gives itself. The
    /// consumer part cannot be chosen, so one worker cannot pose as another consumer's worker and take its tasks.
    /// </summary>
    public static AuditContext ForWorker(HttpContext http, string workerName)
    {
        var consumerId = http.User.GetApiConsumerId() ?? throw new DuctworkBadRequestException("A worker must authenticate with an API key.");
        var name = (workerName ?? "").Trim();
        if (name.Length is 0 or > 60 || !name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
            throw new DuctworkBadRequestException("A worker name is 1 to 60 letters, digits, dashes, underscores or dots.");

        return AuditContext.ForWorker($"c{consumerId}:{name}") with { ClientIp = http.Connection.RemoteIpAddress?.ToString() };
    }

    public static string TabId(HttpContext http)
    {
        var tab = http.Request.Headers[DuctworkTool.TabHeader].ToString().Trim();
        if (tab.Length is < 8 or > 64 || !tab.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
            throw new DuctworkBadRequestException($"Send a {DuctworkTool.TabHeader} header of 8 to 64 letters, digits, dashes or underscores that identifies this browser tab.");
        return tab;
    }

    public static string SessionFor(string email, string tab)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(AuditContext.Normalize(email) + "|" + tab));
        return "u" + Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    private static string? Truncate(string? value, int max) => string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}

/// <summary>Turns data-layer refusals into the HTTP answers a client can act on. Anything unexpected is left to the app's normal error handling, so no internals leak.</summary>
public sealed class DuctworkApiExceptionAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        var mapped = Map(context.Exception);
        if (mapped is null) return;

        context.Result = DuctworkJson.Result(mapped.Value.Body, mapped.Value.Status);
        context.ExceptionHandled = true;
    }

    public static (int Status, object Body)? Map(Exception ex) => ex switch
    {
        JobNotFoundException or KeyNotFoundException => (404, new { code = "not-found", error = ex.Message }),
        JobAccessDeniedException => (403, new { code = "forbidden", error = ex.Message }),
        JobConcurrencyException => (409, new { code = "version-conflict", error = ex.Message }),
        StaleEditTokenException => (409, new { code = "lock-lost", error = ex.Message }),
        JobLockedException => (409, new { code = "locked", error = ex.Message }),
        UploadOffsetException offset => (409, new { code = "offset-mismatch", error = ex.Message, expectedOffset = offset.Expected }),
        UploadBusyException => (409, new { code = "busy", error = ex.Message }),
        ChunkIntegrityException => (422, new { code = "checksum", error = ex.Message }),
        ResultRejectedException rejected => (422, new { code = "result-rejected", error = ex.Message, reasons = rejected.Reasons }),
        JobRuleViolationException => (422, new { code = "rule", error = ex.Message }),
        ArgumentException => (400, new { code = "bad-request", error = ex.Message }),
        _ => null
    };
}

public static class DuctworkServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Ductwork data layer. The repositories hold no per-request state and open their own
    /// connection per call, so they are singletons over the app's connection factory.
    /// </summary>
    public static IServiceCollection AddDuctwork(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DuctworkOptions>(configuration.GetSection("Ductwork"));

        services.AddSingleton<Func<CancellationToken, Task<SqlConnection>>>(sp =>
        {
            var factory = sp.GetRequiredService<SqlConnectionFactory>();
            return ct => factory.OpenConnectionAsync(ct);
        });
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<DuctworkOptions>>().Value.ToLockSettings());
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<DuctworkOptions>>().Value.ToTaskSettings());
        services.AddSingleton(new ReviewLevelSettings());
        services.AddSingleton<IArtifactStore>(sp =>
        {
            var configured = sp.GetRequiredService<IOptions<DuctworkOptions>>().Value.ArtifactRoot;
            var root = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(sp.GetRequiredService<IHostEnvironment>().ContentRootPath, "App_Data", "ductwork")
                : configured;
            return new FileSystemArtifactStore(root);
        });
        services.AddSingleton(sp => new UploadService(sp.GetRequiredService<Func<CancellationToken, Task<SqlConnection>>>(),
            sp.GetRequiredService<IArtifactStore>(), sp.GetRequiredService<IOptions<DuctworkOptions>>().Value.ToUploadSettings()));

        services.AddSingleton(sp => new JobRepository(sp.GetRequiredService<Func<CancellationToken, Task<SqlConnection>>>()));
        services.AddSingleton(sp => new EditLockService(sp.GetRequiredService<Func<CancellationToken, Task<SqlConnection>>>(), sp.GetRequiredService<LockSettings>()));
        services.AddSingleton(sp => new ProcessingTaskRepository(sp.GetRequiredService<Func<CancellationToken, Task<SqlConnection>>>(), sp.GetRequiredService<TaskSettings>()));
        services.AddSingleton(sp => new TaskBindingRepository(sp.GetRequiredService<Func<CancellationToken, Task<SqlConnection>>>()));
        services.AddSingleton(sp => new BuildingTypeRepository(sp.GetRequiredService<Func<CancellationToken, Task<SqlConnection>>>()));
        services.AddSingleton(sp => new EntityRepository(sp.GetRequiredService<Func<CancellationToken, Task<SqlConnection>>>()));
        services.AddSingleton(sp => new CountSheetRepository(sp.GetRequiredService<Func<CancellationToken, Task<SqlConnection>>>()));
        services.AddSingleton(sp => new ActivityRepository(sp.GetRequiredService<Func<CancellationToken, Task<SqlConnection>>>(),
            sp.GetRequiredService<LockSettings>().HeartbeatInterval));
        services.AddSingleton(sp => new TaskResultIngestor(sp.GetRequiredService<Func<CancellationToken, Task<SqlConnection>>>(),
            sp.GetRequiredService<ReviewLevelSettings>()));

        services.AddHostedService<DuctworkMigrationService>();
        services.AddHostedService<DuctworkUploadSweepService>();
        return services;
    }
}

/// <summary>Applies the ductwork schema at startup only when <see cref="DuctworkOptions.RunMigrationsOnStartup"/> is on. It is off by default on purpose.</summary>
public sealed class DuctworkMigrationService(IOptions<DuctworkOptions> options, IConfiguration configuration, ILogger<DuctworkMigrationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.RunMigrationsOnStartup)
        {
            logger.LogInformation("Ductwork migrations are not run at startup (Ductwork:RunMigrationsOnStartup is off).");
            return;
        }

        var connectionString = configuration.GetConnectionString("AITools")
            ?? throw new InvalidOperationException("Missing ConnectionStrings:AITools.");
        var result = await new MigrationRunner().MigrateAsync(connectionString, cancellationToken);
        logger.LogInformation("Ductwork migrations applied: {Applied}; already applied: {Already}.",
            string.Join(", ", result.Applied), string.Join(", ", result.AlreadyApplied));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Closes uploads nobody finished and frees their staging space. A database without the Ductwork schema is not an
/// error here: the feature is off until the schema is applied on purpose, so the sweep just waits.
/// </summary>
public sealed class DuctworkUploadSweepService(UploadService uploads, ILogger<DuctworkUploadSweepService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A short wait first, so startup is never slowed by housekeeping.
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var expired = await uploads.ExpireStaleAsync(AuditContext.ForSystem("upload sweep"), stoppingToken);
                if (expired > 0) logger.LogInformation("Expired {Count} unfinished Ductwork upload(s).", expired);
            }
            catch (SqlException ex) when (ex.Number == 208)
            {
                logger.LogDebug("The Ductwork schema is not applied yet; the upload sweep has nothing to do.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The Ductwork upload sweep failed; it will try again later.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
