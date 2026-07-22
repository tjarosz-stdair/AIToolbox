using AIToolbox.Web.Models;

namespace AIToolbox.Web.Data;

public interface IEventLogRepository
{
    Task RecordAsync(EventLogEntry entry, CancellationToken cancellationToken = default);

    Task<UserLoginEventResult> GetUserLoginEventsAsync(
        UserLoginEventFilter filter,
        CancellationToken cancellationToken = default);

    Task<AiSubmissionEventResult> GetAiSubmissionEventsAsync(
        AiSubmissionEventFilter filter,
        CancellationToken cancellationToken = default);
}
