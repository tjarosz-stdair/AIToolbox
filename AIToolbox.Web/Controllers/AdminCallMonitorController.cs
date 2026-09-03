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

    public AdminCallMonitorController(ICallTranscriptWindowStore windows)
    {
        _windows = windows;
    }

    [HttpGet("")]
    public IActionResult Index()
    {
        return View(_windows.ListActive());
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
}
