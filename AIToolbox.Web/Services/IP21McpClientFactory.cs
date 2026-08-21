using ModelContextProtocol.Client;

namespace AIToolbox.Web.Services;

public interface IP21McpClientFactory
{
    /// <summary>
    /// Opens an MCP client connection to P21 Toolbox's tools server for the given surface,
    /// forwarding both the machine-client OAuth bearer token and (when present) the caller's
    /// validated X-P21-Session-Token on every underlying call, including tools/list. The caller
    /// owns disposing the returned client once the chat turn/tool-call sequence is done.
    /// </summary>
    Task<McpClient> ConnectAsync(string surface, string? sessionToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the tools this connection's OAuth client is allowlisted for and stamps every one
    /// with <paramref name="conversationId"/> as _meta.sessionId, so every subsequent tools/call
    /// made through Microsoft.Extensions.AI's function-invocation loop carries a consistent
    /// session id — P21 Toolbox keys its call budget and audit trail on (tenant, sub, session_id),
    /// so an inconsistent value would silently break both. Never hardcode a tool list here: the
    /// live response IS the per-surface allowlist P21 Toolbox enforces server-side.
    /// </summary>
    Task<IReadOnlyList<McpClientTool>> GetToolsAsync(McpClient client, string conversationId, CancellationToken cancellationToken = default);
}
