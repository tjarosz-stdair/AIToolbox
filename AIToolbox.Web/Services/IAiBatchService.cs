using AIToolbox.Web.Models;

namespace AIToolbox.Web.Services;

public interface IAiBatchService
{
    Task<CreateAiBatchJobResult> CreateAsync(CreateAiBatchJobRequest request, string userEmail, int? consumerId, CancellationToken cancellationToken = default);
    Task PollJobAsync(int jobId, CancellationToken cancellationToken = default);
    Task CancelAsync(int jobId, string? userEmail, int? consumerId, bool isAdmin, CancellationToken cancellationToken = default);
}
