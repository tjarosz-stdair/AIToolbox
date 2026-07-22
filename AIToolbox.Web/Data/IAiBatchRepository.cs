using AIToolbox.Web.Models;

namespace AIToolbox.Web.Data;

public interface IAiBatchRepository
{
    Task<int> CreateJobAsync(AiBatchJob job, IReadOnlyList<AiBatchRequest> requests, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AiBatchJob>> GetJobsAsync(string? userEmail, int? consumerId, bool includeAll, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AiBatchJob>> GetPollableJobsAsync(CancellationToken cancellationToken = default);
    Task<AiBatchJob?> GetJobAsync(int id, string? userEmail, int? consumerId, bool includeAll, CancellationToken cancellationToken = default);
    Task<AiBatchJobDetail?> GetJobDetailAsync(int id, string? userEmail, int? consumerId, bool includeAll, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AiBatchRequest>> GetRequestsAsync(int jobId, CancellationToken cancellationToken = default);
    Task UpdateJobAsync(AiBatchJob job, CancellationToken cancellationToken = default);
    Task UpdateRequestAsync(AiBatchRequest request, CancellationToken cancellationToken = default);
    Task MarkJobCancelRequestedAsync(int id, CancellationToken cancellationToken = default);
}
