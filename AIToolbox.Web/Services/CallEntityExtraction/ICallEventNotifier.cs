using AIToolbox.Web.Models;

namespace AIToolbox.Web.Services.CallEntityExtraction;

/// <summary>Sends a validated entity detection to the Call Data API. Never throws -- a downstream
/// outage shouldn't crash the extraction pipeline, per AIToolbox/TODO_Lists/call-data-api-contract.md.</summary>
public interface ICallEventNotifier
{
    Task NotifyEntityDetectedAsync(EntityDetectedNotification notification, CancellationToken cancellationToken = default);
}
