using System.Text.Json;
using AIToolbox.Web.Data;
using AIToolbox.Web.Models;

namespace AIToolbox.Web.Services;

public sealed class AiBatchService : IAiBatchService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly IAiBatchRepository _batches;
    private readonly IAiProviderRepository _providers;
    private readonly IAiBatchProviderClient _client;
    private readonly IEventLogRepository _events;
    private readonly ILogger<AiBatchService> _logger;

    public AiBatchService(
        IAiBatchRepository batches,
        IAiProviderRepository providers,
        IAiBatchProviderClient client,
        IEventLogRepository events,
        ILogger<AiBatchService> logger)
    {
        _batches = batches;
        _providers = providers;
        _client = client;
        _events = events;
        _logger = logger;
    }

    public async Task<CreateAiBatchJobResult> CreateAsync(
        CreateAiBatchJobRequest request,
        string userEmail,
        int? consumerId,
        CancellationToken cancellationToken = default)
    {
        if (request.Requests.Count == 0)
        {
            throw new InvalidOperationException("At least one batch request is required.");
        }

        var provider = await _providers.GetByIdAsync(request.AiProviderId, cancellationToken)
            ?? throw new InvalidOperationException("The selected provider does not exist.");
        if (!provider.IsEnabled)
        {
            throw new InvalidOperationException("The selected provider is disabled.");
        }

        if (provider.ProviderType is not (AiProviderType.OpenAiBatchResponses or AiProviderType.AnthropicMessageBatch))
        {
            throw new InvalidOperationException("The selected provider is not configured for batch processing.");
        }

        var normalizedRequests = request.Requests.Select((item, index) => new AiBatchRequest
        {
            CustomId = string.IsNullOrWhiteSpace(item.CustomId) ? $"request-{index + 1}" : item.CustomId.Trim(),
            SequenceNumber = index + 1,
            SourceObjectType = item.SourceObjectType,
            SourceObjectId = item.SourceObjectId,
            Status = AiBatchStatus.Queued,
            RequestJson = JsonSerializer.Serialize(item.Body, JsonOptions)
        }).ToList();

        if (normalizedRequests.Select(item => item.CustomId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalizedRequests.Count)
        {
            throw new InvalidOperationException("Batch request custom_id values must be unique within a job.");
        }

        var job = new AiBatchJob
        {
            ToolKey = string.IsNullOrWhiteSpace(request.ToolKey) ? "GenericBatch" : request.ToolKey.Trim(),
            ConsumerId = consumerId,
            UserEmail = userEmail,
            AiProviderId = request.AiProviderId,
            JobType = string.IsNullOrWhiteSpace(request.JobType) ? "BatchAnalysis" : request.JobType.Trim(),
            Status = AiBatchStatus.Preparing,
            InputMode = string.IsNullOrWhiteSpace(request.InputMode) ? "json" : request.InputMode.Trim(),
            RequestJson = JsonSerializer.Serialize(new
            {
                request.ToolKey,
                request.JobType,
                request.InputMode,
                request.Metadata,
                request.CallbackUrl
            }, JsonOptions),
            TotalRequestCount = normalizedRequests.Count,
            CallbackUrl = request.CallbackUrl
        };

        job.Id = await _batches.CreateJobAsync(job, normalizedRequests, cancellationToken);

        try
        {
            job.Status = AiBatchStatus.Submitted;
            job = await _client.SubmitAsync(provider, job, normalizedRequests, cancellationToken);
            await _batches.UpdateJobAsync(job, cancellationToken);
            await LogAiSubmissionAsync(job, provider, succeeded: true, errorMessage: null, cancellationToken);
            foreach (var batchRequest in normalizedRequests)
            {
                batchRequest.Status = AiBatchStatus.WaitingOnProvider;
                await _batches.UpdateRequestAsync(batchRequest, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            job.Status = AiBatchStatus.Failed;
            job.ErrorMessage = ex.Message;
            job.CompletedUtc = DateTime.UtcNow;
            await _batches.UpdateJobAsync(job, cancellationToken);
            await LogAiSubmissionAsync(job, provider, succeeded: false, ex.Message, cancellationToken);
            _logger.LogWarning(ex, "Failed to submit AI batch job {AiBatchJobId}.", job.Id);
            throw;
        }

        return new CreateAiBatchJobResult
        {
            Id = job.Id,
            Status = job.Status,
            TotalRequestCount = job.TotalRequestCount
        };
    }

    public async Task PollJobAsync(int jobId, CancellationToken cancellationToken = default)
    {
        var detail = await _batches.GetJobDetailAsync(jobId, null, null, includeAll: true, cancellationToken)
            ?? throw new InvalidOperationException("The batch job does not exist.");
        var provider = await _providers.GetByIdAsync(detail.Job.AiProviderId, cancellationToken)
            ?? throw new InvalidOperationException("The batch job provider does not exist.");

        if (string.Equals(detail.Job.Status, AiBatchStatus.CancelRequested, StringComparison.OrdinalIgnoreCase))
        {
            detail.Job = await _client.CancelAsync(provider, detail.Job, cancellationToken);
            await _batches.UpdateJobAsync(detail.Job, cancellationToken);
            return;
        }

        try
        {
            var result = await _client.PollAsync(provider, detail.Job, detail.Requests, cancellationToken);
            foreach (var request in result.UpdatedRequests)
            {
                await _batches.UpdateRequestAsync(request, cancellationToken);
            }

            await _batches.UpdateJobAsync(result.Job, cancellationToken);
        }
        catch (Exception ex)
        {
            detail.Job.LastPolledUtc = DateTime.UtcNow;
            detail.Job.ErrorMessage = ex.Message;
            await _batches.UpdateJobAsync(detail.Job, cancellationToken);
            _logger.LogWarning(ex, "Failed to poll AI batch job {AiBatchJobId}.", jobId);
        }
    }

    public async Task CancelAsync(
        int jobId,
        string? userEmail,
        int? consumerId,
        bool isAdmin,
        CancellationToken cancellationToken = default)
    {
        var job = await _batches.GetJobAsync(jobId, userEmail, consumerId, isAdmin, cancellationToken)
            ?? throw new InvalidOperationException("The batch job does not exist or is not visible.");

        await _batches.MarkJobCancelRequestedAsync(job.Id, cancellationToken);
        if (!string.IsNullOrWhiteSpace(job.ProviderBatchId))
        {
            await PollJobAsync(job.Id, cancellationToken);
        }
    }

    private async Task LogAiSubmissionAsync(
        AiBatchJob job,
        AiProvider provider,
        bool succeeded,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        await _events.RecordAsync(new EventLogEntry
        {
            EventType = EventLogTypes.AiSubmission,
            UserEmail = job.UserEmail,
            ToolKey = job.ToolKey,
            AiProviderId = provider.Id,
            AiProviderName = provider.DisplayName,
            Operation = job.JobType,
            DurationMs = null,
            Succeeded = succeeded,
            WasAllowed = true,
            FailureReason = errorMessage,
            MetadataJson = JsonSerializer.Serialize(new
            {
                aiBatchJobId = job.Id,
                providerType = provider.ProviderType.ToString(),
                model = provider.ModelName,
                requestCount = job.TotalRequestCount,
                providerBatchId = job.ProviderBatchId
            }, JsonOptions)
        }, cancellationToken);
    }
}
