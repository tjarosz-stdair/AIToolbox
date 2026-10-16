namespace AIToolbox.Ductwork.Core.Jobs;

/// <summary>The outcome of asking whether a lifecycle step is allowed. A refusal always says why, in words a user can act on.</summary>
public readonly record struct Decision(bool Allowed, string? Reason)
{
    public static Decision Yes { get; } = new(true, null);
    public static Decision No(string reason) => new(false, reason);
}

/// <summary>
/// The job state machine (plan section 8.2). Pure: it decides, the repository enforces and records.
///
///   Draft / InReview  -> Completing -> Complete
///   Draft / InReview  -> Closed
///   Complete / Closed -> Draft            (reopen, reason required)
///
/// Archiving, Archived and Restoring belong to a later slice and are never editable.
/// </summary>
public static class JobLifecycle
{
    /// <summary>Only a working job can be edited. Complete, Closed and everything after are read-only.</summary>
    public static bool IsEditable(JobState state) => state is JobState.Draft or JobState.InReview;

    public static Decision CanBeginComplete(JobState state, CompletionFacts facts)
    {
        if (!IsEditable(state))
            return Decision.No($"A job that is {state} cannot be completed.");
        if (facts.ActiveProcessingTasks > 0)
            return Decision.No($"{facts.ActiveProcessingTasks} processing task(s) are still running. Wait for them or cancel them first.");
        if (!facts.AllStepsDone)
            return Decision.No("Every workflow step (Setup, Process, Review and Report) must be done. To end the job without finishing them, close it instead.");
        if (facts.UnconfirmedMultipliers > 0)
            return Decision.No($"{facts.UnconfirmedMultipliers} building type(s) with counted items have a multiplier that has not been confirmed.");
        return Decision.Yes;
    }

    public static Decision CanFinishComplete(JobState state) =>
        state == JobState.Completing ? Decision.Yes : Decision.No($"Only a job that is Completing can be marked Complete, not one that is {state}.");

    public static Decision CanClose(JobState state, string? reason, CompletionFacts facts)
    {
        if (!IsEditable(state))
            return Decision.No($"A job that is {state} cannot be closed.");
        if (string.IsNullOrWhiteSpace(reason))
            return Decision.No("A reason is required to close a job.");
        if (facts.ActiveProcessingTasks > 0)
            return Decision.No($"{facts.ActiveProcessingTasks} processing task(s) are still running. Wait for them or cancel them first.");
        return Decision.Yes;
    }

    public static Decision CanReopen(JobState state, string? reason)
    {
        if (state is not (JobState.Complete or JobState.Closed))
            return state == JobState.Archived
                ? Decision.No("An archived job must be restored before it can be reopened.")
                : Decision.No($"Only a Complete or Closed job can be reopened, not one that is {state}.");
        if (string.IsNullOrWhiteSpace(reason))
            return Decision.No("A reason is required to reopen a job.");
        return Decision.Yes;
    }

    /// <summary>
    /// Header metadata that is not job content: tags, members and the baseline can change on a Complete,
    /// Closed or Archived job without reopening it. Only a job in the middle of completing or being
    /// archived or restored is held still, so those operations see a stable job.
    /// </summary>
    public static bool AllowsMetadataChange(JobState state) =>
        state is JobState.Draft or JobState.InReview or JobState.Complete or JobState.Closed or JobState.Archived;
}
