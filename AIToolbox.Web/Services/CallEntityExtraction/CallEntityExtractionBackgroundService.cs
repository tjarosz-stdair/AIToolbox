using AIToolbox.Web.Models;
using Serilog.Context;

namespace AIToolbox.Web.Services.CallEntityExtraction;

/// <summary>
/// Dequeues "this call's window changed" signals, re-extracts candidates from current window
/// state, validates newly-seen ones against P21, and notifies the Call Data API of any that
/// validate. Mirrors ProductParsingBackgroundService's dequeue-and-process shape.
/// </summary>
public sealed class CallEntityExtractionBackgroundService : BackgroundService
{
    private readonly ICallEntityExtractionQueue _queue;
    private readonly ICallTranscriptWindowStore _windows;
    private readonly IIdentifierCandidateExtractor _extractor;
    private readonly IEntityActionCatalog _actionCatalog;
    private readonly ICallActivityBroadcaster _broadcaster;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CallEntityExtractionBackgroundService> _logger;

    public CallEntityExtractionBackgroundService(
        ICallEntityExtractionQueue queue,
        ICallTranscriptWindowStore windows,
        IIdentifierCandidateExtractor extractor,
        IEntityActionCatalog actionCatalog,
        ICallActivityBroadcaster broadcaster,
        IServiceScopeFactory scopeFactory,
        ILogger<CallEntityExtractionBackgroundService> logger)
    {
        _queue = queue;
        _windows = windows;
        _extractor = extractor;
        _actionCatalog = actionCatalog;
        _broadcaster = broadcaster;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            string callId;
            try
            {
                callId = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            using var callScope = LogContext.PushProperty("CallId", callId);
            try
            {
                await ProcessCallAsync(callId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Call entity extraction failed for call {CallId}.", callId);
            }
        }
    }

    private async Task ProcessCallAsync(string callId, CancellationToken cancellationToken)
    {
        var window = _windows.GetOrCreate(callId);
        var candidates = _extractor.ExtractCandidates(window.CurrentText());
        if (candidates.Count == 0)
        {
            return;
        }

        // IP21IdentifierValidationClient/ICallEventNotifier depend on scoped services, so they
        // can't be injected directly into this singleton hosted service -- resolve them from a
        // fresh scope per dequeued call, same as ProductParsingBackgroundService does.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var validationClient = scope.ServiceProvider.GetRequiredService<IP21IdentifierValidationClient>();
        var notifier = scope.ServiceProvider.GetRequiredService<ICallEventNotifier>();

        foreach (var candidate in candidates)
        {
            if (!window.TryMarkEmitted(candidate.CanonicalValue))
            {
                continue;
            }

            var validation = await validationClient.ValidateAsync(candidate, callId, cancellationToken);
            if (validation is null)
            {
                _logger.LogDebug(
                    "Identifier candidate not validated (suppressed): {CanonicalValue} (raw {RawSpokenText}, type {TypeGuess}).",
                    candidate.CanonicalValue,
                    candidate.RawSpokenText,
                    candidate.TypeGuess);
                continue;
            }

            _logger.LogInformation(
                "Identifier candidate validated: {CanonicalValue} (raw {RawSpokenText}, type {TypeGuess}, validation status {ValidationStatus}, source {Source}). Context: {ContextSnippet}",
                candidate.CanonicalValue,
                candidate.RawSpokenText,
                candidate.TypeGuess,
                validation.Status,
                validation.Source,
                candidate.ContextSnippet);

            var notification = BuildNotification(callId, window, candidate, validation);
            await notifier.NotifyEntityDetectedAsync(notification, cancellationToken);
            await _broadcaster.EntityDetectedAsync(notification);
        }
    }

    private EntityDetectedNotification BuildNotification(
        string callId,
        CallTranscriptWindow window,
        IdentifierCandidate candidate,
        ValidationResult validation)
    {
        return new EntityDetectedNotification
        {
            CallId = callId,
            EntityId = $"ent-{Guid.NewGuid():N}",
            UtteranceId = window.LastUtteranceId,
            DetectedAtUtc = DateTime.UtcNow,
            EntityType = ToWireEntityType(validation.DetectedEntityType),
            CanonicalValue = candidate.CanonicalValue,
            RawSpokenText = candidate.RawSpokenText,
            Confidence = candidate.Confidence,
            Validation = new EntityDetectedValidation
            {
                Status = ToWireStatus(validation.Status),
                CandidateValues = validation.CandidateValues,
                Source = validation.Source
            },
            SourceUtterance = new EntityDetectedSourceUtterance
            {
                Text = candidate.ContextSnippet,
                OffsetStart = candidate.ContextOffsetStart,
                OffsetEnd = candidate.ContextOffsetStart + candidate.ContextOffsetLength,
                Side = window.LastSide
            },
            Actions = _actionCatalog.GetActions(validation.DetectedEntityType)
        };
    }

    private static string ToWireEntityType(DetectedEntityType entityType)
    {
        return entityType switch
        {
            DetectedEntityType.SerialNumber => "serial_number",
            DetectedEntityType.PartNumber => "part_number",
            _ => "model_number"
        };
    }

    private static string ToWireStatus(ValidationStatus status)
    {
        return status switch
        {
            ValidationStatus.Validated => "validated",
            ValidationStatus.Ambiguous => "ambiguous",
            ValidationStatus.NotFoundInWindow => "not_found_in_window",
            _ => status.ToString()
        };
    }
}
