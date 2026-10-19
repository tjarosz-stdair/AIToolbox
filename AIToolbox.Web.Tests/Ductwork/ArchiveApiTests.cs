using System.Text.Json;
using AIToolbox.Ductwork.Core.Archive;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Web.Controllers.Ductwork;
using AIToolbox.Web.Ductwork;
using static AIToolbox.Web.Tests.Ductwork.ApiHarness;

namespace AIToolbox.Web.Tests.Ductwork;

public class ArchiveApiTests(DuctworkDatabase db) : IClassFixture<DuctworkDatabase>
{
    private const string Manager = "manager@example.com";

    private readonly ApiHarness _h = new(db);
    private static readonly AuditContext Runner = AuditContext.ForSystem("test runner");

    private async Task<(long Id, string Name)> ClosedJobAsync()
    {
        var name = "Closed " + Guid.NewGuid().ToString("N")[..8];
        var id = await _h.NewJobAsync(name);
        await _h.JobRepo.CloseAsync(id, "Lost the bid.", AuditContext.ForUser(Owner, sessionId: "setup"));
        return (id, name);
    }

    private async Task GrantManagerAsync() => await _h.Permissions.GrantJobManagerAsync(Manager, AuditContext.ForUser(Admin, isAdmin: true));

    [Fact]
    public async Task The_owner_archives_a_job_through_the_api_and_the_page_can_follow_it()
    {
        var (id, name) = await ClosedJobAsync();

        var queued = await RunAsync(() => _h.ArchiveApi(Owner).Archive(id, new ArchiveJobRequest("Purge", name), default));

        Assert.Equal((202, "Queued", "ArchivePurge"), (queued.Status, queued["status"].GetString(), queued["kind"].GetString()));
        var mid = await RunAsync(() => _h.ArchiveApi(Owner).ForJob(id, default));
        Assert.Equal("Archiving", mid["state"].GetString());
        Assert.True(mid["canArchive"].GetBoolean());
        Assert.Equal("Queued", mid["operations"][0].GetProperty("status").GetString());

        Assert.True(await _h.Archives.RunOperationAsync(queued["operationId"].GetInt64(), "test", Runner));

        var done = await RunAsync(() => _h.ArchiveApi(Owner).ForJob(id, default));
        Assert.Equal("Archived", done["state"].GetString());
        Assert.Equal("Succeeded", done["operations"][0].GetProperty("status").GetString());
        Assert.Equal("Purged", done["archives"][0].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, done["archives"][0].GetProperty("storageKey").ValueKind);        // where it lives is for administrators

        var restored = await RunAsync(() => _h.ArchiveApi(Owner).Restore(id, default));
        Assert.Equal((202, "Restore"), (restored.Status, restored["kind"].GetString()));
        Assert.True(await _h.Archives.RunOperationAsync(restored["operationId"].GetInt64(), "test", Runner));
        Assert.Equal("Closed", (await RunAsync(() => _h.ArchiveApi(Owner).ForJob(id, default)))["state"].GetString());
    }

    [Fact]
    public async Task An_administrator_sees_where_the_archive_is_kept_and_others_do_not()
    {
        var (id, _) = await ClosedJobAsync();
        var op = await RunAsync(() => _h.ArchiveApi(Owner).Archive(id, new ArchiveJobRequest("ZipOnly", null), default));
        await _h.Archives.RunOperationAsync(op["operationId"].GetInt64(), "test", Runner);
        await _h.JobRepo.AddMemberAsync(id, Admin, JobRole.Viewer, AuditContext.ForUser(Owner, sessionId: "setup"));

        var admin = await RunAsync(() => _h.ArchiveApi(Admin, admin: true).ForJob(id, default));

        Assert.Matches(@"^jobs/\d+/ductwork-", admin["archives"][0].GetProperty("storageKey").GetString());
    }

    [Fact]
    public async Task Removing_the_detail_needs_the_name_and_a_bad_request_changes_nothing()
    {
        var (id, _) = await ClosedJobAsync();

        var wrong = await RunAsync(() => _h.ArchiveApi(Owner).Archive(id, new ArchiveJobRequest("Purge", "nope"), default));
        var kind = await RunAsync(() => _h.ArchiveApi(Owner).Archive(id, new ArchiveJobRequest("Shred", null), default));

        Assert.Equal((422, "rule"), (wrong.Status, wrong.Code));
        Assert.Equal(400, kind.Status);
        Assert.Equal("Closed", (await RunAsync(() => _h.ArchiveApi(Owner).ForJob(id, default)))["state"].GetString());
    }

    [Fact]
    public async Task People_with_no_part_in_a_job_are_told_it_does_not_exist_and_a_viewer_may_look_but_not_archive()
    {
        var (id, name) = await ClosedJobAsync();

        Assert.Equal(404, (await RunAsync(() => _h.ArchiveApi(Stranger).ForJob(id, default))).Status);
        Assert.Equal(404, (await RunAsync(() => _h.ArchiveApi(Stranger).Archive(id, new ArchiveJobRequest("ZipOnly", null), default))).Status);
        Assert.Equal(404, (await RunAsync(() => _h.ArchiveApi(Stranger).Restore(id, default))).Status);

        var view = await RunAsync(() => _h.ArchiveApi(Viewer).ForJob(id, default));
        Assert.Equal((200, false), (view.Status, view["canArchive"].GetBoolean()));
        Assert.Equal(403, (await RunAsync(() => _h.ArchiveApi(Viewer).Archive(id, new ArchiveJobRequest("Purge", name), default))).Status);
        Assert.Equal(403, (await RunAsync(() => _h.ArchiveApi(Editor).Archive(id, new ArchiveJobRequest("ZipOnly", null), default))).Status);
    }

    [Fact]
    public async Task A_job_manager_can_archive_and_restore_any_job_but_sees_only_its_master_record()
    {
        await GrantManagerAsync();
        var (id, name) = await ClosedJobAsync();

        var queued = await RunAsync(() => _h.ArchiveApi(Manager).Archive(id, new ArchiveJobRequest("Purge", name), default));
        Assert.Equal(202, queued.Status);
        await _h.Archives.RunOperationAsync(queued["operationId"].GetInt64(), "test", Runner);

        var overview = await RunAsync(() => _h.ArchiveApi(Manager).Overview(default));
        var job = overview["jobs"].EnumerateArray().Single(j => j.GetProperty("jobId").GetInt64() == id);
        Assert.Equal(("Archived", name), (job.GetProperty("state").GetString(), job.GetProperty("name").GetString()));
        Assert.Equal("Purged", job.GetProperty("latestArchive").GetProperty("status").GetString());
        // Nothing of the job's contents is in the answer: only the record that says what and whose it is.
        Assert.DoesNotContain("entities", overview.Body.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sheets", overview.Body.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var restore = await RunAsync(() => _h.ArchiveApi(Manager).Restore(id, default));
        Assert.Equal(202, restore.Status);
        await _h.Archives.RunOperationAsync(restore["operationId"].GetInt64(), "test", Runner);
        Assert.Equal("Closed", (await _h.JobRepo.GetAsync(id))!.State.ToString());
    }

    [Fact]
    public async Task The_overview_is_for_administrators_and_job_managers_only()
    {
        await GrantManagerAsync();

        Assert.Equal(200, (await RunAsync(() => _h.ArchiveApi(Admin, admin: true).Overview(default))).Status);
        Assert.Equal(200, (await RunAsync(() => _h.ArchiveApi(Manager).Overview(default))).Status);
        Assert.Equal(403, (await RunAsync(() => _h.ArchiveApi(Owner).Overview(default))).Status);
        Assert.Equal(403, (await RunAsync(() => _h.ArchiveApi(Editor).Check(1, default))).Status);
    }

    [Fact]
    public async Task Only_an_administrator_can_relink_an_archive()
    {
        await GrantManagerAsync();
        var (id, _) = await ClosedJobAsync();
        var op = await RunAsync(() => _h.ArchiveApi(Owner).Archive(id, new ArchiveJobRequest("ZipOnly", null), default));
        await _h.Archives.RunOperationAsync(op["operationId"].GetInt64(), "test", Runner);
        var archiveId = (await _h.Archives.ListArchivesAsync(id)).Single().ArchiveId;

        Assert.Equal(403, (await RunAsync(() => _h.ArchiveApi(Manager).Relink(archiveId, new RelinkArchiveRequest("x/y.zip"), default))).Status);
        Assert.Equal(400, (await RunAsync(() => _h.ArchiveApi(Admin, admin: true).Relink(archiveId, new RelinkArchiveRequest("../escape.zip"), default))).Status);
        var check = await RunAsync(() => _h.ArchiveApi(Manager).Check(archiveId, default));
        Assert.True(check["ok"].GetBoolean());
    }

    // ---------------------------------------------------------------- who is a job manager

    [Fact]
    public async Task An_administrator_grants_and_removes_the_job_manager_permission_and_each_is_recorded()
    {
        var email = $"mgr{Guid.NewGuid().ToString("N")[..6]}@example.com";

        var granted = await RunAsync(() => _h.JobManagersApi().Grant(new GrantJobManagerRequest("  " + email.ToUpperInvariant() + " "), default));
        Assert.Equal(201, granted.Status);
        Assert.Contains(granted.Body.EnumerateArray(), g => g.GetProperty("userEmail").GetString() == email);
        Assert.True(await _h.Permissions.GetIsJobManagerAsync(email));
        await RunAsync(() => _h.JobManagersApi().Grant(new GrantJobManagerRequest(email), default));        // a second grant changes nothing

        var removed = await RunAsync(() => _h.JobManagersApi().Revoke(email, default));
        Assert.DoesNotContain(removed.Body.EnumerateArray(), g => g.GetProperty("userEmail").GetString() == email);
        Assert.False(await _h.Permissions.GetIsJobManagerAsync(email));

        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.SettingsAuditEvent WHERE Action = 'job-manager.granted' AND AfterJson LIKE @e;", ("@e", "%" + email + "%")));
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM ductwork.SettingsAuditEvent WHERE Action = 'job-manager.revoked' AND BeforeJson LIKE @e;", ("@e", "%" + email + "%")));
        Assert.Equal(400, (await RunAsync(() => _h.JobManagersApi().Grant(new GrantJobManagerRequest("not an email"), default))).Status);
    }

    [Fact]
    public async Task Granting_the_permission_needs_an_administrator_in_the_data_layer_too()
    {
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => _h.Permissions.GrantJobManagerAsync("x@example.com", AuditContext.ForUser(Owner)));
        await Assert.ThrowsAsync<JobAccessDeniedException>(() => _h.Permissions.RevokeJobManagerAsync("x@example.com", AuditContext.ForUser(Owner)));
    }

    // ---------------------------------------------------------------- the pages

    [Fact]
    public async Task The_archives_page_is_served_only_to_administrators_and_job_managers()
    {
        await GrantManagerAsync();

        Assert.Equal(404, (await RunAsync(() => _h.AppPages(Editor).Archives(default))).Status);
        Assert.Equal(200, (await RunAsync(() => _h.AppPages(Manager).Archives(default))).Status);
        Assert.Equal(200, (await RunAsync(() => _h.AppPages(Admin, admin: true).Archives(default))).Status);
        var list = await RunAsync(() => _h.AppPages(Manager).Index(default));
        Assert.True(list["model"].GetProperty("isJobManager").GetBoolean());
        Assert.False((await RunAsync(() => _h.AppPages(Editor).Index(default)))["model"].GetProperty("isJobManager").GetBoolean());
    }

    [Fact]
    public void Archive_refusals_become_the_answers_a_client_can_act_on()
    {
        Assert.Equal(404, DuctworkApiExceptionAttribute.Map(new ArchiveMissingException("a/b.zip"))!.Value.Status);
        Assert.Equal(422, DuctworkApiExceptionAttribute.Map(new ArchiveCorruptException(["bad hash"]))!.Value.Status);
    }
}
