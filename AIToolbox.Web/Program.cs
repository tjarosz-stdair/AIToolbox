using System.Security.Claims;
using AIToolbox.Web.Authorization;
using AIToolbox.Web.Configuration;
using AIToolbox.Web.Data;
using AIToolbox.Web.Security;
using AIToolbox.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Events;
using Serilog.Filters;

var builder = WebApplication.CreateBuilder(args);

ConfigureSerilog(builder);

builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection("Database"));
builder.Services.Configure<AIToolsDataProtectionOptions>(builder.Configuration.GetSection("DataProtection"));
builder.Services.Configure<AIToolsLoggingOptions>(builder.Configuration.GetSection("AIToolsLogging"));
builder.Services.Configure<MicrosoftEntraOptions>(builder.Configuration.GetSection("MicrosoftEntra"));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<CorsOptions>(builder.Configuration.GetSection("Cors"));

var dataProtectionOptions = builder.Configuration.GetSection("DataProtection").Get<AIToolsDataProtectionOptions>()
    ?? new AIToolsDataProtectionOptions();
var dataProtectionBuilder = builder.Services
    .AddDataProtection()
    .SetApplicationName(dataProtectionOptions.ApplicationName);

if (!string.IsNullOrWhiteSpace(dataProtectionOptions.KeysPath))
{
    var keysPath = Environment.ExpandEnvironmentVariables(dataProtectionOptions.KeysPath.Trim());
    Directory.CreateDirectory(keysPath);
    dataProtectionBuilder.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}

builder.Services.AddHttpClient("AiProviders", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("AIToolbox/1.0");
    client.Timeout = Timeout.InfiniteTimeSpan;
});

builder.Services.AddScoped<SqlConnectionFactory>();
builder.Services.AddScoped<DatabaseInitializer>();
builder.Services.AddScoped<IAiProviderRepository, AiProviderRepository>();
builder.Services.AddScoped<IAuthorizedUserRepository, AuthorizedUserRepository>();
builder.Services.AddScoped<IEventLogRepository, EventLogRepository>();
builder.Services.AddScoped<IPromptRunRepository, PromptRunRepository>();
builder.Services.AddScoped<IPlatformRepository, PlatformRepository>();
builder.Services.AddScoped<IApiConsumerRepository, ApiConsumerRepository>();
builder.Services.AddScoped<IAiBatchRepository, AiBatchRepository>();
builder.Services.AddScoped<ISecretProtector, DataProtectionSecretProtector>();
builder.Services.AddScoped<IApiKeyService, ApiKeyService>();
builder.Services.AddScoped<IAccessControlService, AccessControlService>();
builder.Services.AddScoped<IExternalUserClaimsEnricher, ExternalUserClaimsEnricher>();
builder.Services.AddScoped<IAiProviderClient, AiProviderClient>();
builder.Services.AddScoped<IPromptOrchestrator, PromptOrchestrator>();
builder.Services.AddScoped<IFileStorageService, FileStorageService>();
builder.Services.AddScoped<IProductParsingService, ProductParsingService>();
builder.Services.AddScoped<IAiBatchProviderClient, AiBatchProviderClient>();
builder.Services.AddScoped<IAiBatchService, AiBatchService>();
builder.Services.AddSingleton<IProductParsingJobQueue, ProductParsingJobQueue>();
builder.Services.AddHostedService<ProductParsingBackgroundService>();
builder.Services.AddHostedService<AiBatchPollingService>();
builder.Services.AddSingleton<IMarkdownRenderer, MarkdownRenderer>();
builder.Services.AddScoped<IAuthorizationHandler, ActiveUserAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, AdminUserAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, ToolAccessAuthorizationHandler>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = "AIToolsSmart";
        options.DefaultChallengeScheme = "AIToolsSmart";
    })
    .AddPolicyScheme("AIToolsSmart", "AITools cookie or bearer", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            if (context.Request.Headers.ContainsKey("X-AIToolbox-Api-Key")
                || authorization.StartsWith("Bearer aitb_", StringComparison.OrdinalIgnoreCase))
            {
                return ApiKeyAuthenticationHandler.SchemeName;
            }

            return context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                    ? JwtBearerDefaults.AuthenticationScheme
                    : CookieAuthenticationDefaults.AuthenticationScheme;
        };
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = ".AIToolsCompare.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.LoginPath = "/Account/SignIn";
        options.SlidingExpiration = true;
    })
    .AddOpenIdConnect(options =>
    {
        var entra = builder.Configuration.GetSection("MicrosoftEntra").Get<MicrosoftEntraOptions>()
            ?? new MicrosoftEntraOptions();
        options.Authority = $"{entra.Instance.TrimEnd('/')}/{entra.TenantId}/v2.0";
        options.ClientId = entra.ClientId;
        options.ClientSecret = entra.ClientSecret;
        options.CallbackPath = entra.CallbackPath;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.Scope.Add("email");
        options.Scope.Add("profile");
        options.TokenValidationParameters = new TokenValidationParameters
        {
            NameClaimType = "name",
            RoleClaimType = ClaimTypes.Role,
            ValidateIssuer = false
        };
        options.Events = new OpenIdConnectEvents
        {
            OnTokenValidated = async context =>
            {
                var principal = context.Principal;
                if (principal?.Identity is not ClaimsIdentity)
                {
                    context.Fail("The signed-in user was not available.");
                    return;
                }

                var email = principal.GetEmailAddress();
                if (string.IsNullOrWhiteSpace(email))
                {
                    context.Fail("The signed-in user did not provide an email claim.");
                    return;
                }

                var enricher = context.HttpContext.RequestServices.GetRequiredService<IExternalUserClaimsEnricher>();
                await enricher.EnrichAsync(
                    principal,
                    context.HttpContext,
                    recordLogin: true,
                    cancellationToken: context.HttpContext.RequestAborted);
            },
            OnRemoteFailure = context =>
            {
                context.HandleResponse();
                var message = Uri.EscapeDataString(context.Failure?.Message ?? "Microsoft sign-in failed.");
                context.Response.Redirect($"{context.Request.PathBase}/Account/ExternalAuthFailed?message={message}");
                return Task.CompletedTask;
            }
        };
    })
    .AddJwtBearer(options =>
    {
        var entra = builder.Configuration.GetSection("MicrosoftEntra").Get<MicrosoftEntraOptions>()
            ?? new MicrosoftEntraOptions();
        options.Authority = $"{entra.Instance.TrimEnd('/')}/{entra.TenantId}/v2.0";
        options.Audience = string.IsNullOrWhiteSpace(entra.ApiAudience)
            ? entra.ClientId
            : entra.ApiAudience;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            NameClaimType = "name",
            RoleClaimType = ClaimTypes.Role,
            ValidateIssuer = false
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                if (context.Principal is null)
                {
                    context.Fail("The bearer token principal was not available.");
                    return;
                }

                var enricher = context.HttpContext.RequestServices.GetRequiredService<IExternalUserClaimsEnricher>();
                await enricher.EnrichAsync(
                    context.Principal,
                    context.HttpContext,
                    cancellationToken: context.HttpContext.RequestAborted);
            }
        };
    })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName,
        _ => { });

builder.Services.AddCors(options =>
{
    options.AddPolicy("ChromeExtensionApi", policy =>
    {
        var origins = builder.Configuration
            .GetSection("Cors:ChromeExtensionOrigins")
            .Get<string[]>() ?? [];
        if (origins.Length > 0)
        {
            policy.WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
    });
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ActiveUser", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new ActiveUserRequirement());
    });

    options.AddPolicy("AdminOnly", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new ActiveUserRequirement(), new AdminUserRequirement());
    });

    options.AddPolicy("Tool:ProductParser", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new ToolAccessRequirement(ProductParsingService.ToolKey));
    });

    options.AddPolicy("ApiConsumer", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole("ApiConsumer");
    });

    options.AddPolicy("BatchApiAccess", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
            context.User.IsInRole("Admin")
            || !context.User.IsApiConsumer()
            || context.User.HasApiScope("batch:read")
            || context.User.HasApiScope("batch:write"));
    });
});

builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new AuthorizeFilter("ActiveUser"));
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
    options.GetLevel = (httpContext, elapsed, ex) =>
        ex is not null || httpContext.Response.StatusCode >= 500
            ? LogEventLevel.Error
            : httpContext.Response.StatusCode >= 400
                ? LogEventLevel.Warning
                : LogEventLevel.Information;
    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        diagnosticContext.Set("UserEmail", httpContext.User.GetEmailAddress() ?? httpContext.User.Identity?.Name);
        diagnosticContext.Set("RemoteIpAddress", httpContext.Connection.RemoteIpAddress?.ToString());
        diagnosticContext.Set("UserAgent", httpContext.Request.Headers.UserAgent.ToString());
        var path = httpContext.Request.Path;
        if (path.StartsWithSegments("/ProductParser", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/PdfProductParser", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/v1/product-parse", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/v1/pdf-parse", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/v1/products", StringComparison.OrdinalIgnoreCase))
        {
            diagnosticContext.Set("ToolKey", ProductParsingService.ToolKey);
        }
        else if (path.StartsWithSegments("/Compare", StringComparison.OrdinalIgnoreCase))
        {
            diagnosticContext.Set("ToolKey", "AICompare");
        }
    };
});

app.UseCors("ChromeExtensionApi");
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapControllers();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

await InitializeDatabaseAsync(app);

app.Run();

static void ConfigureSerilog(WebApplicationBuilder builder)
{
    var options = builder.Configuration.GetSection("AIToolsLogging").Get<AIToolsLoggingOptions>()
        ?? new AIToolsLoggingOptions();
    var basePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(options.BasePath.Trim()));
    Directory.CreateDirectory(basePath);

    const string outputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] [{ToolKey}] [{UserEmail}] {Message:lj}{NewLine}{Exception}";

    builder.Host.UseSerilog((context, services, loggerConfiguration) =>
    {
        loggerConfiguration
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Authentication", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore.Authorization", LogEventLevel.Information)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "AIToolbox")
            .Enrich.WithProperty("Environment", context.HostingEnvironment.EnvironmentName)
            .ReadFrom.Services(services)
            .WriteTo.File(
                Path.Combine(basePath, "app", "aitoolbox-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: options.RetainedFileCountLimit,
                fileSizeLimitBytes: options.FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: outputTemplate)
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(logEvent => logEvent.Level >= LogEventLevel.Error)
                .WriteTo.File(
                    Path.Combine(basePath, "exceptions", "site-exceptions-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: options.RetainedFileCountLimit,
                    fileSizeLimitBytes: options.FileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    shared: true,
                    outputTemplate: outputTemplate))
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(Matching.WithProperty<string>("ToolKey", value =>
                    string.Equals(value, "AICompare", StringComparison.OrdinalIgnoreCase)))
                .WriteTo.File(
                    Path.Combine(basePath, "AICompare", "aicompare-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: options.RetainedFileCountLimit,
                    fileSizeLimitBytes: options.FileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    shared: true,
                    outputTemplate: outputTemplate))
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(Matching.WithProperty<string>("ToolKey", value =>
                    string.Equals(value, ProductParsingService.ToolKey, StringComparison.OrdinalIgnoreCase)))
                .WriteTo.File(
                    Path.Combine(basePath, ProductParsingService.ToolKey, "product-parser-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: options.RetainedFileCountLimit,
                    fileSizeLimitBytes: options.FileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    shared: true,
                    outputTemplate: outputTemplate));
    });
}

static async Task InitializeDatabaseAsync(WebApplication app)
{
    var options = app.Configuration.GetSection("Database").Get<DatabaseOptions>() ?? new DatabaseOptions();
    if (!options.InitializeOnStartup)
    {
        return;
    }

    try
    {
        await using var scope = app.Services.CreateAsyncScope();
        var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
        await initializer.InitializeAsync();
    }
    catch (Exception ex)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseInitialization");
        logger.LogError(ex, "Database initialization failed.");

        if (!app.Environment.IsDevelopment())
        {
            throw;
        }
    }
}
