using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIToolbox.Ductwork.Core.Data;
using AIToolbox.Ductwork.Core.Drawings;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Tasks;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Llm;

public enum VisionApproverRule { EditPermission, JobManager, Admin }

public enum ApprovalKind { PerRun, Standing }

public enum ApprovalStatus { Active, Used, Expired, Revoked }

/// <summary>The administrator's settings for sending drawings to a hosted model. The default, with nothing saved, is off.</summary>
public sealed record VisionPolicyRecord
{
    public const string DefaultNotice =
        "Sending these drawings to a hosted AI model means their content leaves this server and may contain customer information. " +
        "Approve only if your organization's data-governance guidance allows it for this job.";

    public int VisionPolicyId { get; init; }
    public bool Enabled { get; init; }
    public IReadOnlyList<int> AllowedProviderIds { get; init; } = [];
    public VisionApproverRule ApproverRule { get; init; } = VisionApproverRule.EditPermission;
    public string NoticeText { get; init; } = DefaultNotice;
    public int NoticeVersion { get; init; } = 1;
    public int MaxPagesPerApproval { get; init; } = 10;
    public int MaxImagesPerPage { get; init; } = 6;
    public bool RequireReason { get; init; } = true;
    public int ApprovalLifetimeMinutes { get; init; } = 60;
    public IReadOnlyList<string> StandingTaskTypes { get; init; } = [];
    public int StandingMaxPagesPerJob { get; init; }
    public string ChangedBy { get; init; } = "";
    public DateTime? ChangedUtc { get; init; }
}

public sealed record VisionPolicyUpdate
{
    public bool Enabled { get; init; }
    public IReadOnlyList<int> AllowedProviderIds { get; init; } = [];
    public VisionApproverRule ApproverRule { get; init; } = VisionApproverRule.EditPermission;
    public string? NoticeText { get; init; }
    public int MaxPagesPerApproval { get; init; } = 10;
    public int MaxImagesPerPage { get; init; } = 6;
    public bool RequireReason { get; init; } = true;
    public int ApprovalLifetimeMinutes { get; init; } = 60;
    public IReadOnlyList<string> StandingTaskTypes { get; init; } = [];
    public int StandingMaxPagesPerJob { get; init; }
}

public sealed record VisionApprovalRecord
{
    public long VisionApprovalId { get; init; }
    public Guid PublicId { get; init; }
    public long JobId { get; init; }
    public ApprovalKind Kind { get; init; }
    public required string ApprovedBy { get; init; }
    public DateTime ApprovedUtc { get; init; }
    public DateTime ExpiresUtc { get; init; }
    public IReadOnlyList<Guid> SheetIds { get; init; } = [];
    public int PageCount { get; init; }
    public int ImageBudget { get; init; }
    public int ImagesSent { get; init; }
    public int ProviderSnapshotId { get; init; }
    public int NoticeVersion { get; init; }
    public string? Reason { get; init; }
    public ApprovalStatus Status { get; init; }
    public long? UsedByTaskId { get; init; }
    public bool IsExpired { get; init; }
    public string? RevokedBy { get; init; }
    public DateTime? RevokedUtc { get; init; }
    public string? RevokeReason { get; init; }
}

public sealed record ProviderDisclosure(int ProviderId, string DisplayName, string Model, string EndpointHost, string? GovernanceRoute, ProviderSnapshotInfo Snapshot);

public sealed record ApprovalRequest
{
    public required IReadOnlyList<Guid> SheetIds { get; init; }
    public required ProviderDisclosure Provider { get; init; }
    public string? Reason { get; init; }
    /// <summary>The notice version the person was shown. If the wording has changed since, they must read it again.</summary>
    public int AcknowledgedNoticeVersion { get; init; }
}

public sealed record DisclosedSheet(Guid SheetId, string? SheetNumber, string? Title, int PageNo, string FileName);

/// <summary>Everything the approval dialog must say before a person agrees, and whether they may agree at all.</summary>
public sealed record VisionDisclosure
{
    public required VisionPolicyRecord Policy { get; init; }
    public required ProviderDisclosure Provider { get; init; }
    public required IReadOnlyList<DisclosedSheet> Sheets { get; init; }
    public int PageCount { get; init; }
    public int ImageBudget { get; init; }
    public int LifetimeMinutes { get; init; }
    /// <summary>Reasons the approval cannot be given. Empty means it can.</summary>
    public required IReadOnlyList<string> Problems { get; init; }
}

public sealed record VisionGrant(long TransmissionId, long ApprovalId, int ProviderSnapshotId, string ProviderSnapshotHash, IReadOnlyList<int> AllowedProviderIds);

/// <summary>Drawing content may not be sent. The code says why, so the worker and the screen can tell the person.</summary>
public sealed class VisionRefusedException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The control on sending drawings to a hosted model (plan section 6.12). Nothing is sent unless an administrator has switched
/// the capability on and allowed the provider, a person with the right to has approved these sheets for this run, the approval
/// is unexpired and unused by any other task, and the budget of images is not exhausted. Every send is reserved before it
/// happens and recorded after, as counts and hashes only. Switching the capability off takes effect at once.
/// </summary>
public sealed class VisionRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Func<CancellationToken, Task<SqlConnection>> _open;

    public VisionRepository(Func<CancellationToken, Task<SqlConnection>> openConnection) => _open = openConnection;

    // ------------------------------------------------------------------ policy

    public async Task<VisionPolicyRecord> GetPolicyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return await ReadPolicyAsync(connection, null, cancellationToken);
    }

    /// <summary>
    /// An administrator changes the policy. It is a new version, never an edit. Switching the capability off revokes every
    /// approval that has not finished, and cancels queued and running tasks that send drawings.
    /// </summary>
    public async Task<VisionPolicyRecord> SetPolicyAsync(VisionPolicyUpdate update, AuditContext context, CancellationToken cancellationToken = default)
    {
        context.EnsureValid();
        if (context.ActorType != ActorType.User || !context.IsAdmin) throw new JobAccessDeniedException("Only an administrator can change the hosted vision policy.");
        if (update.MaxPagesPerApproval < 1 || update.MaxImagesPerPage < 1 || update.ApprovalLifetimeMinutes < 1 || update.StandingMaxPagesPerJob < 0)
            throw new JobRuleViolationException("The limits must be positive, and the standing page limit cannot be negative.");
        if (update.StandingTaskTypes.Count > 0 && update.StandingMaxPagesPerJob == 0)
            throw new JobRuleViolationException("Standing approval for task types needs a page limit per job.");

        VisionPolicyRecord before, after;
        await using (var connection = await _open(cancellationToken))
        {
            await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                before = await ReadPolicyAsync(connection, tx, cancellationToken);
                var notice = string.IsNullOrWhiteSpace(update.NoticeText) ? before.NoticeText : update.NoticeText.Trim();
                var version = notice == before.NoticeText ? before.NoticeVersion : before.NoticeVersion + 1;

                await JobRepository.ExecAsync(connection, tx,
                    """
                    INSERT INTO ductwork.VisionPolicy (Enabled, AllowedProviderIdsJson, ApproverRule, NoticeText, NoticeVersion, MaxPagesPerApproval, MaxImagesPerPage, RequireReason,
                                                      ApprovalLifetimeMinutes, StandingTaskTypesJson, StandingMaxPagesPerJob, ChangedBy)
                    VALUES (@en, @prov, @rule, @notice, @ver, @pages, @images, @reason, @life, @standing, @standingPages, @by);
                    """, cancellationToken,
                    ("@en", update.Enabled), ("@prov", JsonSerializer.Serialize(update.AllowedProviderIds.Distinct().Order().ToList())), ("@rule", update.ApproverRule.ToString()),
                    ("@notice", notice), ("@ver", version), ("@pages", update.MaxPagesPerApproval), ("@images", update.MaxImagesPerPage), ("@reason", update.RequireReason),
                    ("@life", update.ApprovalLifetimeMinutes), ("@standing", JsonSerializer.Serialize(update.StandingTaskTypes.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList())),
                    ("@standingPages", update.StandingMaxPagesPerJob), ("@by", JobRepository.Actor(context)));
                after = await ReadPolicyAsync(connection, tx, cancellationToken);

                await SettingsAudit.AppendAsync(connection, tx, context, "vision-policy", before.Enabled != after.Enabled ? (after.Enabled ? "vision.enabled" : "vision.disabled") : "vision.policy-changed",
                    after.Enabled ? "Hosted vision is on." : "Hosted vision is off.", DescribePolicy(before), DescribePolicy(after), cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        if (before.Enabled && !after.Enabled) await ShutOffAsync(cancellationToken);
        return after;
    }

    // ------------------------------------------------------------------ approvals

    /// <summary>What the person is about to agree to, in the words the dialog must show, and why they cannot agree if they cannot.</summary>
    public async Task<VisionDisclosure> PreviewApprovalAsync(long jobId, IReadOnlyList<Guid> sheetIds, ProviderDisclosure provider, AuditContext context, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        var policy = await ReadPolicyAsync(connection, null, cancellationToken);
        var sheets = await ReadSheetsAsync(connection, null, jobId, sheetIds, cancellationToken);
        var problems = new List<string>();
        CheckApprovable(policy, provider.ProviderId, sheets, sheetIds, problems);
        var (role, manager) = context.ActorType == ActorType.User
            ? await JobAccess.LoadAsync(connection, null, jobId, context.ActorEmail!, cancellationToken)
            : (null, false);
        if (!MayApprove(policy, role, manager, context.IsAdmin)) problems.Add(ApproverMessage(policy.ApproverRule));
        return new VisionDisclosure
        {
            Policy = policy, Provider = provider, Sheets = sheets.Select(s => s.Disclosed).ToList(), PageCount = sheets.Count, ImageBudget = sheets.Count * policy.MaxImagesPerPage,
            LifetimeMinutes = policy.ApprovalLifetimeMinutes, Problems = problems
        };
    }

    public Task<VisionApprovalRecord> CreateApprovalAsync(long jobId, ApprovalRequest request, AuditContext context, CancellationToken cancellationToken = default)
    {
        if (context.ActorType != ActorType.User) throw new JobRuleViolationException("Only a person can approve sending drawings to a hosted model.");
        return CreateAsync(jobId, request, context, cancellationToken);
    }

    private async Task<VisionApprovalRecord> CreateAsync(long jobId, ApprovalRequest request, AuditContext context, CancellationToken ct)
    {
        int snapshotId;
        await using (var connection = await _open(ct))
            snapshotId = await ProducerStore.EnsureProviderSnapshotAsync(connection, request.Provider.Snapshot, ct);

        return await JobTxn.RunAsync(_open, jobId, context, ct, async t =>
        {
            t.RequireEditable();
            var policy = await ReadPolicyAsync(t.Connection, t.Transaction, t.Token);
            var (role, manager) = await JobAccess.LoadAsync(t.Connection, t.Transaction, jobId, context.ActorEmail!, t.Token);
            if (!MayApprove(policy, role, manager, context.IsAdmin)) throw new JobAccessDeniedException(ApproverMessage(policy.ApproverRule));

            var sheets = await ReadSheetsAsync(t.Connection, t.Transaction, jobId, request.SheetIds, t.Token);
            var problems = new List<string>();
            CheckApprovable(policy, request.Provider.ProviderId, sheets, request.SheetIds, problems);
            if (policy.RequireReason && string.IsNullOrWhiteSpace(request.Reason)) problems.Add("A written reason is required.");
            if (request.AcknowledgedNoticeVersion != policy.NoticeVersion) problems.Add("The data-governance notice has changed. Read it again and confirm.");
            if (problems.Count > 0) throw new JobRuleViolationException(string.Join(" ", problems));

            var id = await InsertApprovalAsync(t, ApprovalKind.PerRun, JobRepository.Actor(context), policy, sheets.Select(s => s.PublicId).ToList(), snapshotId, Clean(request.Reason), policy.ApprovalLifetimeMinutes);
            var approval = (await ReadApprovalsAsync(t.Connection, t.Transaction, "WHERE a.VisionApprovalId = @id", t.Token, ("@id", id))).Single();
            await t.AuditAtCurrentVersionAsync(new AuditEntry("vision", "vision.approved",
                $"Approved sending {approval.PageCount} page(s) to {request.Provider.DisplayName} ({request.Provider.Model}) for one run.", "VisionApproval", approval.PublicId.ToString("D"), null,
                DescribeApproval(approval, request.Provider)));
            return approval;
        }, requires: JobCapability.Edit);
    }

    public Task<VisionApprovalRecord> RevokeApprovalAsync(long jobId, long approvalId, string reason, AuditContext context, CancellationToken cancellationToken = default) =>
        JobTxn.RunAsync(_open, jobId, context, cancellationToken, async t =>
        {
            var why = Clean(reason) ?? throw new JobRuleViolationException("Revoking an approval needs a reason.");
            var approval = (await ReadApprovalsAsync(t.Connection, t.Transaction, "WITH (UPDLOCK, ROWLOCK) WHERE a.VisionApprovalId = @id AND a.JobId = @j", t.Token, ("@id", approvalId), ("@j", jobId))).SingleOrDefault()
                ?? throw new KeyNotFoundException($"Approval {approvalId} is not on this job.");
            if (approval.Status == ApprovalStatus.Revoked) return approval;
            await RevokeCoreAsync(t, approval, why);
            return (await ReadApprovalsAsync(t.Connection, t.Transaction, "WHERE a.VisionApprovalId = @id", t.Token, ("@id", approvalId))).Single();
        }, requires: JobCapability.Edit);

    public async Task<IReadOnlyList<VisionApprovalRecord>> ListApprovalsAsync(long jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return await ReadApprovalsAsync(connection, null, "WHERE a.JobId = @j ORDER BY a.VisionApprovalId DESC", cancellationToken, ("@j", jobId));
    }

    public async Task<VisionApprovalRecord?> GetApprovalAsync(long jobId, Guid publicId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return (await ReadApprovalsAsync(connection, null, "WHERE a.JobId = @j AND a.PublicId = @p", cancellationToken, ("@j", jobId), ("@p", publicId))).SingleOrDefault();
    }

    /// <summary>Marks approvals that ran out of time as expired. They were already unusable; this makes the record say so.</summary>
    [NoAudit("Housekeeping only: expiry is decided by the clock, and an approval past its time is refused whatever its status says.")]
    public async Task<int> ExpireStaleAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        return await JobRepository.ExecAsync(connection, null, "UPDATE ductwork.VisionApproval SET Status = 'Expired' WHERE Status = 'Active' AND ExpiresUtc <= SYSUTCDATETIME();", cancellationToken);
    }

    // ------------------------------------------------------------------ queueing a task that sends drawings

    /// <summary>
    /// Called while a task that sends drawings is being queued. A given approval must be live, for this job and this very model,
    /// and unused; it is then bound to the task. With none given, a task type the administrator approved in advance gets a
    /// standing approval within its page limit. Anything else is refused.
    /// </summary>
    internal static async Task<long> BindApprovalAsync(JobTxn t, long? approvalId, string taskType, long producerId)
    {
        var policy = await ReadPolicyAsync(t.Connection, t.Transaction, t.Token);
        if (!policy.Enabled)
            throw new JobRuleViolationException("Sending drawings to a hosted model is switched off by the administrator, so this task cannot be queued.");

        var producerSnapshot = await SnapshotOfProducerAsync(t, producerId)
            ?? throw new JobRuleViolationException("This task type is not bound to a hosted model.");

        if (approvalId is null)
        {
            if (!policy.StandingTaskTypes.Contains(taskType, StringComparer.Ordinal))
                throw new JobRuleViolationException("A task that sends drawing content to a hosted model needs an approval for it.");
            return await IssueStandingAsync(t, policy, producerSnapshot);
        }

        var approval = (await ReadApprovalsAsync(t.Connection, t.Transaction, "WITH (UPDLOCK, ROWLOCK) WHERE a.VisionApprovalId = @id", t.Token, ("@id", approvalId.Value))).SingleOrDefault();
        if (approval is null || approval.JobId != t.Job.JobId) throw new JobRuleViolationException("That approval is not on this job.");
        if (approval.Status != ApprovalStatus.Active || approval.IsExpired)
            throw new JobRuleViolationException(approval.Status == ApprovalStatus.Used ? "That approval was already used for another run." : "That approval has expired or been revoked. Approve again.");
        if (approval.ProviderSnapshotId != producerSnapshot)
            throw new JobRuleViolationException("That approval was given for a different model than the one this task type uses.");
        return approval.VisionApprovalId;
    }

    /// <summary>After the task row exists: the approval is used by exactly this task.</summary>
    internal static async Task MarkUsedAsync(JobTxn t, long approvalId, long taskId)
    {
        var moved = await JobRepository.ExecAsync(t.Connection, t.Transaction,
            "UPDATE ductwork.VisionApproval SET Status = 'Used', UsedByTaskId = @task WHERE VisionApprovalId = @id AND Status = 'Active' AND ExpiresUtc > SYSUTCDATETIME();",
            t.Token, ("@task", taskId), ("@id", approvalId));
        if (moved == 0) throw new JobRuleViolationException("That approval could not be used. It may have expired or been used by another run.");
    }

    private static async Task<long> IssueStandingAsync(JobTxn t, VisionPolicyRecord policy, int providerSnapshotId)
    {
        var used = await ScalarAsync(t, "SELECT ISNULL(SUM(PageCount), 0) FROM ductwork.VisionApproval WHERE JobId = @j AND Kind = 'Standing' AND Status <> 'Revoked';", ("@j", t.Job.JobId));
        var sheets = (await ReadSheetsAsync(t.Connection, t.Transaction, t.Job.JobId, null, t.Token)).Where(s => s.InScope && s.DuplicateOfPublicId is null).ToList();
        var room = policy.StandingMaxPagesPerJob - (int)used;
        if (sheets.Count == 0) throw new JobRuleViolationException("This job has no in-scope sheets to send.");
        if (sheets.Count > room)
            throw new JobRuleViolationException($"The standing approval for this task type allows {policy.StandingMaxPagesPerJob} pages per job and {Math.Max(0, room)} remain, but {sheets.Count} would be sent. Ask for a per-run approval.");

        var id = await InsertApprovalAsync(t, ApprovalKind.Standing, "policy:standing", policy, sheets.Select(s => s.PublicId).ToList(), providerSnapshotId, "Standing approval set by the administrator.", policy.ApprovalLifetimeMinutes);
        var approval = (await ReadApprovalsAsync(t.Connection, t.Transaction, "WHERE a.VisionApprovalId = @id", t.Token, ("@id", id))).Single();
        await t.AuditAtCurrentVersionAsync(new AuditEntry("vision", "vision.standing-issued", $"A standing approval covered {approval.PageCount} page(s) for this run.", "VisionApproval", approval.PublicId.ToString("D"), null,
            DescribeApproval(approval, null)));
        return id;
    }

    // ------------------------------------------------------------------ the gateway's checks

    /// <summary>
    /// The gateway asks before every send. All the checks run here, in one transaction, and the images are reserved against the
    /// approval's budget before anything leaves, so concurrent calls cannot overspend it. Any failure is a refusal that says why.
    /// </summary>
    public async Task<VisionGrant> AuthorizeSendAsync(long taskId, AuditContext worker, IReadOnlyList<Guid> sheetIds, int imageCount, long totalBytes, string directive,
        CancellationToken cancellationToken = default)
    {
        if (worker.ActorType != ActorType.Worker || string.IsNullOrWhiteSpace(worker.ActorDisplayName))
            throw new ArgumentException("Only a worker sends images, so the context must be a worker's, with its id.", nameof(worker));
        if (imageCount < 1) throw new VisionRefusedException("no-images", "There are no images to send.");
        if (sheetIds.Count == 0) throw new VisionRefusedException("no-sheets", "Say which sheets the images come from.");

        var existing = await new ProcessingTaskRepository(_open).GetAsync(taskId, cancellationToken)
            ?? throw new VisionRefusedException("not-running", "That task does not exist.");

        return await JobTxn.RunAsync(_open, existing.JobId, worker, cancellationToken, async t =>
        {
            // The administrator's switch is checked first, so a worker told to stop is told why.
            var policy = await ReadPolicyAsync(t.Connection, t.Transaction, t.Token);
            if (!policy.Enabled) throw new VisionRefusedException("disabled", "Sending drawings to a hosted model has been switched off by the administrator.");

            var task = await ProcessingTaskRepository.ReadAsync(t.Connection, t.Transaction, taskId, forUpdate: false, t.Token)
                ?? throw new VisionRefusedException("not-running", "That task does not exist.");
            if (task.Status != ProcessingTaskStatus.Running || task.LeaseOwner != worker.ActorDisplayName || task.CancelRequested)
                throw new VisionRefusedException("not-running", "This task is not running under this worker.");
            if (!task.SendsDrawingContentToHostedModel || task.VisionApprovalId is not { } approvalId)
                throw new VisionRefusedException("not-approved", "This task type does not send drawing content to a hosted model, so it has no approval to do so.");

            var approval = (await ReadApprovalsAsync(t.Connection, t.Transaction, "WITH (UPDLOCK, ROWLOCK) WHERE a.VisionApprovalId = @id", t.Token, ("@id", approvalId))).Single();
            if (approval.Status == ApprovalStatus.Revoked) throw new VisionRefusedException("revoked", "The approval for this run was revoked.");
            if (approval.IsExpired || approval.Status == ApprovalStatus.Expired) throw new VisionRefusedException("expired", "The approval for this run has expired. Approve again and re-run.");
            if (approval.Status != ApprovalStatus.Used || approval.UsedByTaskId != taskId) throw new VisionRefusedException("not-approved", "This task does not hold an approval.");

            var producerSnapshot = await SnapshotOfProducerAsync(t, task.ProducerId);
            if (producerSnapshot != approval.ProviderSnapshotId)
                throw new VisionRefusedException("provider", "The approval was given for a different model than this task uses.");

            var stray = sheetIds.Where(s => !approval.SheetIds.Contains(s)).ToList();
            if (stray.Count > 0) throw new VisionRefusedException("sheet-not-approved", $"{stray.Count} of those sheets are not covered by the approval.");
            if (approval.ImagesSent + imageCount > approval.ImageBudget)
                throw new VisionRefusedException("budget", $"The approval allows {approval.ImageBudget} images and {approval.ImagesSent} have been sent, so {imageCount} more is too many.");

            await JobRepository.ExecAsync(t.Connection, t.Transaction, "UPDATE ductwork.VisionApproval SET ImagesSent = ImagesSent + @n WHERE VisionApprovalId = @id;", t.Token, ("@n", imageCount), ("@id", approvalId));
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directive ?? ""))).ToLowerInvariant();
            var transmission = await ScalarAsync(t,
                """
                INSERT INTO ductwork.VisionTransmission (JobId, VisionApprovalId, ProcessingTaskId, ProviderSnapshotId, SheetIdsJson, ImageCount, TotalBytes, DirectiveSha256, RequestedBy)
                OUTPUT INSERTED.VisionTransmissionId
                VALUES (@j, @a, @t, @p, @s, @n, @b, @h, @by);
                """,
                ("@j", t.Job.JobId), ("@a", approvalId), ("@t", taskId), ("@p", producerSnapshot), ("@s", JsonSerializer.Serialize(sheetIds.Distinct().ToList())), ("@n", imageCount),
                ("@b", totalBytes), ("@h", hash), ("@by", JobRepository.Actor(worker)));
            await t.AuditForTaskAsync(new AuditEntry("vision", "vision.sending", $"Sending {imageCount} image(s) from {sheetIds.Distinct().Count()} sheet(s) to the approved model.", "VisionApproval",
                approval.PublicId.ToString("D"), null, JobRepository.Json(new() { ["images"] = imageCount, ["sheets"] = sheetIds.Distinct().Count(), ["bytes"] = totalBytes, ["directiveSha256"] = hash })),
                taskId, task.ProducerId);
            await using var hashCommand = t.Connection.CreateCommand();
            hashCommand.Transaction = t.Transaction;
            hashCommand.CommandText = "SELECT ContentHash FROM ductwork.ProviderSnapshot WHERE ProviderSnapshotId = @s;";
            hashCommand.Parameters.AddWithValue("@s", producerSnapshot.Value);
            var snapshotHash = (string)(await hashCommand.ExecuteScalarAsync(t.Token))!;
            return new VisionGrant(transmission, approvalId, producerSnapshot.Value, snapshotHash, policy.AllowedProviderIds);
        });
    }

    /// <summary>The send finished. A request refused before it left the server hands its images back to the budget, because nothing was disclosed.</summary>
    public async Task CompleteSendAsync(long transmissionId, bool sent, bool leftTheServer, string? error, AuditContext worker, CancellationToken cancellationToken = default)
    {
        long jobId;
        await using (var connection = await _open(cancellationToken))
        {
            var found = await ScalarOnConnectionAsync(connection, "SELECT JobId FROM ductwork.VisionTransmission WHERE VisionTransmissionId = @id;", cancellationToken, ("@id", transmissionId));
            jobId = found ?? throw new KeyNotFoundException($"Transmission {transmissionId} does not exist.");
        }

        await JobTxn.RunAsync(_open, jobId, worker, cancellationToken, async t =>
        {
            var moved = await JobRepository.ExecAsync(t.Connection, t.Transaction,
                "UPDATE ductwork.VisionTransmission SET Outcome = @o, CompletedUtc = SYSUTCDATETIME(), ErrorMessage = @e WHERE VisionTransmissionId = @id AND Outcome = 'Reserved';",
                t.Token, ("@o", sent ? "Sent" : "Failed"), ("@e", Clean(error, 1000)), ("@id", transmissionId));
            if (moved == 0) return false;
            if (!sent && !leftTheServer)
                await JobRepository.ExecAsync(t.Connection, t.Transaction,
                    """
                    UPDATE a SET ImagesSent = a.ImagesSent - x.ImageCount
                    FROM ductwork.VisionApproval a JOIN ductwork.VisionTransmission x ON x.VisionApprovalId = a.VisionApprovalId
                    WHERE x.VisionTransmissionId = @id;
                    """, t.Token, ("@id", transmissionId));
            return true;
        });
    }

    public async Task<IReadOnlyList<(long Id, int Images, long Bytes, string Outcome, DateTime RequestedUtc, string? Error)>> ListTransmissionsAsync(long jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT VisionTransmissionId, ImageCount, TotalBytes, Outcome, RequestedUtc, ErrorMessage FROM ductwork.VisionTransmission WHERE JobId = @j ORDER BY VisionTransmissionId;";
        command.Parameters.AddWithValue("@j", jobId);
        var list = new List<(long, int, long, string, DateTime, string?)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add((reader.GetInt64(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetString(3), reader.GetDateTime(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        return list;
    }

    // ------------------------------------------------------------------ switching off

    /// <summary>Takes effect at once: unfinished approvals are revoked, queued tasks that send drawings are cancelled, and running ones are told to stop (the gateway also refuses them).</summary>
    private async Task ShutOffAsync(CancellationToken ct)
    {
        var system = AuditContext.ForSystem("hosted vision switched off");
        List<(long Job, long Approval)> approvals;
        await using (var connection = await _open(ct))
            approvals = await ReadPairsAsync(connection, "SELECT JobId, VisionApprovalId FROM ductwork.VisionApproval WHERE Status IN ('Active', 'Used');", ct);

        foreach (var (job, approval) in approvals)
            await RevokeOneAsync(job, approval, "Hosted vision was switched off by the administrator.", system, ct);

        await CancelTasksAsync("SELECT ProcessingTaskId FROM ductwork.ProcessingTask WHERE SendsDrawingContentToHostedModel = 1 AND Status IN ('Queued', 'Running');", system, ct);
    }


    private async Task RevokeOneAsync(long jobId, long approvalId, string reason, AuditContext context, CancellationToken ct) =>
        await JobTxn.RunAsync(_open, jobId, context, ct, async t =>
        {
            var approval = (await ReadApprovalsAsync(t.Connection, t.Transaction, "WITH (UPDLOCK, ROWLOCK) WHERE a.VisionApprovalId = @id", t.Token, ("@id", approvalId))).Single();
            if (approval.Status is ApprovalStatus.Active or ApprovalStatus.Used) await RevokeCoreAsync(t, approval, reason);
            return true;
        });

    private async Task CancelTasksAsync(string sql, AuditContext system, CancellationToken ct)
    {
        List<long> ids;
        await using (var connection = await _open(ct))
            ids = (await ReadPairsAsync(connection, sql.Replace("SELECT ProcessingTaskId", "SELECT ProcessingTaskId, ProcessingTaskId"), ct)).Select(p => p.Job).ToList();
        var tasks = new ProcessingTaskRepository(_open);
        foreach (var id in ids)
        {
            try { await tasks.CancelAsync(id, system, ct); }
            catch (JobRuleViolationException) { /* already finished */ }
        }
    }

    private static async Task RevokeCoreAsync(JobTxn t, VisionApprovalRecord approval, string reason)
    {
        await JobRepository.ExecAsync(t.Connection, t.Transaction,
            "UPDATE ductwork.VisionApproval SET Status = 'Revoked', RevokedBy = @by, RevokedUtc = SYSUTCDATETIME(), RevokeReason = @why WHERE VisionApprovalId = @id;",
            t.Token, ("@by", JobRepository.Actor(t.Context)), ("@why", reason), ("@id", approval.VisionApprovalId));
        await t.AuditAtCurrentVersionAsync(new AuditEntry("vision", "vision.revoked", $"Revoked an approval to send drawings to a hosted model: {reason}", "VisionApproval", approval.PublicId.ToString("D"),
            DescribeApproval(approval, null), null));
    }

    // ------------------------------------------------------------------ rules

    private static bool MayApprove(VisionPolicyRecord policy, JobRole? role, bool manager, bool admin) => policy.ApproverRule switch
    {
        VisionApproverRule.Admin => admin,
        VisionApproverRule.JobManager => admin || manager,
        _ => JobPermissions.HasEditPermission(role)
    };

    private static string ApproverMessage(VisionApproverRule rule) => rule switch
    {
        VisionApproverRule.Admin => "Only an administrator can approve sending drawings to a hosted model.",
        VisionApproverRule.JobManager => "Only a Job Manager or an administrator can approve sending drawings to a hosted model.",
        _ => "You need edit permission on this job to approve sending its drawings to a hosted model."
    };

    private sealed record SheetRow(Guid PublicId, bool InScope, Guid? DuplicateOfPublicId, DisclosedSheet Disclosed);

    private static void CheckApprovable(VisionPolicyRecord policy, int providerId, IReadOnlyList<SheetRow> found, IReadOnlyList<Guid>? requested, List<string> problems)
    {
        if (!policy.Enabled) problems.Add("Sending drawings to a hosted model is switched off by the administrator.");
        if (!policy.AllowedProviderIds.Contains(providerId)) problems.Add("That model has not been allowed by the administrator for drawings.");
        if (requested is null || requested.Count == 0) problems.Add("Choose at least one sheet.");
        else
        {
            if (requested.Distinct().Count() != found.Count) problems.Add("One or more of those sheets are not on this job.");
            if (found.Any(s => !s.InScope)) problems.Add("A sheet that is out of scope cannot be sent.");
            if (found.Any(s => s.DuplicateOfPublicId is not null)) problems.Add("A duplicate sheet cannot be sent; choose the original.");
            if (found.Count > policy.MaxPagesPerApproval) problems.Add($"At most {policy.MaxPagesPerApproval} pages can be approved at once.");
        }
    }

    // ------------------------------------------------------------------ reads and writes

    private static async Task<long> InsertApprovalAsync(JobTxn t, ApprovalKind kind, string by, VisionPolicyRecord policy, IReadOnlyList<Guid> sheetIds, int snapshotId, string? reason, int lifetimeMinutes) =>
        await ScalarAsync(t,
            """
            INSERT INTO ductwork.VisionApproval (JobId, Kind, ApprovedBy, ExpiresUtc, SheetIdsJson, PageCount, ImageBudget, ProviderSnapshotId, NoticeVersion, Reason)
            OUTPUT INSERTED.VisionApprovalId
            VALUES (@j, @k, @by, DATEADD(MINUTE, @life, SYSUTCDATETIME()), @sheets, @pages, @budget, @snap, @notice, @reason);
            """,
            ("@j", t.Job.JobId), ("@k", kind.ToString()), ("@by", by), ("@life", lifetimeMinutes), ("@sheets", JsonSerializer.Serialize(sheetIds)), ("@pages", sheetIds.Count),
            ("@budget", sheetIds.Count * policy.MaxImagesPerPage), ("@snap", snapshotId), ("@notice", policy.NoticeVersion), ("@reason", reason));

    private static async Task<int?> SnapshotOfProducerAsync(JobTxn t, long producerId)
    {
        var result = await ScalarOrNullAsync(t, "SELECT ProviderSnapshotId FROM ductwork.Producer WHERE ProducerId = @p AND Kind = 'Llm';", ("@p", producerId));
        return result is null ? null : (int)result.Value;
    }

    internal static async Task<VisionPolicyRecord> ReadPolicyAsync(SqlConnection c, SqlTransaction? tx, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            """
            SELECT TOP 1 VisionPolicyId, Enabled, AllowedProviderIdsJson, ApproverRule, NoticeText, NoticeVersion, MaxPagesPerApproval, MaxImagesPerPage, RequireReason,
                         ApprovalLifetimeMinutes, StandingTaskTypesJson, StandingMaxPagesPerJob, ChangedBy, ChangedUtc
            FROM ductwork.VisionPolicy ORDER BY VisionPolicyId DESC;
            """;
        await using var r = await command.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return new VisionPolicyRecord();
        return new VisionPolicyRecord
        {
            VisionPolicyId = r.GetInt32(0), Enabled = r.GetBoolean(1), AllowedProviderIds = JsonSerializer.Deserialize<List<int>>(r.GetString(2), Json) ?? [],
            ApproverRule = Enum.Parse<VisionApproverRule>(r.GetString(3)), NoticeText = r.GetString(4), NoticeVersion = r.GetInt32(5), MaxPagesPerApproval = r.GetInt32(6),
            MaxImagesPerPage = r.GetInt32(7), RequireReason = r.GetBoolean(8), ApprovalLifetimeMinutes = r.GetInt32(9),
            StandingTaskTypes = JsonSerializer.Deserialize<List<string>>(r.GetString(10), Json) ?? [], StandingMaxPagesPerJob = r.GetInt32(11), ChangedBy = r.GetString(12), ChangedUtc = r.GetDateTime(13)
        };
    }

    private static async Task<List<SheetRow>> ReadSheetsAsync(SqlConnection c, SqlTransaction? tx, long jobId, IReadOnlyList<Guid>? publicIds, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        var filter = "";
        if (publicIds is not null)
        {
            if (publicIds.Count == 0) return [];
            var names = publicIds.Distinct().Select((_, i) => "@g" + i).ToList();
            filter = $" AND s.PublicId IN ({string.Join(",", names)})";
            var i = 0;
            foreach (var id in publicIds.Distinct()) command.Parameters.AddWithValue(names[i++], id);
        }

        command.CommandText =
            $"""
            SELECT s.PublicId, s.InScope, d.PublicId, s.SheetNumber, s.Title, s.PageNo, f.OriginalName
            FROM ductwork.Sheet s
            JOIN ductwork.SourceFile f ON f.SourceFileId = s.SourceFileId
            LEFT JOIN ductwork.Sheet d ON d.SheetId = s.DuplicateOfSheetId
            WHERE s.JobId = @j{filter}
            ORDER BY s.SourceFileId, s.PageNo;
            """;
        command.Parameters.AddWithValue("@j", jobId);
        var list = new List<SheetRow>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new SheetRow(r.GetGuid(0), r.GetBoolean(1), r.IsDBNull(2) ? null : r.GetGuid(2),
                new DisclosedSheet(r.GetGuid(0), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5), r.GetString(6))));
        return list;
    }

    private static async Task<List<VisionApprovalRecord>> ReadApprovalsAsync(SqlConnection c, SqlTransaction? tx, string whereAndHint, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        var hint = whereAndHint.StartsWith("WITH", StringComparison.Ordinal) ? whereAndHint[..(whereAndHint.IndexOf(')') + 1)] + " " : "";
        var where = whereAndHint[hint.Length..];
        command.CommandText =
            $"""
            SELECT a.VisionApprovalId, a.PublicId, a.JobId, a.Kind, a.ApprovedBy, a.ApprovedUtc, a.ExpiresUtc, a.SheetIdsJson, a.PageCount, a.ImageBudget, a.ImagesSent, a.ProviderSnapshotId,
                   a.NoticeVersion, a.Reason, a.Status, a.UsedByTaskId, CASE WHEN a.ExpiresUtc <= SYSUTCDATETIME() THEN 1 ELSE 0 END, a.RevokedBy, a.RevokedUtc, a.RevokeReason
            FROM ductwork.VisionApproval a {hint}{where}
            """;
        foreach (var (name, value) in args) JobRepository.P(command, name, value);
        var list = new List<VisionApprovalRecord>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new VisionApprovalRecord
            {
                VisionApprovalId = r.GetInt64(0), PublicId = r.GetGuid(1), JobId = r.GetInt64(2), Kind = Enum.Parse<ApprovalKind>(r.GetString(3)), ApprovedBy = r.GetString(4),
                ApprovedUtc = r.GetDateTime(5), ExpiresUtc = r.GetDateTime(6), SheetIds = JsonSerializer.Deserialize<List<Guid>>(r.GetString(7), Json) ?? [], PageCount = r.GetInt32(8),
                ImageBudget = r.GetInt32(9), ImagesSent = r.GetInt32(10), ProviderSnapshotId = r.GetInt32(11), NoticeVersion = r.GetInt32(12), Reason = r.IsDBNull(13) ? null : r.GetString(13),
                Status = Enum.Parse<ApprovalStatus>(r.GetString(14)), UsedByTaskId = r.IsDBNull(15) ? null : r.GetInt64(15), IsExpired = r.GetInt32(16) == 1,
                RevokedBy = r.IsDBNull(17) ? null : r.GetString(17), RevokedUtc = r.IsDBNull(18) ? null : r.GetDateTime(18), RevokeReason = r.IsDBNull(19) ? null : r.GetString(19)
            });
        return list;
    }

    private static async Task<List<(long Job, long Approval)>> ReadPairsAsync(SqlConnection c, string sql, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        var list = new List<(long, long)>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add((r.GetInt64(0), r.GetInt64(1)));
        return list;
    }

    private static string DescribePolicy(VisionPolicyRecord p) => JobRepository.Json(new()
    {
        ["enabled"] = p.Enabled, ["providers"] = string.Join(",", p.AllowedProviderIds), ["approver"] = p.ApproverRule.ToString(), ["noticeVersion"] = p.NoticeVersion,
        ["maxPages"] = p.MaxPagesPerApproval, ["imagesPerPage"] = p.MaxImagesPerPage, ["requireReason"] = p.RequireReason, ["lifetimeMinutes"] = p.ApprovalLifetimeMinutes,
        ["standingTypes"] = string.Join(",", p.StandingTaskTypes), ["standingPages"] = p.StandingMaxPagesPerJob
    });

    private static string DescribeApproval(VisionApprovalRecord a, ProviderDisclosure? provider) => JobRepository.Json(new()
    {
        ["kind"] = a.Kind.ToString(), ["pages"] = a.PageCount, ["imageBudget"] = a.ImageBudget, ["expiresUtc"] = a.ExpiresUtc, ["sheets"] = string.Join(",", a.SheetIds),
        ["provider"] = provider?.DisplayName, ["model"] = provider?.Model, ["route"] = provider?.GovernanceRoute, ["reason"] = a.Reason, ["status"] = a.Status.ToString()
    });

    private static string? Clean(string? text, int max = 400)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }

    private static async Task<long?> ScalarOnConnectionAsync(SqlConnection c, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args) JobRepository.P(command, name, value);
        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<long?> ScalarOrNullAsync(JobTxn t, string sql, params (string Name, object? Value)[] args)
    {
        await using var command = t.Connection.CreateCommand();
        command.Transaction = t.Transaction;
        command.CommandText = sql;
        foreach (var (name, value) in args) JobRepository.P(command, name, value);
        var result = await command.ExecuteScalarAsync(t.Token);
        return result is null or DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<long> ScalarAsync(JobTxn t, string sql, params (string Name, object? Value)[] args) =>
        await ScalarOrNullAsync(t, sql, args) ?? throw new InvalidOperationException("The statement returned nothing.");
}
