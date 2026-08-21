using Microsoft.Extensions.AI;

namespace AIToolbox.Web.Services;

public interface IUiCapabilityProvider
{
    /// <summary>
    /// Fetches (or returns cached) UI capability manifests for each of the given UI surfaces and
    /// returns them as local AIFunctions, one HTTP call per registered surface (§5.4). A fetch
    /// failure for one surface never throws — it logs and contributes no tools for that surface,
    /// so the conversation degrades to MCP-only rather than failing outright.
    /// </summary>
    Task<IReadOnlyList<AITool>> GetToolsAsync(
        string mcpCredentialSurface,
        IReadOnlyList<string> uiSurfaces,
        CancellationToken cancellationToken = default);
}
