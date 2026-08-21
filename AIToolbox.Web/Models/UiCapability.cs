using System.Text.Json;

namespace AIToolbox.Web.Models;

/// <summary>
/// One entry from a P21 Toolbox GET /api/ui-capabilities/{surface} manifest response. "UiSurface"
/// here is the UI-navigable document (orderentry, picsearch) that published it — a different
/// dimension from McpClientCredential.Surface, which selects the chat-embedding scenario's OAuth
/// credential. Deliberately named differently everywhere so the two are never conflated.
/// </summary>
public sealed class UiCapability
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    /// <summary>"navigation" or "mutation" — only "navigation" is ever registered in v1; a
    /// "mutation" entry is refused outright, no directive covers its confirmation flow yet.</summary>
    public required string Kind { get; init; }

    public required JsonElement InputSchema { get; init; }

    public required string UiSurface { get; init; }
}
