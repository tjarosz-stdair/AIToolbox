using AIToolbox.Web.Services.CallEntityExtraction;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers;

/// <summary>
/// Real-time view of live RingCX calls' transcripts and AI-detected entity prompts, for an admin
/// to observe without needing to impersonate an agent's Caller Tray session (no such
/// impersonation mechanism exists anywhere in this system today -- see CallMonitorHub's doc).
/// </summary>
[Authorize(Policy = "AdminOnly")]
[Route("Admin/CallMonitor")]
public sealed class AdminCallMonitorController : Controller
{
    private readonly ICallTranscriptWindowStore _windows;
    private readonly CallDataApiAdminClient _adminClient;

    public AdminCallMonitorController(ICallTranscriptWindowStore windows, CallDataApiAdminClient adminClient)
    {
        _windows = windows;
        _adminClient = adminClient;
    }

    [HttpGet("")]
    public IActionResult Index()
    {
        return View(_windows.ListActive());
    }

    /// <summary>
    /// Same data as Index()'s server-rendered model, as JSON -- lets the client re-sync the
    /// active-calls list after a SignalR reconnect (any ActiveCallStarted/CallEnded broadcast that
    /// fired while disconnected is otherwise lost for good; SignalR doesn't replay missed events).
    /// </summary>
    [HttpGet("active-calls")]
    public IActionResult GetActiveCalls()
    {
        return Json(_windows.ListActive());
    }

    /// <summary>
    /// Initial snapshot for a call the admin just selected -- SignalR only delivers events from
    /// the moment of joining the group forward, so this fills in whatever already happened.
    /// </summary>
    [HttpGet("{callId}/snapshot")]
    public IActionResult GetSnapshot(string callId)
    {
        var window = _windows.GetOrCreate(callId);

        return Json(new
        {
            callId = window.CallId,
            agentKey = window.AgentKey,
            lines = window.RecentLines()
        });
    }

    /// <summary>
    /// Backs the "AI Prompt Testing Overrides" panel -- lets an admin exercise the ingestion
    /// and/or delivery stages for an agent whose real opt-in is off, without touching that
    /// agent's real experience on live calls. See RingCXAgentHubAPI/docs/AIToolboxContract.md
    /// §2.4 for the Hub-side contract these calls implement.
    /// </summary>
    [HttpGet("overrides")]
    public async Task<IActionResult> GetOverrides(string agentKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(agentKey))
        {
            return BadRequest("agentKey is required.");
        }

        var settings = await _adminClient.GetAgentAiSettingsAsync(agentKey, cancellationToken);
        if (settings is null)
        {
            return StatusCode(StatusCodes.Status502BadGateway, "Unable to reach the Call Data API.");
        }

        return Json(settings);
    }

    /// <summary>
    /// Backs the "active overrides" side panel -- every agent currently running under an
    /// ingestion and/or delivery override, so you don't need to already know an agentKey to see
    /// what testing state is live.
    /// </summary>
    [HttpGet("overrides/active")]
    public async Task<IActionResult> GetAllActiveOverrides(CancellationToken cancellationToken)
    {
        var overrides = await _adminClient.GetActiveOverridesAsync(cancellationToken);
        return overrides is null
            ? StatusCode(StatusCodes.Status502BadGateway, "Unable to reach the Call Data API.")
            : Json(overrides);
    }

    [HttpPost("overrides/ingestion")]
    public async Task<IActionResult> SetIngestionOverride([FromForm] string agentKey, [FromForm] int expiresInMinutes, CancellationToken cancellationToken)
    {
        var ok = await _adminClient.SetIngestionOverrideAsync(agentKey, expiresInMinutes, cancellationToken);
        return await AfterOverrideChangeAsync(agentKey, ok, cancellationToken);
    }

    [HttpPost("overrides/ingestion/clear")]
    public async Task<IActionResult> ClearIngestionOverride([FromForm] string agentKey, CancellationToken cancellationToken)
    {
        var ok = await _adminClient.ClearIngestionOverrideAsync(agentKey, cancellationToken);
        return await AfterOverrideChangeAsync(agentKey, ok, cancellationToken);
    }

    [HttpPost("overrides/delivery")]
    public async Task<IActionResult> SetDeliveryOverride(
        [FromForm] string agentKey, [FromForm] int expiresInMinutes, [FromForm] string? mirrorToAgentKey, CancellationToken cancellationToken)
    {
        var ok = await _adminClient.SetDeliveryOverrideAsync(agentKey, expiresInMinutes, mirrorToAgentKey, cancellationToken);
        return await AfterOverrideChangeAsync(agentKey, ok, cancellationToken);
    }

    [HttpPost("overrides/delivery/clear")]
    public async Task<IActionResult> ClearDeliveryOverride([FromForm] string agentKey, CancellationToken cancellationToken)
    {
        var ok = await _adminClient.ClearDeliveryOverrideAsync(agentKey, cancellationToken);
        return await AfterOverrideChangeAsync(agentKey, ok, cancellationToken);
    }

    // The Hub's set/clear-override endpoints return only the one override's own fields, not the
    // full picture -- always re-fetch afterward so the panel's status line (both overrides plus
    // the real ShowAiPrompts flag) reflects reality rather than a partial response.
    private async Task<IActionResult> AfterOverrideChangeAsync(string agentKey, bool changeSucceeded, CancellationToken cancellationToken)
    {
        if (!changeSucceeded)
        {
            return StatusCode(StatusCodes.Status502BadGateway, "Unable to reach the Call Data API.");
        }

        var settings = await _adminClient.GetAgentAiSettingsAsync(agentKey, cancellationToken);
        return settings is null
            ? StatusCode(StatusCodes.Status502BadGateway, "Unable to reach the Call Data API.")
            : Json(settings);
    }
}
