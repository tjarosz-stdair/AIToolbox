using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIToolbox.Ductwork.Core.Counting;

namespace AIToolbox.Ductwork.Core.Reports;

public sealed record ReportJob(
    long JobId, string Name, string? CustomerName, string? ProjectName, string? SiteAddress, string OwnerEmail, string State,
    int CompletionVersion, decimal? BaselineManualHours, DateTime CreatedUtc);

/// <summary>An approved or proposed rule that bears on this job, so the sheet can say how the counts were read.</summary>
public sealed record ReportRule(string Name, string Scope, string Status, int Version, string? Description, string Match, string Action, string? ApprovedBy, DateTime? ApprovedUtc);

/// <summary>One item as it stands, with the evidence for it. The detail list behind the count sheet.</summary>
public sealed record ReportDetail(
    string BuildingType, string Type, string? Category, string? Tag, string? Size, string? Manufacturer, string? Model, string? CustomerPartCode,
    decimal Quantity, string Unit, string Method, double Confidence, string ReviewStatus, string Disposition, string ResolutionSource, bool IsStale,
    string? DispositionReason, string? Evidence, string? ViewRef);

/// <summary>Job-level time, never per person. Who spent how long is for Job Managers and administrators, not for a report that gets passed around.</summary>
public sealed record ReportMetrics(double ActiveHours, double IdleHours, int Sessions, IReadOnlyDictionary<string, double> ActiveHoursByStep);

/// <summary>
/// What every report is drawn from (plan section 12): one neutral model of the job's effective state. The screen, the Excel file and the
/// HTML file all read this, so they cannot disagree, and <see cref="ModelHash"/> proves two files came from the same state.
/// </summary>
public sealed record ReportModel
{
    public required ReportJob Job { get; init; }
    public required CountSheet Sheet { get; init; }
    public required IReadOnlyList<ReportRule> Rules { get; init; }
    public required IReadOnlyList<ReportDetail> Details { get; init; }
    public required ReportMetrics Metrics { get; init; }

    /// <summary>The job's version this report belongs to: the one it becomes when it is completed, or the current one if it already is.</summary>
    public int ForCompletionVersion { get; init; }

    public bool IsFinal => Sheet.IsFinal;

    /// <summary>Why the sheet is not final, in plain words. Empty when it is.</summary>
    public IReadOnlyList<string> NotFinalReasons
    {
        get
        {
            var reasons = new List<string>();
            if (Sheet.UnconfirmedBuildingTypes.Count > 0) reasons.Add($"{Sheet.UnconfirmedBuildingTypes.Count} building type(s) have no confirmed multiplier: {string.Join(", ", Sheet.UnconfirmedBuildingTypes)}.");
            if (Sheet.OpenItems.Count > 0) reasons.Add($"{Sheet.OpenItems.Count} item(s) cannot be counted yet (see Holds).");
            if (Sheet.OpenFindings.Count > 0) reasons.Add($"{Sheet.OpenFindings.Count} question(s) are unanswered and could change a count.");
            var proposed = Sheet.Totals.Sum(t => t.ProposedCount);
            if (proposed > 0) reasons.Add($"{proposed} counted item(s) have not been verified by a person.");
            if (Sheet.Totals.Any(t => !t.IsComplete) && Sheet.UnconfirmedBuildingTypes.Count == 0) reasons.Add("Some totals are still to be determined.");
            return reasons;
        }
    }

    private static readonly JsonSerializerOptions HashOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>A hash of the content, in a fixed form, that does not depend on when it was made or who asked.</summary>
    public byte[] ModelHash() => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Job, Sheet, Rules, Details, Metrics, ForCompletionVersion }, HashOptions));
}

/// <summary>A renderer turns the model into a file. It reads the model and nothing else: no database, no clock beyond the stamp it is given.</summary>
public interface IReportRenderer
{
    string Format { get; }
    string ContentType { get; }
    string Extension { get; }
    byte[] Render(ReportModel model, ReportStamp stamp);
}

/// <summary>Who produced the file and when. Kept apart from the model so it can never change what a count says.</summary>
public sealed record ReportStamp(string GeneratedBy, DateTime GeneratedUtc, bool IsIssued);
