using AIToolbox.Web.Hubs;
using AIToolbox.Web.Models;
using Microsoft.AspNetCore.SignalR;

namespace AIToolbox.Web.Services.CallEntityExtraction;

public sealed class CallActivityBroadcaster : ICallActivityBroadcaster
{
    private readonly IHubContext<CallMonitorHub> _hubContext;
    private readonly ILogger<CallActivityBroadcaster> _logger;

    public CallActivityBroadcaster(IHubContext<CallMonitorHub> hubContext, ILogger<CallActivityBroadcaster> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public Task ActiveCallStartedAsync(string callId, string agentKey) =>
        SendAsync(() => _hubContext.Clients.All.SendAsync("ActiveCallStarted", new { callId, agentKey }), callId);

    public Task CallEndedAsync(string callId) =>
        SendAsync(() => _hubContext.Clients.All.SendAsync("CallEnded", new { callId }), callId);

    public Task TranscriptLineAppendedAsync(string callId, string agentKey, TranscriptLine line) =>
        SendAsync(() => _hubContext.Clients.Group(CallMonitorHub.GroupForCall(callId)).SendAsync(
            "TranscriptLineAppended",
            new
            {
                callId,
                agentKey,
                line.Side,
                line.Text,
                line.UtteranceId,
                line.Sequence,
                line.CapturedAtUtc
            }),
            callId);

    public Task EntityDetectedAsync(EntityDetectedNotification notification) =>
        SendAsync(() => _hubContext.Clients.Group(CallMonitorHub.GroupForCall(notification.CallId))
            .SendAsync("EntityDetected", notification), notification.CallId);

    public Task DispositionRecordedAsync(CallEventDispositionRequest disposition) =>
        SendAsync(() => _hubContext.Clients.Group(CallMonitorHub.GroupForCall(disposition.CallId))
            .SendAsync("DispositionRecorded", disposition), disposition.CallId);

    private async Task SendAsync(Func<Task> send, string callId)
    {
        try
        {
            await send();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast call activity to the Live Call Monitor for call {CallId}.", callId);
        }
    }
}
