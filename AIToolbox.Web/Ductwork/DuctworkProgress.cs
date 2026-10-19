using System.Collections.Concurrent;
using AIToolbox.Ductwork.Core.Activity;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Progress;
using AIToolbox.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Web.Ductwork;

/// <summary>
/// Live progress for the jobs a person has open. A page asks to watch a job it can see; the hub checks the person's access to that job exactly as the
/// REST API does (a job they cannot see is "not found") and then pushes a small event whenever one of the job's tasks or archive operations changes. An
/// event carries a status and a progress bar's numbers, never job contents: the page re-reads through the normal API, which checks access again.
/// </summary>
[Authorize(Policy = DuctworkPolicies.User)]
public sealed class DuctworkProgressHub(JobRepository jobs, DuctworkProgressWatches watches) : Hub
{
    public const string Path = "/hubs/ductwork-progress";
    public const string ProgressMethod = "progress";

    public static string GroupFor(long jobId) => $"ductwork-job:{jobId}";

    /// <summary>Starts sending this connection the progress of a job it may see. Safe to repeat.</summary>
    public async Task Watch(long jobId)
    {
        var email = EmailOf(Context);
        if (!watches.CanAdd(Context.ConnectionId, jobId)) throw new HubException($"A page can watch at most {DuctworkProgressWatches.MaxPerConnection} jobs at once.");

        var (role, _) = await jobs.GetAccessAsync(jobId, email, Context.ConnectionAborted);
        if (role is null) throw new HubException("That job was not found.");

        if (!watches.Add(Context.ConnectionId, email, jobId)) return;
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(jobId), Context.ConnectionAborted);
    }

    public async Task Unwatch(long jobId)
    {
        if (watches.Remove(Context.ConnectionId, jobId)) await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(jobId));
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        // The group memberships go with the connection; this only releases the tracker's count so it stops reading jobs nobody is on.
        watches.RemoveConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    private static string EmailOf(HubCallerContext context) =>
        AuditContext.Normalize(context.User?.GetEmailAddress() ?? throw new HubException("The signed-in user has no email address."));
}

/// <summary>
/// Which connection is watching which job, for whom. The tracker only needs a count per job, but access can be taken away while a page stays open
/// (a member removed from a job), so the person each watch was made for is kept and checked again on a timer.
/// </summary>
public sealed class DuctworkProgressWatches(JobProgressTracker tracker)
{
    public const int MaxPerConnection = 20;

    private readonly ConcurrentDictionary<string, Dictionary<long, string>> _byConnection = new();

    public bool CanAdd(string connectionId, long jobId)
    {
        if (!_byConnection.TryGetValue(connectionId, out var jobs)) return true;
        lock (jobs) return jobs.ContainsKey(jobId) || jobs.Count < MaxPerConnection;
    }

    /// <summary>False when the connection was already watching the job.</summary>
    public bool Add(string connectionId, string email, long jobId)
    {
        var jobs = _byConnection.GetOrAdd(connectionId, _ => []);
        lock (jobs)
        {
            if (jobs.ContainsKey(jobId)) return false;
            jobs[jobId] = email;
        }

        tracker.Watch(jobId);
        return true;
    }

    public bool Remove(string connectionId, long jobId)
    {
        if (!_byConnection.TryGetValue(connectionId, out var jobs)) return false;
        lock (jobs)
        {
            if (!jobs.Remove(jobId)) return false;
        }

        tracker.Unwatch(jobId);
        return true;
    }

    public void RemoveConnection(string connectionId)
    {
        if (!_byConnection.TryRemove(connectionId, out var jobs)) return;
        long[] watched;
        lock (jobs) watched = [.. jobs.Keys];
        foreach (var jobId in watched) tracker.Unwatch(jobId);
    }

    public IReadOnlyList<(string ConnectionId, string Email, long JobId)> Snapshot()
    {
        var all = new List<(string, string, long)>();
        foreach (var (connectionId, jobs) in _byConnection)
            lock (jobs) all.AddRange(jobs.Select(j => (connectionId, j.Value, j.Key)));
        return all;
    }
}

/// <summary>Delivers a job's changes to the browsers watching it.</summary>
public sealed class HubJobProgressSink(IHubContext<DuctworkProgressHub> hub) : IJobProgressSink
{
    public Task SendAsync(long jobId, IReadOnlyList<JobProgressEvent> events, CancellationToken cancellationToken) =>
        hub.Clients.Group(DuctworkProgressHub.GroupFor(jobId)).SendAsync(DuctworkProgressHub.ProgressMethod, jobId, events, cancellationToken);
}

/// <summary>
/// Reads the watched jobs about once a second while anybody is watching, and about every half minute checks that each watcher still has access. A
/// database without the Ductwork schema is not an error: the feature is off until the schema is applied on purpose, so this just waits.
/// </summary>
public sealed class DuctworkProgressService(
    JobProgressTracker tracker, DuctworkProgressWatches watches, JobRepository jobs, IHubContext<DuctworkProgressHub> hub, ILogger<DuctworkProgressService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Recheck = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextCheck = DateTime.UtcNow + Recheck;
        while (!stoppingToken.IsCancellationRequested)
        {
            var pause = Interval;
            try
            {
                await tracker.PollOnceAsync(stoppingToken);
                if (DateTime.UtcNow >= nextCheck)
                {
                    nextCheck = DateTime.UtcNow + Recheck;
                    await DropLostAccessAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (SqlException ex) when (ex.Number == 208)
            {
                pause = TimeSpan.FromMinutes(5);
                logger.LogDebug("The Ductwork schema is not applied yet; there is no progress to report.");
            }
            catch (Exception ex)
            {
                pause = Backoff;
                logger.LogWarning(ex, "Reading job progress failed; it will try again shortly.");
            }

            try { await Task.Delay(pause, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Takes a connection off a job its person can no longer see, so a removed member stops receiving that job's progress.</summary>
    public async Task DropLostAccessAsync(CancellationToken cancellationToken)
    {
        foreach (var (connectionId, email, jobId) in watches.Snapshot())
        {
            var (role, _) = await jobs.GetAccessAsync(jobId, email, cancellationToken);
            if (role is not null) continue;
            if (watches.Remove(connectionId, jobId))
                await hub.Groups.RemoveFromGroupAsync(connectionId, DuctworkProgressHub.GroupFor(jobId), cancellationToken);
        }
    }
}
