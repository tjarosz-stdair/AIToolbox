using AIToolbox.Web.Models;
using AIToolbox.Web.Security;
using AIToolbox.Web.Services.CallEntityExtraction;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers.Api;

/// <summary>
/// Listener -> us. Only isFinal segments feed extraction -- interim segments are accepted (for a
/// possible future live-caption UI) but are noise for identifier spotting. Idempotent per
/// (callId, utteranceId, sequence) is the Listener's responsibility to make retry-safe on their
/// side; this endpoint doesn't reject duplicates itself since a duplicate append is harmless
/// (the window's dedup-on-canonical-value happens downstream, not here).
/// </summary>
[ApiController]
[Authorize(Policy = "ApiConsumer")]
[Route("api/v1/call-transcripts")]
public sealed class CallTranscriptIngestController : ControllerBase
{
    private readonly ICallTranscriptWindowStore _windows;
    private readonly ICallEntityExtractionQueue _queue;
    private readonly ICallActivityBroadcaster _broadcaster;
    private readonly ILogger<CallTranscriptIngestController> _logger;

    public CallTranscriptIngestController(
        ICallTranscriptWindowStore windows,
        ICallEntityExtractionQueue queue,
        ICallActivityBroadcaster broadcaster,
        ILogger<CallTranscriptIngestController> logger)
    {
        _windows = windows;
        _queue = queue;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    [HttpPost("segments")]
    public async Task<IActionResult> PostSegment([FromBody] TranscriptSegmentRequest request, CancellationToken cancellationToken)
    {
        if (!User.IsApiConsumer() || !User.HasApiScope("calltranscripts:write"))
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.CallId) || string.IsNullOrWhiteSpace(request.Text))
        {
            return BadRequest(new { error = "callId and text are required." });
        }

        if (!request.IsFinal)
        {
            return Accepted();
        }

        var utteranceId = $"{request.SegmentId}:{request.Sequence}";
        var window = _windows.GetOrCreate(request.CallId, out var wasCreated);
        window.AppendFinalSegment(request.Text, utteranceId, request.Side, request.AgentKey, request.Sequence, request.CapturedAtUtc);

        if (wasCreated)
        {
            await _broadcaster.ActiveCallStartedAsync(request.CallId, window.AgentKey);
        }

        await _broadcaster.TranscriptLineAppendedAsync(
            request.CallId,
            window.AgentKey,
            new TranscriptLine(utteranceId, request.Side, request.Text, request.Sequence, request.CapturedAtUtc));

        try
        {
            await _queue.QueueAsync(request.CallId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never let a queueing failure fail the Listener's POST -- the segment is already
            // durably appended to the window; worst case this segment's candidates are picked up
            // on the next segment's signal instead of immediately.
            _logger.LogError(ex, "Failed to queue call {CallId} for entity extraction.", request.CallId);
        }

        return Accepted();
    }
}
