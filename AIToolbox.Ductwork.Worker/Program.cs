using AIToolbox.Ductwork.Worker;
using AIToolbox.Ductwork.Worker.Handlers;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "AIToolbox Ductwork Worker");
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection(WorkerOptions.Section));

var configured = builder.Configuration.GetSection(WorkerOptions.Section).Get<WorkerOptions>() ?? new WorkerOptions();
if (string.IsNullOrWhiteSpace(configured.ServerUrl) || string.IsNullOrWhiteSpace(configured.ApiKey))
{
    Console.Error.WriteLine("Worker:ServerUrl and Worker:ApiKey must be set (appsettings.Production.json, environment variables or user secrets).");
    return 2;
}

builder.Services.AddHttpClient<IDuctworkApi, DuctworkApiClient>((sp, http) =>
{
    var options = sp.GetRequiredService<IOptions<WorkerOptions>>().Value;
    http.BaseAddress = new Uri(options.ServerUrl.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(Math.Max(10, options.HttpTimeoutSeconds));
});

builder.Services.AddSingleton<IProcessRunner, ChildProcessRunner>();
builder.Services.AddSingleton<ITaskHandler, ResidentialLabelsHandler>();
if (configured.EnableFakeHandler) builder.Services.AddSingleton<ITaskHandler, FakeDetectHandler>();
builder.Services.AddSingleton<HandlerRegistry>();
builder.Services.AddHostedService<WorkerService>();

await builder.Build().RunAsync();
return 0;
