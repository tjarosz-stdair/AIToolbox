namespace AIToolbox.Ductwork.Core.Jobs;

/// <summary>
/// Who may do what (plan section 8.4). Pure: it only answers the question, and the repository asks it
/// on every mutating call, so the rule holds whatever the UI shows.
///
/// A Job Manager is a Ductwork-only permission that can archive and restore any job. It grants no view
/// or edit access to a job's contents. An AIToolbox administrator likewise sees no job contents unless a
/// member; the administrator's powers (granting Job Manager, storage, forced takeover) are outside the
/// per-job capabilities.
/// </summary>
public static class JobPermissions
{
    public static bool Can(JobRole? membership, bool isJobManager, JobCapability capability)
    {
        if (isJobManager && capability is JobCapability.Archive or JobCapability.Restore)
            return true;

        return membership switch
        {
            JobRole.Owner => true,
            JobRole.Editor => capability is JobCapability.View or JobCapability.ReadAudit or JobCapability.Edit
                or JobCapability.Complete or JobCapability.Close or JobCapability.Reopen or JobCapability.Tag,
            JobRole.Viewer => capability is JobCapability.View or JobCapability.ReadAudit,
            _ => false
        };
    }

    /// <summary>"Edit permission" in the plan means Owner or Editor.</summary>
    public static bool HasEditPermission(JobRole? membership) => membership is JobRole.Owner or JobRole.Editor;
}
