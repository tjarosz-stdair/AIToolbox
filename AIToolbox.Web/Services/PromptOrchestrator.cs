using System.Text;
using System.Text.Json;
using AIToolbox.Web.Data;
using AIToolbox.Web.Models;
using Serilog.Context;

namespace AIToolbox.Web.Services;

public sealed class PromptOrchestrator : IPromptOrchestrator
{
    private readonly IAiProviderRepository _providers;
    private readonly IPromptRunRepository _runs;
    private readonly IAiProviderClient _client;
    private readonly IPlatformRepository _platform;
    private readonly IEventLogRepository _events;
    private readonly ILogger<PromptOrchestrator> _logger;

    public PromptOrchestrator(
        IAiProviderRepository providers,
        IPromptRunRepository runs,
        IAiProviderClient client,
        IPlatformRepository platform,
        IEventLogRepository events,
        ILogger<PromptOrchestrator> logger)
    {
        _providers = providers;
        _runs = runs;
        _client = client;
        _platform = platform;
        _events = events;
        _logger = logger;
    }

    public async Task<int> SubmitQuestionAsync(
        string userEmail,
        string question,
        bool isPublic,
        IEnumerable<int> providerIds,
        CancellationToken cancellationToken = default)
    {
        using var toolScope = LogContext.PushProperty("ToolKey", "AICompare");
        using var userScope = LogContext.PushProperty("UserEmail", userEmail);
        var selectedProviders = await _providers.GetEnabledByIdsAsync(providerIds, cancellationToken);
        if (selectedProviders.Count == 0)
        {
            throw new InvalidOperationException("Select at least one enabled AI tool.");
        }

        var promptRunId = await _runs.CreatePromptRunAsync(userEmail, question, isPublic, cancellationToken);
        _logger.LogInformation(
            "AI Compare prompt run {PromptRunId} started with {ProviderCount} providers. IsPublic={IsPublic}; QuestionLength={QuestionLength}.",
            promptRunId,
            selectedProviders.Count,
            isPublic,
            question.Length);

        var responseTasks = selectedProviders
            .Select(provider => _client.SendPromptAsync(provider, question, cancellationToken))
            .ToArray();
        var responses = await Task.WhenAll(responseTasks);
        await _runs.AddPromptResponsesAsync(promptRunId, responses, cancellationToken);
        await LogResponsesAsync(userEmail, "AICompare", "Prompt", selectedProviders, responses, cancellationToken);
        _logger.LogInformation(
            "AI Compare prompt run {PromptRunId} completed. Succeeded={SucceededCount}; Failed={FailedCount}.",
            promptRunId,
            responses.Count(response => string.IsNullOrWhiteSpace(response.ErrorMessage)),
            responses.Count(response => !string.IsNullOrWhiteSpace(response.ErrorMessage)));

        return promptRunId;
    }

    public async Task<int> SubmitQuestionWithProgressAsync(
        string userEmail,
        string question,
        bool isPublic,
        IEnumerable<int> providerIds,
        Func<AiToolCallResult, Task> onProviderCompleted,
        CancellationToken cancellationToken = default)
    {
        using var toolScope = LogContext.PushProperty("ToolKey", "AICompare");
        using var userScope = LogContext.PushProperty("UserEmail", userEmail);
        var selectedProviders = await _providers.GetEnabledByIdsAsync(providerIds, cancellationToken);
        if (selectedProviders.Count == 0)
        {
            throw new InvalidOperationException("Select at least one enabled AI tool.");
        }

        var promptRunId = await _runs.CreatePromptRunAsync(userEmail, question, isPublic, cancellationToken);
        _logger.LogInformation(
            "AI Compare streaming prompt run {PromptRunId} started with {ProviderCount} providers. IsPublic={IsPublic}; QuestionLength={QuestionLength}.",
            promptRunId,
            selectedProviders.Count,
            isPublic,
            question.Length);

        var runningTasks = selectedProviders
            .Select(provider => _client.SendPromptAsync(provider, question, cancellationToken))
            .ToList();

        while (runningTasks.Count > 0)
        {
            var completedTask = await Task.WhenAny(runningTasks);
            runningTasks.Remove(completedTask);

            var response = await completedTask;
            await _runs.AddPromptResponsesAsync(promptRunId, new[] { response }, cancellationToken);
            await LogResponsesAsync(userEmail, "AICompare", "Prompt", selectedProviders, new[] { response }, cancellationToken);
            _logger.LogInformation(
                "AI Compare provider {ProviderId} completed for prompt run {PromptRunId}. Succeeded={Succeeded}; DurationMs={DurationMs}; TokensIn={TokensIn}; TokensOut={TokensOut}.",
                response.ProviderId,
                promptRunId,
                string.IsNullOrWhiteSpace(response.ErrorMessage),
                response.DurationMs,
                response.InputTokens,
                response.OutputTokens);
            await onProviderCompleted(response);
        }

        _logger.LogInformation("AI Compare streaming prompt run {PromptRunId} completed.", promptRunId);
        return promptRunId;
    }

    public async Task<int> AnalyzeResponsesAsync(
        string userEmail,
        int promptRunId,
        IEnumerable<int> providerIds,
        CancellationToken cancellationToken = default)
    {
        using var toolScope = LogContext.PushProperty("ToolKey", "AICompare");
        using var userScope = LogContext.PushProperty("UserEmail", userEmail);
        var promptRun = await _runs.GetPromptRunAsync(promptRunId, cancellationToken)
            ?? throw new InvalidOperationException("The prompt run was not found.");
        var selectedProviders = await _providers.GetEnabledByIdsAsync(providerIds, cancellationToken);
        if (selectedProviders.Count == 0)
        {
            throw new InvalidOperationException("Select at least one enabled AI tool for analysis.");
        }

        var analysisPrompt = BuildAnalysisPrompt(promptRun.Question, promptRun.Responses);
        var analysisRunId = await _runs.CreateAnalysisRunAsync(
            promptRunId,
            userEmail,
            analysisPrompt,
            cancellationToken);
        _logger.LogInformation(
            "AI Compare analysis run {AnalysisRunId} started for prompt run {PromptRunId} with {ProviderCount} providers.",
            analysisRunId,
            promptRunId,
            selectedProviders.Count);

        var responseTasks = selectedProviders
            .Select(provider => _client.SendPromptAsync(provider, analysisPrompt, cancellationToken))
            .ToArray();
        var responses = await Task.WhenAll(responseTasks);
        await _runs.AddAnalysisResponsesAsync(analysisRunId, responses, cancellationToken);
        await LogResponsesAsync(userEmail, "AICompare", "Analysis", selectedProviders, responses, cancellationToken);
        _logger.LogInformation(
            "AI Compare analysis run {AnalysisRunId} completed. Succeeded={SucceededCount}; Failed={FailedCount}.",
            analysisRunId,
            responses.Count(response => string.IsNullOrWhiteSpace(response.ErrorMessage)),
            responses.Count(response => !string.IsNullOrWhiteSpace(response.ErrorMessage)));

        return analysisRunId;
    }

    private async Task LogResponsesAsync(
        string userEmail,
        string toolKey,
        string requestType,
        IReadOnlyList<AiProvider> providers,
        IEnumerable<AiToolCallResult> responses,
        CancellationToken cancellationToken)
    {
        foreach (var response in responses)
        {
            var provider = providers.FirstOrDefault(item => item.Id == response.ProviderId);
            await _platform.LogUsageAsync(new ToolUsageLog
            {
                UserEmail = userEmail,
                ToolKey = toolKey,
                AiProviderId = response.ProviderId,
                RequestType = requestType,
                TokensIn = response.InputTokens,
                TokensOut = response.OutputTokens,
                DurationMs = response.DurationMs,
                Succeeded = string.IsNullOrWhiteSpace(response.ErrorMessage),
                ErrorMessage = response.ErrorMessage,
                EstimatedCostUsd = provider is null
                    ? null
                    : await _platform.EstimateCostAsync(provider, response.InputTokens, response.OutputTokens, cancellationToken)
            }, cancellationToken);
            await _events.RecordAsync(new EventLogEntry
            {
                EventType = EventLogTypes.AiSubmission,
                UserEmail = userEmail,
                ToolKey = toolKey,
                AiProviderId = response.ProviderId,
                AiProviderName = response.ProviderName ?? provider?.DisplayName,
                Operation = requestType,
                DurationMs = response.DurationMs,
                Succeeded = string.IsNullOrWhiteSpace(response.ErrorMessage),
                WasAllowed = true,
                FailureReason = response.ErrorMessage,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    inputTokens = response.InputTokens,
                    outputTokens = response.OutputTokens,
                    finishReason = response.FinishReason
                })
            }, cancellationToken);

            if (string.IsNullOrWhiteSpace(response.ErrorMessage))
            {
                _logger.LogInformation(
                    "{ToolKey} {RequestType} provider {ProviderId} succeeded in {DurationMs} ms. TokensIn={TokensIn}; TokensOut={TokensOut}.",
                    toolKey,
                    requestType,
                    response.ProviderId,
                    response.DurationMs,
                    response.InputTokens,
                    response.OutputTokens);
            }
            else
            {
                _logger.LogWarning(
                    "{ToolKey} {RequestType} provider {ProviderId} failed in {DurationMs} ms. Error={ErrorMessage}",
                    toolKey,
                    requestType,
                    response.ProviderId,
                    response.DurationMs,
                    response.ErrorMessage);
            }
        }
    }

    private static string BuildAnalysisPrompt(string question, IEnumerable<PromptResponse> responses)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Compare multiple AI tool responses to the exact same question for an IT staff audience.");
        builder.AppendLine("Evaluate accuracy, completeness, contradictions, assumptions, security or operational risks, and practical usefulness.");
        builder.AppendLine("Finish with a concise recommendation of which response is strongest and why.");
        builder.AppendLine();
        builder.AppendLine("Original question:");
        builder.AppendLine(question);
        builder.AppendLine();
        builder.AppendLine("Responses:");

        foreach (var response in responses)
        {
            builder.AppendLine();
            builder.AppendLine($"[{response.ProviderName}]");
            if (!string.IsNullOrWhiteSpace(response.ErrorMessage))
            {
                builder.AppendLine($"This provider failed: {response.ErrorMessage}");
            }
            else
            {
                builder.AppendLine(response.ResponseText);
            }
        }

        return builder.ToString();
    }
}
