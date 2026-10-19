using System.Security.Cryptography;
using System.Text.Json;
using AIToolbox.Ductwork.Core.Activity;
using AIToolbox.Ductwork.Core.Counting;
using AIToolbox.Ductwork.Core.Drawings;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Rules;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Reports;

public sealed record ReportRecord
{
    public long ReportId { get; init; }
    public Guid PublicId { get; init; }
    public long JobId { get; init; }
    public int CompletionVersion { get; init; }
    public string Format { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long Bytes { get; init; }
    public string ModelHash { get; init; } = "";
    public bool IsFinal { get; init; }
    public string GeneratedBy { get; init; } = "";
    public DateTime GeneratedUtc { get; init; }
}

/// <summary>A stored report no longer matches the hash recorded when it was issued.</summary>
public sealed class ReportIntegrityException() : InvalidOperationException("This report file no longer matches what was issued. Contact your administrator.");

/// <summary>
/// Count sheet files (plan section 12). A draft is drawn on demand from the job's current state and never stored. An issued report is made
/// while a job is being completed, from the state the job is frozen in, stored with its hash and the hash of the model it was drawn from, and
/// kept as a record. If issuing fails the job stays Completing and can try again: it is never silently Complete without its reports.
/// </summary>
public sealed class ReportService
{
    private readonly Func<CancellationToken, Task<SqlConnection>> _open;
    private readonly JobRepository _jobs;
    private readonly CountSheetRepository _sheets;
    private readonly RuleRepository _rules;
    private readonly ActivityRepository _activity;
    private readonly IArtifactStore _store;
    private readonly IReadOnlyList<IReportRenderer> _renderers;

    public ReportService(Func<CancellationToken, Task<SqlConnection>> open, JobRepository jobs, CountSheetRepository sheets, RuleRepository rules, ActivityRepository activity,
        IArtifactStore store, IEnumerable<IReportRenderer>? renderers = null)
    {
        _open = open;
        _jobs = jobs;
        _sheets = sheets;
        _rules = rules;
        _activity = activity;
        _store = store;
        _renderers = (renderers ?? [new ExcelReportRenderer(), new HtmlReportRenderer()]).ToList();
    }

    public IReadOnlyList<string> Formats => _renderers.Select(r => r.Format).ToList();

    // ------------------------------------------------------------------ reads

    /// <summary>The job's effective state as the neutral model every report is drawn from.</summary>
    public async Task<ReportModel> GetModelAsync(long jobId, CancellationToken ct = default)
    {
        var job = await _jobs.GetAsync(jobId, ct) ?? throw new JobNotFoundException(jobId);
        var sheet = await _sheets.BuildAsync(jobId, ct);

        List<EntityRecord> entities;
        await using (var c = await _open(ct)) entities = (await EntityRepository.ListAsync(c, null, jobId, new EntityFilter(), ct)).ToList();
        var names = sheet.Blocks.Where(b => b.BuildingTypeId is not null).ToDictionary(b => b.BuildingTypeId!.Value, b => b.Name);

        var details = entities
            .Where(e => e.ReviewStatus != Contracts.ReviewStatus.Rejected)
            .OrderBy(e => e.BuildingTypeId is { } id && names.TryGetValue(id, out var n) ? n : "~", StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Type, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.EntityId)
            .Select(e => new ReportDetail(
                e.BuildingTypeId is { } id2 && names.TryGetValue(id2, out var name) ? name : CountSheetBuilder.UnassignedName, e.Type, e.Category, e.Tag, e.Size, e.Manufacturer, e.Model, e.CustomerPartCode,
                e.Quantity, e.Unit.ToString(), e.Method.ToString(), e.Confidence, e.ReviewStatus.ToString(), e.Disposition.ToString(), e.ResolutionSource.ToString(), e.IsStale,
                e.DispositionReason, e.Evidence, e.SheetViewRef)).ToList();

        // Rules that never changed anything (still only proposed) are left out: they are not part of how the counts were read.
        var rules = (await _rules.ListForJobAsync(jobId, ct)).Where(r => r.Status != RuleStatus.Proposed)
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Version)
            .Select(r => new ReportRule(r.Name, r.Scope.ToString(), r.Status.ToString(), r.Version, r.Description, Describe(r.Match), Describe(r.Action), r.ApprovedBy, r.ApprovedUtc)).ToList();

        var time = await _activity.GetSummaryAsync(jobId, ct);
        var metrics = new ReportMetrics(Math.Round(time.ActiveSeconds / 3600, 2), Math.Round(time.IdleSeconds / 3600, 2), time.Sessions,
            time.ByStep.ToDictionary(s => s.Key, s => Math.Round(s.ActiveSeconds / 3600, 2)));

        return new ReportModel
        {
            Job = new ReportJob(job.JobId, job.Name, job.CustomerName, job.ProjectName, job.SiteAddress, job.OwnerEmail, job.State.ToString(), job.CompletionVersion, job.BaselineManualHours, job.CreatedUtc),
            Sheet = sheet, Rules = rules, Details = details, Metrics = metrics,
            ForCompletionVersion = job.State is JobState.Draft or JobState.InReview or JobState.Completing ? job.CompletionVersion + 1 : job.CompletionVersion
        };
    }

    /// <summary>A draft file drawn from the job as it is right now, for anyone who may see the job. It is stamped as a draft and is never stored.</summary>
    public async Task<(byte[] Bytes, string FileName, string ContentType)> GetDraftAsync(long jobId, string format, string requestedBy, CancellationToken ct = default)
    {
        var renderer = RendererFor(format);
        var model = await GetModelAsync(jobId, ct);
        var bytes = renderer.Render(model, new ReportStamp(requestedBy, DateTime.UtcNow, IsIssued: false));
        return (bytes, $"count-sheet-{jobId}-draft-{DateTime.UtcNow:yyyyMMdd-HHmm}.{renderer.Extension}", renderer.ContentType);
    }

    public async Task<IReadOnlyList<ReportRecord>> ListReportsAsync(long jobId, CancellationToken ct = default)
    {
        await using var c = await _open(ct);
        return await ReadAsync(c, null, "WHERE JobId = @j", [("@j", jobId)], ct);
    }

    /// <summary>An issued file, read back and checked against the hash recorded when it was issued.</summary>
    public async Task<(ReportRecord Report, byte[] Bytes, string ContentType)> GetStoredAsync(long jobId, Guid publicId, CancellationToken ct = default)
    {
        ReportRecord report;
        await using (var c = await _open(ct))
            report = (await ReadAsync(c, null, "WHERE JobId = @j AND PublicId = @p", [("@j", jobId), ("@p", publicId)], ct)).FirstOrDefault() ?? throw new KeyNotFoundException("That report does not exist.");

        await using var stream = await _store.OpenReadAsync(KeyOf(report), ct) ?? throw new KeyNotFoundException("That report file is missing from storage. Contact your administrator.");
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct);
        var bytes = memory.ToArray();
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), report.Sha256, StringComparison.OrdinalIgnoreCase)) throw new ReportIntegrityException();
        return (report, bytes, RendererFor(report.Format).ContentType);
    }

    // ------------------------------------------------------------------ issuing

    /// <summary>Makes and stores the issued reports for a job that is being completed. Safe to repeat after a failure: each run is a new, complete set.</summary>
    public async Task<IReadOnlyList<ReportRecord>> IssueAsync(long jobId, AuditContext context, CancellationToken ct = default)
    {
        context.EnsureValid();
        var model = await GetModelAsync(jobId, ct);
        var stamp = new ReportStamp(JobRepository.Actor(context), DateTime.UtcNow, IsIssued: true);
        var modelHash = model.ModelHash();

        return await JobTxn.RunAsync(_open, jobId, context, ct, async t =>
        {
            await t.RequireAsync(JobCapability.Complete);
            if (t.Job.State != JobState.Completing)
                throw new JobRuleViolationException($"Reports are issued while a job is being completed, not while it is {t.Job.State}.");
            if (t.Job.CompletionVersion + 1 != model.ForCompletionVersion)
                throw new JobRuleViolationException("The job changed while its reports were being made. Try again.");

            var written = new List<string>();
            var inserted = new List<long>();
            try
            {
                foreach (var renderer in _renderers)
                {
                    var bytes = renderer.Render(model, stamp);
                    var fileName = $"count-sheet-{jobId}-v{model.ForCompletionVersion}-{stamp.GeneratedUtc:yyyyMMdd}.{renderer.Extension}";
                    var id = Guid.NewGuid();
                    var key = $"reports/{jobId}/v{model.ForCompletionVersion}-{id.ToString("N")[..12]}.{renderer.Extension}";
                    var staging = $"reports/{id:D}";

                    await using (var data = new MemoryStream(bytes)) await _store.AppendStagingAsync(staging, 0, data, long.MaxValue, ct);
                    await _store.PromoteAsync(staging, key, ct);
                    written.Add(key);

                    inserted.Add(await ScalarAsync(t,
                        """
                        INSERT INTO ductwork.JobReport (PublicId, JobId, CompletionVersion, Format, FileName, StorageKey, Sha256, Bytes, ModelHash, IsFinal, GeneratedBy)
                        OUTPUT inserted.ReportId VALUES (@p, @j, @v, @f, @n, @k, @sha, @b, @mh, @fin, @by);
                        """, ("@p", id), ("@j", jobId), ("@v", model.ForCompletionVersion), ("@f", renderer.Format), ("@n", fileName), ("@k", key), ("@sha", SHA256.HashData(bytes)),
                        ("@b", (long)bytes.Length), ("@mh", modelHash), ("@fin", model.IsFinal), ("@by", stamp.GeneratedBy)));
                }

                await t.AuditAtCurrentVersionAsync(new AuditEntry("report", "report.issued",
                    $"Issued the count sheet for completion version {model.ForCompletionVersion} ({string.Join(", ", _renderers.Select(r => r.Format))}){(model.IsFinal ? "" : "; it is not final: " + string.Join(" ", model.NotFinalReasons))}",
                    "Report", null, null, JobRepository.Json(new() { ["version"] = model.ForCompletionVersion, ["final"] = model.IsFinal, ["modelHash"] = Convert.ToHexString(modelHash) })));
            }
            catch
            {
                foreach (var key in written) await _store.DeleteAsync(key, CancellationToken.None);
                throw;
            }

            return await ReadAsync(t.Connection, t.Transaction, $"WHERE ReportId IN ({string.Join(",", inserted)})", [], t.Token);
        }, requires: JobCapability.Complete);
    }

    // ------------------------------------------------------------------ helpers

    private IReportRenderer RendererFor(string format) =>
        _renderers.FirstOrDefault(r => string.Equals(r.Format, format, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"'{format}' is not a report format. Use one of: {string.Join(", ", _renderers.Select(r => r.Format))}.");

    private static string KeyOf(ReportRecord r) => $"reports/{r.JobId}/v{r.CompletionVersion}-{r.PublicId.ToString("N")[..12]}.{(r.Format)}";

    private static string Describe(RuleMatch m)
    {
        var parts = new List<string>();
        if (m.Type is not null) parts.Add($"type {m.Type}");
        if (m.Size is not null) parts.Add($"size {m.Size}");
        if (m.SizePattern is not null) parts.Add($"size like {m.SizePattern}");
        if (m.Tag is not null) parts.Add($"tag {m.Tag}");
        if (m.BuildingType is not null) parts.Add($"building type {m.BuildingType}");
        if (m.Method is not null) parts.Add($"found by {m.Method}");
        return parts.Count == 0 ? "everything" : string.Join(", ", parts);
    }

    private static string Describe(RuleAction a)
    {
        var parts = new List<string>();
        if (a.SetSize is not null) parts.Add($"set size to {a.SetSize}");
        if (a.SizeSuffix is not null) parts.Add($"add {a.SizeSuffix} to the size");
        if (a.SetType is not null) parts.Add($"set type to {a.SetType}");
        if (a.MultiplyQuantity is { } m) parts.Add($"multiply the quantity by {m}");
        return string.Join(", ", parts);
    }

    private static async Task<long> ScalarAsync(JobTxn t, string sql, params (string Name, object? Value)[] args)
    {
        await using var command = t.Connection.CreateCommand();
        command.Transaction = t.Transaction;
        command.CommandText = sql;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return (long)(await command.ExecuteScalarAsync(t.Token))!;
    }

    private static async Task<IReadOnlyList<ReportRecord>> ReadAsync(SqlConnection c, SqlTransaction? tx, string where, (string Name, object Value)[] args, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            $"SELECT ReportId, PublicId, JobId, CompletionVersion, Format, FileName, Sha256, Bytes, ModelHash, IsFinal, GeneratedBy, GeneratedUtc FROM ductwork.JobReport {where} ORDER BY ReportId DESC;";
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value);
        var list = new List<ReportRecord>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ReportRecord
            {
                ReportId = r.GetInt64(0), PublicId = r.GetGuid(1), JobId = r.GetInt64(2), CompletionVersion = r.GetInt32(3), Format = r.GetString(4), FileName = r.GetString(5),
                Sha256 = Convert.ToHexString((byte[])r.GetValue(6)), Bytes = r.GetInt64(7), ModelHash = Convert.ToHexString((byte[])r.GetValue(8)), IsFinal = r.GetBoolean(9),
                GeneratedBy = r.GetString(10), GeneratedUtc = DateTime.SpecifyKind(r.GetDateTime(11), DateTimeKind.Utc)
            });
        return list;
    }
}
