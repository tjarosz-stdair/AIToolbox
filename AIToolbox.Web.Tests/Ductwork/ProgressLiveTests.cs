using System.Security.Claims;
using System.Text.Encodings.Web;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Data;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Progress;
using AIToolbox.Ductwork.Core.Tasks;
using AIToolbox.Web.Ductwork;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static AIToolbox.Web.Tests.Ductwork.ApiHarness;

namespace AIToolbox.Web.Tests.Ductwork;

/// <summary>
/// Progress over a real SignalR connection: the actual hub, tracker and background service in an in-memory server on the test database, with a real
/// client. What the fakes cannot show: the wire format the page reads, and that an event reaches the people on a job and nobody else.
/// </summary>
public class ProgressLiveTests(DuctworkDatabase db) : IClassFixture<DuctworkDatabase>
{
    private sealed record Push(long Job, JobProgressEvent[] Events);

    private const string UserHeader = "X-Test-User";

    private readonly ApiHarness _h = new(db);

    /// <summary>Signs a request in as whoever the test header names, so the hub's own policy and access check are what is being tested.</summary>
    private sealed class HeaderAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var email = Request.Headers[UserHeader].ToString();
            if (email.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(Person(email), Scheme.Name)));
        }
    }

    private async Task<WebApplication> StartServerAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuth>("Test", _ => { });
        builder.Services.AddAuthorization(o => o.AddPolicy(DuctworkPolicies.User, p => p.RequireAuthenticatedUser()));

        Func<CancellationToken, Task<SqlConnection>> open = async ct =>
        {
            var connection = new SqlConnection(db.ConnectionString);
            await connection.OpenAsync(ct);
            return connection;
        };
        builder.Services.AddSingleton(new JobRepository(open));
        builder.Services.AddSingleton<IJobProgressSink, HubJobProgressSink>();
        builder.Services.AddSingleton(sp => new JobProgressTracker(open, sp.GetRequiredService<IJobProgressSink>()));
        builder.Services.AddSingleton<DuctworkProgressWatches>();
        builder.Services.AddHostedService<DuctworkProgressService>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHub<DuctworkProgressHub>(DuctworkProgressHub.Path);
        await app.StartAsync();
        return app;
    }

    private static HubConnection Connect(WebApplication app, string email, List<Push> received)
    {
        var server = app.GetTestServer();
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, DuctworkProgressHub.Path), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                o.Headers[UserHeader] = email;
            })
            .Build();
        connection.On<long, JobProgressEvent[]>(DuctworkProgressHub.ProgressMethod, (job, events) => { lock (received) received.Add(new Push(job, events)); });
        return connection;
    }

    private async Task<long> QueueTaskAsync(long jobId, string type)
    {
        var token = (await _h.LockService.AcquireAsync(jobId, AuditContext.ForUser(Editor, sessionId: "s-editor"))).Token!;
        await using var connection = new SqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        var producer = await ProducerStore.EnsureProducerAsync(connection, ProducerRef.Algorithm("live-" + type, "0.1.0", "h"));
        var task = await _h.TaskRepo.EnqueueAsync(new NewProcessingTask { JobId = jobId, TaskType = type, ProducerId = producer, MaxAttempts = 3, InputManifestRef = "inputs/x" }, token,
            AuditContext.ForUser(Editor, sessionId: "s-editor"));
        return task.ProcessingTaskId;
    }

    private static async Task<T> EventuallyAsync<T>(Func<T?> read, int seconds = 10) where T : class
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (read() is { } value) return value;
            await Task.Delay(50);
        }

        throw new TimeoutException("The progress event did not arrive.");
    }

    [Fact]
    public async Task A_change_on_a_watched_job_reaches_the_member_watching_it_and_nobody_else()
    {
        var watched = await _h.NewJobAsync();
        var elsewhere = await _h.NewJobAsync();
        await using var app = await StartServerAsync();
        var viewerGot = new List<Push>();
        var strangerGot = new List<Push>();
        await using var viewer = Connect(app, Viewer, viewerGot);
        await using var stranger = Connect(app, Stranger, strangerGot);
        await viewer.StartAsync();
        await stranger.StartAsync();
        await viewer.InvokeAsync("Watch", watched);
        await Assert.ThrowsAsync<HubException>(() => stranger.InvokeAsync("Watch", watched));

        var type = "live-" + Guid.NewGuid().ToString("N")[..8];
        var taskId = await QueueTaskAsync(watched, type);
        await QueueTaskAsync(elsewhere, type + "-other");

        var first = await EventuallyAsync(() => { lock (viewerGot) return viewerGot.FirstOrDefault(); });
        Assert.Equal(watched, first.Job);
        var queued = Assert.Single(first.Events);
        Assert.Equal((JobProgressEvent.TaskKind, taskId.ToString(), "Queued", watched), (queued.Kind, queued.Id, queued.Status, queued.JobId));

        // A running worker's heartbeat arrives as progress numbers on the next read.
        await _h.TaskRepo.ClaimNextAsync(AuditContext.ForWorker("worker-live"), [type]);
        await _h.TaskRepo.HeartbeatAsync(taskId, "worker-live", 4, 8, "page 4 of 8");
        var running = await EventuallyAsync(() =>
        {
            lock (viewerGot) return viewerGot.SelectMany(g => g.Events).FirstOrDefault(e => e.Status == "Running" && e.Done == 4);
        });
        Assert.Equal((8L, "page 4 of 8"), (running.Total, running.Message));

        lock (strangerGot) Assert.Empty(strangerGot);
        lock (viewerGot) Assert.All(viewerGot, g => Assert.Equal(watched, g.Job));              // the other job's task was never sent
    }

    [Fact]
    public async Task A_page_that_stops_watching_stops_receiving()
    {
        var jobId = await _h.NewJobAsync();
        await using var app = await StartServerAsync();
        var got = new List<Push>();
        await using var page = Connect(app, Owner, got);
        await page.StartAsync();
        await page.InvokeAsync("Watch", jobId);
        await page.InvokeAsync("Unwatch", jobId);
        await Task.Delay(100);

        await QueueTaskAsync(jobId, "live-" + Guid.NewGuid().ToString("N")[..8]);
        await Task.Delay(2500);                                                                  // two or more reads

        lock (got) Assert.Empty(got);
        Assert.Empty(app.Services.GetRequiredService<JobProgressTracker>().WatchedJobs);
    }
}
