using AIToolbox.Ductwork.Contracts;

namespace AIToolbox.Ductwork.Core.Jobs;

public enum JobState { Draft, InReview, Completing, Complete, Closed, Archiving, Archived, Restoring }

public enum JobRole { Viewer, Editor, Owner }

public enum StepStatus { NotStarted, Active, Done }

public enum ActorType { User, System, Worker, Llm }

/// <summary>What someone is trying to do to a job. Roles map to capabilities in <see cref="JobPermissions"/>.</summary>
public enum JobCapability
{
    View,
    ReadAudit,
    Edit,
    Complete,
    Close,
    Reopen,
    Tag,
    ManageMembers,
    Archive,
    Restore
}

public sealed record JobRecord
{
    public long JobId { get; init; }
    public Guid PublicId { get; init; }
    public required string Name { get; init; }
    public string? CustomerName { get; init; }
    public string? ProjectName { get; init; }
    public string? SiteAddress { get; init; }
    public required string OwnerEmail { get; init; }
    public JobState State { get; init; }
    public JobState? StateBeforeArchive { get; init; }
    public int CompletionVersion { get; init; }
    public long StateVersion { get; init; }
    public ReviewLevel ReviewLevel { get; init; }
    public decimal? BaselineManualHours { get; init; }
    public DateTime? CompletedUtc { get; init; }
    public string? CompletedBy { get; init; }
    public DateTime? ClosedUtc { get; init; }
    public string? ClosedBy { get; init; }
    public string? CloseReason { get; init; }
    public DateTime? ReopenedUtc { get; init; }
    public string? ReopenedBy { get; init; }
    public string? ReopenReason { get; init; }
    public DateTime CreatedUtc { get; init; }
    public required string CreatedBy { get; init; }
    public DateTime UpdatedUtc { get; init; }
}

public sealed record NewJob
{
    public required string Name { get; init; }
    public string? CustomerName { get; init; }
    public string? ProjectName { get; init; }
    public string? SiteAddress { get; init; }
    public ReviewLevel ReviewLevel { get; init; } = ReviewLevel.Sensitive;
}

/// <summary>The editable header fields. A null leaves a field unchanged; use <see cref="Clear"/> to blank one.</summary>
public sealed record JobDetails
{
    public string? Name { get; init; }
    public string? CustomerName { get; init; }
    public string? ProjectName { get; init; }
    public string? SiteAddress { get; init; }

    /// <summary>Optional header fields to set to nothing.</summary>
    public IReadOnlySet<string> Clear { get; init; } = new HashSet<string>();
}

public sealed record JobMemberRecord(long JobId, string UserEmail, JobRole Role, DateTime AddedUtc, string AddedBy);

public sealed record JobListFilter
{
    public IReadOnlyList<JobState>? States { get; init; }
    public string? OwnerEmail { get; init; }
    /// <summary>A job must carry every one of these tags.</summary>
    public IReadOnlyList<string>? Tags { get; init; }
    /// <summary>Only jobs this person is a member of.</summary>
    public string? MemberEmail { get; init; }
    public int Limit { get; init; } = 200;
}

/// <summary>Facts about a job that the lifecycle checks when work is finished. Tables for some of them arrive in later slices.</summary>
public sealed record CompletionFacts(bool AllStepsDone, int ActiveProcessingTasks, int UnconfirmedMultipliers);

public interface ICompletionProbe
{
    Task<(int ActiveProcessingTasks, int UnconfirmedMultipliers)> GetAsync(
        Microsoft.Data.SqlClient.SqlConnection connection, Microsoft.Data.SqlClient.SqlTransaction transaction, long jobId,
        CancellationToken cancellationToken);
}

/// <summary>Used until processing tasks and building types exist: nothing is active and nothing is unconfirmed.</summary>
public sealed class NoDetailCompletionProbe : ICompletionProbe
{
    public Task<(int ActiveProcessingTasks, int UnconfirmedMultipliers)> GetAsync(
        Microsoft.Data.SqlClient.SqlConnection connection, Microsoft.Data.SqlClient.SqlTransaction transaction, long jobId,
        CancellationToken cancellationToken) => Task.FromResult((0, 0));
}

public sealed class JobRuleViolationException(string message) : InvalidOperationException(message);

public sealed class JobAccessDeniedException(string message) : UnauthorizedAccessException(message);

public sealed class JobConcurrencyException(long expected, long actual)
    : InvalidOperationException($"The job changed while you were working (version {expected} expected, {actual} found). Reload and try again.");

public sealed class JobNotFoundException(long jobId) : KeyNotFoundException($"Job {jobId} does not exist.");
