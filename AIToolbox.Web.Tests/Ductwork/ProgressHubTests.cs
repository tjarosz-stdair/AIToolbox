using System.Security.Claims;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Progress;
using AIToolbox.Web.Ductwork;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using static AIToolbox.Web.Tests.Ductwork.ApiHarness;

namespace AIToolbox.Web.Tests.Ductwork;

/// <summary>Who may watch a job's progress: exactly the people who may see the job, checked again while the page stays open.</summary>
public class ProgressHubTests(DuctworkDatabase db) : IClassFixture<DuctworkDatabase>
{
    private readonly ApiHarness _h = new(db);

    private sealed class FakeCaller(string connectionId, ClaimsPrincipal user) : HubCallerContext
    {
        public override string ConnectionId => connectionId;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => user;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    private sealed class FakeGroups : IGroupManager
    {
        public List<(string Connection, string Group)> Members { get; } = [];

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            lock (Members) Members.Add((connectionId, groupName));
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            lock (Members) Members.Remove((connectionId, groupName));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHubContext(FakeGroups groups) : IHubContext<DuctworkProgressHub>
    {
        public IHubClients Clients => throw new NotSupportedException();
        public IGroupManager Groups => groups;
    }

    private sealed class NoSink : IJobProgressSink
    {
        public Task SendAsync(long jobId, IReadOnlyList<JobProgressEvent> events, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private (DuctworkProgressHub Hub, DuctworkProgressWatches Watches, JobProgressTracker Tracker, FakeGroups Groups) Hub(string email, string connectionId = "c1", DuctworkProgressWatches? shared = null)
    {
        var tracker = shared is null ? new JobProgressTracker(ct => Task.FromResult(new Microsoft.Data.SqlClient.SqlConnection()), new NoSink()) : null;
        var watches = shared ?? new DuctworkProgressWatches(tracker!);
        var groups = new FakeGroups();
        var hub = new DuctworkProgressHub(_h.JobRepo, watches) { Context = new FakeCaller(connectionId, Person(email)), Groups = groups };
        return (hub, watches, tracker!, groups);
    }

    [Fact]
    public async Task A_member_watches_their_job_and_joins_its_group()
    {
        var id = await _h.NewJobAsync();
        var (hub, watches, tracker, groups) = Hub(Viewer);

        await hub.Watch(id);
        await hub.Watch(id);                                                                    // asking twice changes nothing

        Assert.Equal([id], tracker.WatchedJobs);
        Assert.Equal([("c1", DuctworkProgressHub.GroupFor(id))], groups.Members);
        Assert.Single(watches.Snapshot());
    }

    [Fact]
    public async Task A_person_with_no_access_is_told_the_job_does_not_exist_and_watches_nothing()
    {
        var id = await _h.NewJobAsync();
        var (hub, watches, tracker, groups) = Hub(Stranger);

        var refused = await Assert.ThrowsAsync<HubException>(() => hub.Watch(id));
        var missing = await Assert.ThrowsAsync<HubException>(() => hub.Watch(9_999_999));

        Assert.Equal(refused.Message, missing.Message);                                          // a job that exists looks like one that does not
        Assert.Empty(tracker.WatchedJobs);
        Assert.Empty(groups.Members);
        Assert.Empty(watches.Snapshot());
    }

    [Fact]
    public async Task Leaving_a_job_or_disconnecting_stops_the_server_reading_it()
    {
        var a = await _h.NewJobAsync();
        var b = await _h.NewJobAsync();
        var (hub, watches, tracker, groups) = Hub(Owner);
        await hub.Watch(a);
        await hub.Watch(b);

        await hub.Unwatch(a);
        Assert.Equal([b], tracker.WatchedJobs);
        Assert.DoesNotContain(groups.Members, m => m.Group == DuctworkProgressHub.GroupFor(a));

        await hub.OnDisconnectedAsync(null);
        Assert.Empty(tracker.WatchedJobs);
        Assert.Empty(watches.Snapshot());
    }

    [Fact]
    public async Task Two_pages_on_one_job_keep_it_watched_until_both_leave()
    {
        var id = await _h.NewJobAsync();
        var (first, watches, tracker, _) = Hub(Owner, "c1");
        var (second, _, _, _) = Hub(Editor, "c2", watches);
        await first.Watch(id);
        await second.Watch(id);

        await first.OnDisconnectedAsync(null);
        Assert.Equal([id], tracker.WatchedJobs);

        await second.OnDisconnectedAsync(null);
        Assert.Empty(tracker.WatchedJobs);
    }

    [Fact]
    public async Task One_page_cannot_watch_an_unbounded_number_of_jobs()
    {
        var (hub, _, tracker, _) = Hub(Owner);
        var ids = new List<long>();
        for (var i = 0; i < DuctworkProgressWatches.MaxPerConnection + 1; i++) ids.Add(await _h.NewJobAsync());
        foreach (var id in ids.Take(DuctworkProgressWatches.MaxPerConnection)) await hub.Watch(id);

        await Assert.ThrowsAsync<HubException>(() => hub.Watch(ids[^1]));

        Assert.Equal(DuctworkProgressWatches.MaxPerConnection, tracker.WatchedJobs.Count);
    }

    [Fact]
    public async Task A_member_who_is_removed_stops_receiving_the_job_without_reconnecting()
    {
        var id = await _h.NewJobAsync();
        var (hub, watches, tracker, _) = Hub(Viewer);
        await hub.Watch(id);
        var groups = new FakeGroups();
        await groups.AddToGroupAsync("c1", DuctworkProgressHub.GroupFor(id));
        var service = new DuctworkProgressService(tracker, watches, _h.JobRepo, new FakeHubContext(groups), NullLogger<DuctworkProgressService>.Instance);

        await service.DropLostAccessAsync(default);
        Assert.Equal([id], tracker.WatchedJobs);                                                // still a member: nothing changes

        await _h.JobRepo.RemoveMemberAsync(id, Viewer, AuditContext.ForUser(Owner, sessionId: "setup"));
        await service.DropLostAccessAsync(default);

        Assert.Empty(tracker.WatchedJobs);
        Assert.Empty(groups.Members);
        Assert.Empty(watches.Snapshot());
    }
}
