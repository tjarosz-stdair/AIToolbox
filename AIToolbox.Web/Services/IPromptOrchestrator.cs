using AIToolbox.Web.Models;

namespace AIToolbox.Web.Services;

public interface IPromptOrchestrator
{
    Task<int> SubmitQuestionAsync(
        string userEmail,
        string question,
        bool isPublic,
        IEnumerable<int> providerIds,
        CancellationToken cancellationToken = default);

    Task<int> SubmitQuestionWithProgressAsync(
        string userEmail,
        string question,
        bool isPublic,
        IEnumerable<int> providerIds,
        Func<AiToolCallResult, Task> onProviderCompleted,
        CancellationToken cancellationToken = default);

    Task<int> AnalyzeResponsesAsync(
        string userEmail,
        int promptRunId,
        IEnumerable<int> providerIds,
        CancellationToken cancellationToken = default);
}
