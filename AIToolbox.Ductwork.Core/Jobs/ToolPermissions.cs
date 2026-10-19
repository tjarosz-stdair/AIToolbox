using AIToolbox.Ductwork.Core.Tasks;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Jobs;

public sealed record JobManagerGrant(string UserEmail, string GrantedBy, DateTime GrantedUtc);

/// <summary>
/// The Job Manager permission (plan section 8.4): archive, purge and restore any job, member or not. It grants no view or edit
/// access to a job's contents. A Ductwork-only permission, given by an administrator, and every grant and revocation is recorded in
/// the settings audit log.
/// </summary>
public sealed class ToolPermissionRepository(Func<CancellationToken, Task<SqlConnection>> open)
{
    private const string Permission = "JobManager";

    public async Task<IReadOnlyList<JobManagerGrant>> ListJobManagersAsync(CancellationToken ct = default)
    {
        await using var c = await open(ct);
        await using var command = c.CreateCommand();
        command.CommandText = "SELECT UserEmail, GrantedBy, GrantedUtc FROM ductwork.ToolPermission WHERE Permission = @p ORDER BY UserEmail;";
        command.Parameters.AddWithValue("@p", Permission);
        var list = new List<JobManagerGrant>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new JobManagerGrant(r.GetString(0), r.GetString(1), DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc)));
        return list;
    }

    public async Task<bool> GetIsJobManagerAsync(string email, CancellationToken ct = default)
    {
        await using var c = await open(ct);
        await using var command = c.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ductwork.ToolPermission WHERE UserEmail = @e AND Permission = @p;";
        command.Parameters.AddWithValue("@e", AuditContext.Normalize(email));
        command.Parameters.AddWithValue("@p", Permission);
        return (int)(await command.ExecuteScalarAsync(ct))! > 0;
    }

    public async Task<IReadOnlyList<JobManagerGrant>> GrantJobManagerAsync(string email, AuditContext context, CancellationToken ct = default)
    {
        RequireAdmin(context);
        var normalized = AuditContext.Normalize(email);
        if (normalized.Length is 0 or > 256 || !normalized.Contains('@') || normalized.Any(char.IsWhiteSpace))
            throw new ArgumentException("Enter the person's email address.");

        await using var c = await open(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        var added = await JobRepository.ExecAsync(c, tx,
            "INSERT INTO ductwork.ToolPermission (UserEmail, Permission, GrantedBy) SELECT @e, @p, @by WHERE NOT EXISTS (SELECT 1 FROM ductwork.ToolPermission WITH (UPDLOCK, HOLDLOCK) WHERE UserEmail = @e AND Permission = @p);", ct,
            ("@e", normalized), ("@p", Permission), ("@by", JobRepository.Actor(context)));
        if (added > 0)
            await SettingsAudit.AppendAsync(c, tx, context, "tool-permission", "job-manager.granted", $"Made {normalized} a Job Manager (may archive and restore any job, with no access to job contents).",
                null, JobRepository.Json(new() { ["user"] = normalized }), ct);
        await tx.CommitAsync(ct);
        return await ListJobManagersAsync(ct);
    }

    public async Task<IReadOnlyList<JobManagerGrant>> RevokeJobManagerAsync(string email, AuditContext context, CancellationToken ct = default)
    {
        RequireAdmin(context);
        var normalized = AuditContext.Normalize(email);

        await using var c = await open(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        var removed = await JobRepository.ExecAsync(c, tx, "DELETE FROM ductwork.ToolPermission WHERE UserEmail = @e AND Permission = @p;", ct, ("@e", normalized), ("@p", Permission));
        if (removed > 0)
            await SettingsAudit.AppendAsync(c, tx, context, "tool-permission", "job-manager.revoked", $"Removed {normalized} as a Job Manager.", JobRepository.Json(new() { ["user"] = normalized }), null, ct);
        await tx.CommitAsync(ct);
        return await ListJobManagersAsync(ct);
    }

    private static void RequireAdmin(AuditContext context)
    {
        context.EnsureValid();
        if (!context.IsAdmin) throw new JobAccessDeniedException("Only an administrator can grant or remove the Job Manager permission.");
    }
}
