using AIToolbox.Web.Models;

namespace AIToolbox.Web.Services;

public interface IAiBatchProviderClient
{
    Task<AiBatchJob> SubmitAsync(AiProvider provider, AiBatchJob job, IReadOnlyList<AiBatchRequest> requests, CancellationToken cancellationToken = default);
    Task<AiBatchPollResult> PollAsync(AiProvider provider, AiBatchJob job, IReadOnlyList<AiBatchRequest> requests, CancellationToken cancellationToken = default);
    Task<AiBatchJob> CancelAsync(AiProvider provider, AiBatchJob job, CancellationToken cancellationToken = default);
}

public sealed class AiBatchPollResult
{
    public required AiBatchJob Job { get; set; }
    public IReadOnlyList<AiBatchRequest> UpdatedRequests { get; set; } = [];
}
