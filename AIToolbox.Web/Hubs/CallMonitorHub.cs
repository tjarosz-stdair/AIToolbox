using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AIToolbox.Web.Hubs;

/// <summary>
/// Live feed for the admin "Live Call Monitor" -- lets an authorized admin watch a call's
/// transcript and AI-detected entity prompts in real time, in place of impersonating an agent's
/// Caller Tray session (no such impersonation mechanism exists anywhere in this system; this is
/// new capability, not a replacement for existing code).
///
/// AdminOnly-gated the same way as every other admin page, so this reuses the existing
/// Entra/M365 cookie-auth admin login rather than a separate auth scheme -- SignalR's default
/// browser transport carries the same auth cookie automatically.
///
/// Active-call list changes (ActiveCallStarted/CallEnded, broadcast by CallActivityBroadcaster)
/// go to every connected admin (Clients.All) since only admins can connect at all; per-call
/// transcript lines and entity detections are scoped to a group per call so watching one call
/// doesn't flood an admin with every other call's traffic.
/// </summary>
[Authorize(Policy = "AdminOnly")]
public sealed class CallMonitorHub : Hub
{
    public static string GroupForCall(string callId) => $"call:{callId}";

    public Task JoinCall(string callId) => Groups.AddToGroupAsync(Context.ConnectionId, GroupForCall(callId));

    public Task LeaveCall(string callId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupForCall(callId));
}
