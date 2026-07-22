using AIToolbox.Web.Models;

namespace AIToolbox.Web.Data;

public interface IPromptRunRepository
{
    Task<int> CreatePromptRunAsync(
        string userEmail,
        string question,
        bool isPublic,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PromptRunSummary>> GetVisiblePromptRunsAsync(
        string userEmail,
        string scope,
        int maxRows = 100,
        CancellationToken cancellationToken = default);

    Task AddPromptResponsesAsync(int promptRunId, IEnumerable<AiToolCallResult> responses, CancellationToken cancellationToken = default);

    Task<PromptRun?> GetPromptRunAsync(int id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AnalysisRunSummary>> GetAnalysisRunSummariesAsync(
        int promptRunId,
        CancellationToken cancellationToken = default);

    Task<bool> UpdatePromptRunVisibilityAsync(
        int promptRunId,
        string userEmail,
        bool isPublic,
        CancellationToken cancellationToken = default);

    Task<int> CreateAnalysisRunAsync(int promptRunId, string userEmail, string analysisPrompt, CancellationToken cancellationToken = default);

    Task AddAnalysisResponsesAsync(int analysisRunId, IEnumerable<AiToolCallResult> responses, CancellationToken cancellationToken = default);

    Task<AnalysisRun?> GetAnalysisRunAsync(int id, CancellationToken cancellationToken = default);
}
