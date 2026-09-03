namespace AIToolbox.Web.Services.CallEntityExtraction;

/// <summary>
/// Drops per-call transcript window state once a call's gone quiet — there's no explicit "call
/// ended" signal from the Listener contract, so idle timeout is how state gets reclaimed. Mirrors
/// ConversationRetentionPurgeService's PeriodicTimer shape.
/// </summary>
public sealed class CallTranscriptWindowIdleCleanupService : BackgroundService
{
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    private readonly ICallTranscriptWindowStore _windows;
    private readonly ICallActivityBroadcaster _broadcaster;
    private readonly ILogger<CallTranscriptWindowIdleCleanupService> _logger;

    public CallTranscriptWindowIdleCleanupService(
        ICallTranscriptWindowStore windows,
        ICallActivityBroadcaster broadcaster,
        ILogger<CallTranscriptWindowIdleCleanupService> logger)
    {
        _windows = windows;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var removed = _windows.RemoveIdle(IdleThreshold);
                if (removed.Count > 0)
                {
                    _logger.LogInformation("Dropped {Count} idle call transcript windows.", removed.Count);

                    foreach (var callId in removed)
                    {
                        await _broadcaster.CallEndedAsync(callId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Call transcript window idle cleanup cycle failed.");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
